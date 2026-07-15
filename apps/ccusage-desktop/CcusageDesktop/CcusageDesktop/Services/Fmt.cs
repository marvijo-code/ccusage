using Windows.UI;

namespace CcusageDesktop.Services;

/// <summary>Number/date formatting and model-family classification.</summary>
public static class Fmt
{
    public static string Cost(double v) =>
        v >= 10_000 ? "$" + v.ToString("N0")
        : v >= 100 ? "$" + v.ToString("N1")
        : "$" + v.ToString("N2");

    public static string Cost2(double v) => "$" + v.ToString("N2");

    public static string Tokens(long v) => v switch
    {
        >= 1_000_000_000 => (v / 1_000_000_000d).ToString("0.##") + "B",
        >= 1_000_000 => (v / 1_000_000d).ToString("0.#") + "M",
        >= 1_000 => (v / 1_000d).ToString("0.#") + "K",
        _ => v.ToString(),
    };

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t.ToLocalTime();
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m" : $"{d.Minutes}m";

    /// <summary>Maps a ccusage model name to a display family + accent color.</summary>
    public static (string Family, Color Color) ModelFamily(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("claude")) return ("Claude", C(0xD9, 0x77, 0x57));
        if (m.Contains("gpt") || m.Contains("codex")) return ("GPT / Codex", C(0x19, 0xC3, 0x7D));
        if (m.Contains("gemini")) return ("Gemini", C(0x4C, 0x8D, 0xF6));
        if (m.Contains("glm")) return ("GLM", C(0x8B, 0x74, 0xF9));
        if (m.Contains("kimi")) return ("Kimi", C(0x2E, 0xB9, 0xC7));
        if (m.Contains("qwen")) return ("Qwen", C(0xB4, 0x5C, 0xE0));
        if (m.Contains("minimax")) return ("MiniMax", C(0xE8, 0x53, 0x6E));
        if (m.Contains("deepseek")) return ("DeepSeek", C(0x53, 0x6D, 0xFE));
        if (m.Contains("mimo")) return ("MiMo", C(0xF2, 0x9D, 0x38));
        return ("Other", C(0x8B, 0x94, 0x9E));
    }

    public static (string Label, Color Color) AgentInfo(string agent) => agent.ToLowerInvariant() switch
    {
        "claude" => ("Claude Code", C(0xD9, 0x77, 0x57)),
        "codex" => ("Codex", C(0x19, 0xC3, 0x7D)),
        "opencode" => ("OpenCode", C(0xF2, 0x9D, 0x38)),
        "openclaw" => ("OpenClaw", C(0xE8, 0x53, 0x6E)),
        "gemini" => ("Gemini CLI", C(0x4C, 0x8D, 0xF6)),
        "copilot" => ("Copilot CLI", C(0x8B, 0x74, 0xF9)),
        var a => (char.ToUpperInvariant(a[0]) + a[1..], C(0x8B, 0x94, 0x9E)),
    };

    static Color C(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
}
