using System.Net.Http;
using System.Text.Json;
using CcusageDesktop.Models;

namespace CcusageDesktop.Services;

/// <summary>One rate-limit window for one harness. Percent is 0–100.</summary>
public sealed record HarnessLimit(
    string Harness,
    string Window,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    bool IsProxy,
    DateTimeOffset? AsOf,
    string? Note = null);

/// <summary>
/// Gathers %-to-limit numbers per harness:
/// - Codex: real numbers from the rate_limits snapshots Codex writes into its rollout JSONLs.
/// - Claude: real numbers via the OAuth usage endpoint when ~/.claude/.credentials.json holds a
///   valid token; otherwise a proxy (current 5h block / week vs the personal historical max),
///   flagged IsProxy so the UI can label it.
/// </summary>
public sealed class LimitsService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <param name="agentDaily">Per-harness daily reports (from `ccusage &lt;agent&gt; daily`) — one proxy row per harness.</param>
    public async Task<List<HarnessLimit>> GetAsync(
        BlocksReport? blocks,
        PeriodReport? weekly,
        IReadOnlyDictionary<string, AgentReport>? agentDaily = null)
    {
        var result = new List<HarnessLimit>();
        try { result.AddRange(await GetClaudeAsync(blocks, weekly, agentDaily)); } catch { }
        try
        {
            var codex = GetCodex();
            if (codex.Count == 0 && agentDaily?.GetValueOrDefault("codex") is { } cd && ProxyWeekly("codex", cd) is { } cp)
                codex.Add(cp); // no fresh rollout snapshot — fall back to the personal-max proxy
            result.AddRange(codex);
        }
        catch { }

        // Every other harness ccusage has seen gets a vs-personal-max weekly row.
        if (agentDaily is not null)
        {
            foreach (var (agent, report) in agentDaily.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (agent is "claude" or "codex") continue;
                try { if (ProxyWeekly(agent, report) is { } row) result.Add(row); } catch { }
            }
        }
        return result;
    }

    /// <summary>
    /// Current week's tokens vs the heaviest completed week for that harness, aggregated from
    /// daily rows into Monday-anchored weeks (matching the top-level weekly report).
    /// </summary>
    static HarnessLimit? ProxyWeekly(string agent, AgentReport report)
    {
        var weeks = new Dictionary<DateTime, long>();
        foreach (var row in report.Rows)
        {
            if (!DateTime.TryParse(row.Day, out var day)) continue;
            var monday = day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
            weeks[monday] = weeks.GetValueOrDefault(monday) + row.TotalTokens;
        }
        if (weeks.Count == 0) return null;

        var thisMonday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
        var used = weeks.GetValueOrDefault(thisMonday);
        var history = weeks.Where(kv => kv.Key != thisMonday).Select(kv => kv.Value).ToList();
        var max = history.Count > 0 ? history.Max() : used;
        if (max <= 0) return null;
        return new HarnessLimit(
            agent, "Weekly",
            Math.Min(100, used * 100.0 / max),
            new DateTimeOffset(thisMonday.AddDays(7)), IsProxy: true, AsOf: null);
    }

    // ── Claude ───────────────────────────────────────────────────────────────

    async Task<List<HarnessLimit>> GetClaudeAsync(
        BlocksReport? blocks, PeriodReport? weekly, IReadOnlyDictionary<string, AgentReport>? agentDaily)
    {
        // Best source: the official rate_limits Claude Code hands to the statusline hook,
        // persisted by ~/.claude/statusline-command.sh. Between snapshots, usage keeps moving —
        // so we self-calibrate a $-per-percent limit from consecutive snapshots and extrapolate
        // the drift from local cost data (ccusage blocks).
        var claudeDaily = agentDaily?.GetValueOrDefault("claude");
        var snap = ReadClaudeSnapshot();
        if (snap is not null)
        {
            var real = EstimateFromSnapshot(snap, blocks, claudeDaily);
            if (real.Count > 0) return real;
        }
        var oauth = await TryClaudeOauthAsync();
        if (oauth.Count > 0) return oauth;
        // Prefer the claude-only daily report for the weekly proxy; fall back to filtering the combined one.
        if (claudeDaily is not null)
        {
            var list = ClaudeProxy(blocks, weekly: null);
            if (ProxyWeekly("claude", claudeDaily) is { } weeklyRow) list.Add(weeklyRow);
            return list;
        }
        return ClaudeProxy(blocks, weekly);
    }

    // ── Claude snapshot + drift estimation ───────────────────────────────────

    sealed class ClaudeSnapshot
    {
        public DateTimeOffset At { get; set; }
        public Dictionary<string, double> Pct { get; set; } = [];
        public Dictionary<string, DateTimeOffset?> Resets { get; set; } = [];
    }

    sealed class ClaudeCalibration
    {
        public Dictionary<string, double> LimitUsd { get; set; } = [];
        public ClaudeSnapshot? LastSnap { get; set; }
    }

    static string StatePath(string file) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ccusage-desktop", file);

    /// <summary>Reads the rate_limits snapshot captured from Claude Code's statusline payload.</summary>
    static ClaudeSnapshot? ReadClaudeSnapshot()
    {
        try
        {
            var path = StatePath("claude-rate-limits.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return null;

            var snap = new ClaudeSnapshot { At = File.GetLastWriteTime(path) };
            if (root.TryGetProperty("captured_at", out var cap) && DateTimeOffset.TryParse(cap.GetString(), out var t))
                snap.At = t.ToLocalTime();
            if (DateTimeOffset.Now - snap.At > TimeSpan.FromDays(7)) return null; // ancient — fall back entirely

            foreach (var prop in limits.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                double? pct = null;
                if (prop.Value.TryGetProperty("used_percentage", out var up) && up.ValueKind == JsonValueKind.Number) pct = up.GetDouble();
                else if (prop.Value.TryGetProperty("utilization", out var ut) && ut.ValueKind == JsonValueKind.Number) pct = ut.GetDouble();
                if (pct is null) continue;
                snap.Pct[prop.Name] = pct.Value;
                snap.Resets[prop.Name] = ParseResetsAt(prop.Value);
            }
            return snap.Pct.Count > 0 ? snap : null;
        }
        catch
        {
            return null;
        }
    }

    List<HarnessLimit> EstimateFromSnapshot(ClaudeSnapshot snap, BlocksReport? blocks, AgentReport? claudeDaily)
    {
        var calib = LoadCalibration();
        UpdateCalibration(calib, snap, blocks, claudeDaily);

        var now = DateTimeOffset.Now;
        var age = now - snap.At;
        var rows = new List<HarnessLimit>();

        foreach (var (key, snapPct) in snap.Pct)
        {
            var resets = snap.Resets.GetValueOrDefault(key);
            var limit = calib.LimitUsd.GetValueOrDefault(key);
            double pct;
            string? note;

            if (age < TimeSpan.FromMinutes(10))
            {
                pct = snapPct;
                note = null; // fresh official number
            }
            else if (resets is { } r && r <= now)
            {
                // The window rolled over since the snapshot — usage restarts from the reset.
                var windowStart = r;
                if (key == "five_hour")
                {
                    // New session starts at first activity after the old window expired.
                    var firstActivity = blocks?.Blocks
                        .Where(b => !b.IsGap && b.CostUsd > 0)
                        .Select(b => (DateTimeOffset?)b.StartTime)
                        .FirstOrDefault(s => s > r);
                    if (firstActivity is null && BlocksCostBetween(blocks, r, now) <= 0.01)
                    {
                        rows.Add(new HarnessLimit("claude", WindowLabelFromKey(key), 0, null, IsProxy: false, AsOf: snap.At, Note: "est"));
                        continue;
                    }
                    windowStart = firstActivity > r ? firstActivity.Value : r;
                    resets = windowStart.AddHours(5);
                }
                else
                {
                    while (windowStart.AddDays(7) < now) windowStart = windowStart.AddDays(7);
                    resets = windowStart.AddDays(7);
                }
                var cost = CostForKey(key, windowStart, now, blocks, claudeDaily);
                pct = limit > 0 ? cost / limit * 100 : 0;
                note = "est";
            }
            else
            {
                var drift = limit > 0 ? CostForKey(key, snap.At, now, blocks, claudeDaily) / limit * 100 : 0;
                pct = snapPct + drift;
                note = age > TimeSpan.FromMinutes(30) ? "est" : null;
            }

            rows.Add(new HarnessLimit("claude", WindowLabelFromKey(key), Math.Clamp(pct, 0, 100),
                resets, IsProxy: false, AsOf: snap.At, Note: note));
        }
        return rows;
    }

    static ClaudeCalibration LoadCalibration()
    {
        try
        {
            var path = StatePath("claude-calibration.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ClaudeCalibration>(File.ReadAllText(path), UsageJson.Options) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>
    /// Learns $-cost limits per window. Pair method: cost burned between two snapshots divided by
    /// the % it moved. Bootstrap (no prior snapshot): whole-window cost divided by the absolute %.
    /// </summary>
    static void UpdateCalibration(ClaudeCalibration calib, ClaudeSnapshot snap, BlocksReport? blocks, AgentReport? claudeDaily)
    {
        try
        {
            var last = calib.LastSnap;
            var changed = false;

            foreach (var (key, pct) in snap.Pct)
            {
                // Pair-based estimate — most accurate, needs the same window in both snapshots.
                if (last is not null && snap.At - last.At > TimeSpan.FromMinutes(5)
                    && last.Pct.TryGetValue(key, out var prevPct)
                    && snap.Resets.GetValueOrDefault(key) is { } r1 && last.Resets.GetValueOrDefault(key) is { } r0
                    && (r1 - r0).Duration() < TimeSpan.FromMinutes(5)
                    && pct - prevPct >= 2)
                {
                    var cost = CostForKey(key, last.At, snap.At, blocks, claudeDaily);
                    if (cost > 0.5)
                    {
                        var estimate = cost / ((pct - prevPct) / 100.0);
                        var blended = calib.LimitUsd.TryGetValue(key, out var prev) ? (prev + estimate) / 2 : estimate;
                        calib.LimitUsd[key] = Math.Max(blended, LimitFloor(key, blocks, claudeDaily));
                        changed = true;
                    }
                }
                // Bootstrap from the whole current window.
                else if (!calib.LimitUsd.ContainsKey(key) && pct >= 2 && snap.Resets.GetValueOrDefault(key) is { } resets)
                {
                    var windowStart = key == "five_hour" ? resets.AddHours(-5) : resets.AddDays(-7);
                    var cost = CostForKey(key, windowStart, snap.At, blocks, claudeDaily);
                    if (cost > 0.5)
                    {
                        calib.LimitUsd[key] = Math.Max(cost / (pct / 100.0), LimitFloor(key, blocks, claudeDaily));
                        changed = true;
                    }
                }
            }

            if (last is null || snap.At != last.At)
            {
                calib.LastSnap = snap;
                changed = true;
            }
            if (changed)
                File.WriteAllText(StatePath("claude-calibration.json"), JsonSerializer.Serialize(calib, UsageJson.Options));
        }
        catch { }
    }

    /// <summary>
    /// A hard lower bound on a window's $ limit: the heaviest window ever completed without
    /// hitting 100%. Guards against noisy calibration when a snapshot lands early in a window.
    /// </summary>
    static double LimitFloor(string key, BlocksReport? blocks, AgentReport? claudeDaily)
    {
        if (key == "five_hour")
            return blocks?.Blocks.Where(b => !b.IsGap).Select(b => b.CostUsd).DefaultIfEmpty(0).Max() ?? 0;
        if (key == "seven_day" && claudeDaily is not null)
        {
            var weeks = new Dictionary<DateTime, double>();
            foreach (var row in claudeDaily.Rows)
            {
                if (!DateTime.TryParse(row.Day, out var day)) continue;
                var monday = day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
                weeks[monday] = weeks.GetValueOrDefault(monday) + row.Cost;
            }
            return weeks.Count > 0 ? weeks.Values.Max() : 0;
        }
        return 0;
    }

    /// <summary>Claude-harness cost in [from, to] — per-model windows get the model's share of each day.</summary>
    static double CostForKey(string key, DateTimeOffset from, DateTimeOffset to, BlocksReport? blocks, AgentReport? claudeDaily)
    {
        if (!key.StartsWith("seven_day_", StringComparison.Ordinal) || claudeDaily is null)
            return BlocksCostBetween(blocks, from, to);

        // Model-specific window (e.g. seven_day_fable): weight each day's cost by that model's share.
        var needle = key[10..];
        double sum = 0;
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var dayStart = new DateTimeOffset(day, from.Offset);
            var overlap = BlocksCostBetween(blocks, Max(dayStart, from), Min(dayStart.AddDays(1), to));
            if (overlap <= 0) continue;
            var row = claudeDaily.Rows.FirstOrDefault(r => r.Day == day.ToString("yyyy-MM-dd"));
            var breakdowns = row?.ModelBreakdowns;
            if (breakdowns is { Count: > 0 })
            {
                var total = breakdowns.Sum(b => b.Cost);
                var model = breakdowns.Where(b => b.ModelName.Contains(needle, StringComparison.OrdinalIgnoreCase)).Sum(b => b.Cost);
                sum += total > 0 ? overlap * (model / total) : 0;
            }
            else
            {
                sum += overlap; // no breakdown — assume it's all this model rather than dropping it
            }
        }
        return sum;
    }

    static double BlocksCostBetween(BlocksReport? blocks, DateTimeOffset from, DateTimeOffset to)
    {
        if (blocks is null || to <= from) return 0;
        double sum = 0;
        foreach (var b in blocks.Blocks)
        {
            if (b.IsGap || b.CostUsd <= 0) continue;
            var start = b.StartTime;
            var end = b.ActualEndTime ?? (b.IsActive ? DateTimeOffset.Now : b.EndTime);
            if (end <= start) continue;
            var os = Max(start, from);
            var oe = Min(end, to);
            if (oe <= os) continue;
            sum += b.CostUsd * ((oe - os).TotalMinutes / (end - start).TotalMinutes);
        }
        return sum;
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    static DateTimeOffset? ParseResetsAt(JsonElement window)
    {
        if (!window.TryGetProperty("resets_at", out var r)) return null;
        if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var iso)) return iso;
        if (r.ValueKind == JsonValueKind.Number)
        {
            var n = r.GetInt64();
            // Heuristic: unix ms vs unix seconds.
            return n > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
        }
        return null;
    }

    static async Task<List<HarnessLimit>> TryClaudeOauthAsync()
    {
        var list = new List<HarnessLimit>();
        try
        {
            var credPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(credPath)) return list;
            using var creds = JsonDocument.Parse(await File.ReadAllTextAsync(credPath));
            if (!creds.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return list;
            var token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            var expiresAt = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
            if (string.IsNullOrWhiteSpace(token)) return list;
            if (expiresAt > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) < DateTimeOffset.UtcNow.AddMinutes(1)) return list;

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return list;

            // Shape-agnostic parse: any object property carrying "utilization" is a limit window.
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (!prop.Value.TryGetProperty("utilization", out var util) || util.ValueKind != JsonValueKind.Number) continue;
                DateTimeOffset? resets = null;
                if (prop.Value.TryGetProperty("resets_at", out var r))
                {
                    if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var iso)) resets = iso;
                    else if (r.ValueKind == JsonValueKind.Number) resets = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64());
                }
                list.Add(new HarnessLimit("claude", WindowLabelFromKey(prop.Name), util.GetDouble(), resets, IsProxy: false, AsOf: DateTimeOffset.Now));
            }
        }
        catch
        {
            list.Clear();
        }
        return list;
    }

    static string WindowLabelFromKey(string key) => key switch
    {
        "five_hour" => "5-hour",
        "seven_day" => "Weekly",
        "seven_day_oauth_apps" => "Weekly · Apps",
        _ when key.StartsWith("seven_day_", StringComparison.Ordinal) =>
            "Weekly · " + char.ToUpperInvariant(key[10..][0]) + key[11..].Replace('_', ' '),
        _ => key.Replace('_', ' '),
    };

    static List<HarnessLimit> ClaudeProxy(BlocksReport? blocks, PeriodReport? weekly)
    {
        var list = new List<HarnessLimit>();

        // 5-hour: active block tokens vs heaviest historical block.
        var real = blocks?.Blocks.Where(b => !b.IsGap && b.TotalTokens > 0).ToList();
        if (real is { Count: > 0 })
        {
            var max = real.Max(b => b.TotalTokens);
            var active = real.LastOrDefault(b => b.IsActive);
            if (max > 0)
            {
                list.Add(new HarnessLimit(
                    "claude", "5-hour",
                    active is null ? 0 : Math.Min(100, active.TotalTokens * 100.0 / max),
                    active?.EndTime, IsProxy: true, AsOf: null));
            }
        }

        // Weekly: this week's Claude-model tokens vs heaviest historical Claude week.
        var rows = weekly?.Rows;
        if (rows is { Count: > 0 })
        {
            static long ClaudeTokens(UsageRow r) => r.ModelBreakdowns
                .Where(b => b.ModelName.Contains("claude", StringComparison.OrdinalIgnoreCase))
                .Sum(b => b.InputTokens + b.OutputTokens + b.CacheCreationTokens + b.CacheReadTokens);

            var current = rows[^1];
            var isCurrentWeek = DateTime.TryParse(current.Period, out var ws)
                && ws <= DateTime.Today && DateTime.Today < ws.AddDays(7);
            var history = rows.Take(rows.Count - (isCurrentWeek ? 1 : 0)).Select(ClaudeTokens).ToList();
            var max = history.Count > 0 ? history.Max() : 0;
            if (max > 0)
            {
                var used = isCurrentWeek ? ClaudeTokens(current) : 0;
                var nextMonday = DateTime.Today.AddDays(((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7 - 1) % 7 + 1);
                list.Add(new HarnessLimit(
                    "claude", "Weekly",
                    Math.Min(100, used * 100.0 / max),
                    new DateTimeOffset(nextMonday), IsProxy: true, AsOf: null));
            }
        }

        return list;
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    static List<HarnessLimit> GetCodex()
    {
        var list = new List<HarnessLimit>();
        var sessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(sessionsDir)) return list;

        var files = new DirectoryInfo(sessionsDir)
            .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(8);

        foreach (var file in files)
        {
            var line = LastLineContaining(file.FullName, "\"rate_limits\"");
            if (line is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                DateTimeOffset? asOf = root.TryGetProperty("timestamp", out var ts)
                    && DateTimeOffset.TryParse(ts.GetString(), out var parsed) ? parsed.ToLocalTime() : file.LastWriteTime;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                if (!payload.TryGetProperty("rate_limits", out var limits)
                    && !(payload.TryGetProperty("info", out var info) && info.TryGetProperty("rate_limits", out limits))) continue;

                foreach (var name in new[] { "primary", "secondary" })
                {
                    if (!limits.TryGetProperty(name, out var win) || win.ValueKind != JsonValueKind.Object) continue;
                    if (!win.TryGetProperty("used_percent", out var pct) || pct.ValueKind != JsonValueKind.Number) continue;
                    var minutes = win.TryGetProperty("window_minutes", out var wm) && wm.ValueKind == JsonValueKind.Number ? wm.GetInt32() : 0;
                    DateTimeOffset? resets = win.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).ToLocalTime() : null;
                    list.Add(new HarnessLimit("codex", WindowLabelFromMinutes(minutes), pct.GetDouble(), resets, IsProxy: false, AsOf: asOf));
                }
                if (list.Count > 0) return list;
            }
            catch
            {
                // Malformed tail line — try the next file.
            }
        }
        return list;
    }

    static string WindowLabelFromMinutes(int minutes) => minutes switch
    {
        <= 0 => "limit",
        <= 360 => "5-hour",
        >= 9000 and <= 11000 => "Weekly",
        _ => $"{minutes / 60}-hour",
    };

    /// <summary>Reads the last complete line containing <paramref name="needle"/> from up to the final 512 KB of a file.</summary>
    static string? LastLineContaining(string path, string needle)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int maxTail = 512 * 1024;
        var take = (int)Math.Min(fs.Length, maxTail);
        fs.Seek(-take, SeekOrigin.End);
        var buf = new byte[take];
        var read = fs.Read(buf, 0, take);
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        var lines = text.Split('\n');
        // Skip index 0 when we started mid-file: it is almost certainly a partial line.
        var start = take == fs.Length ? 0 : 1;
        for (var i = lines.Length - 1; i >= start; i--)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
                return lines[i].Trim();
        }
        return null;
    }
}
