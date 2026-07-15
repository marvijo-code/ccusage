using System;
using System.Collections.Generic;
using System.Linq;
using CcusageDesktop.Models;
using CcusageDesktop.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CcusageDesktop;

public sealed partial class MainPage : Page
{
    readonly UsageDataService _svc = new();
    readonly LimitsService _limitsSvc = new();
    List<HarnessLimit> _limits = [];
    bool _limitsLoading;
    int _tickCount;

    PeriodReport? _daily;
    PeriodReport? _weekly;
    PeriodReport? _monthly;
    SessionReport? _sessions;
    BlocksReport? _blocks;

    readonly Dictionary<string, AgentReport> _agentDaily = new(StringComparer.OrdinalIgnoreCase);

    DateTimeOffset? _lastUpdated;
    readonly Dictionary<string, string> _errors = [];
    bool _refreshing;
    int _refreshDone;
    int _refreshTotal = 5;

    /// <summary>Optional startup overrides (set from command-line args before the page exists).</summary>
    public static string? InitialTab { get; set; }
    public static bool AutoRefreshDisabled { get; set; }
    public static bool StartExpanded { get; set; }

    bool _compact = !StartExpanded;
    string _activeTab = "Weekly";
    static readonly string[] TabNames = ["Overview", "Daily", "Weekly", "Monthly", "Sessions", "Blocks"];
    readonly Dictionary<string, (Border Chip, TextBlock Label)> _tabChips = [];

    DispatcherTimer? _ticker;
    DateTimeOffset _lastAutoRefresh = DateTimeOffset.Now;

    public MainPage()
    {
        this.InitializeComponent();
        if (InitialTab is { } tab && TabNames.Contains(tab, StringComparer.OrdinalIgnoreCase))
        {
            _activeTab = TabNames.First(t => t.Equals(tab, StringComparison.OrdinalIgnoreCase));
            _compact = false;
        }
        BuildTabs();
        ApplyChrome();
        Loaded += OnLoaded;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        LoadFromCache();
        RenderActive();
        UpdateStatusLine();
        _ = RefreshLimitsAsync();
        if (!AutoRefreshDisabled) _ = RefreshAllAsync();

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _ticker.Tick += (_, _) =>
        {
            _tickCount++;
            UpdateStatusLine();
            if (_tickCount % 4 == 0) _ = RefreshLimitsAsync(); // every 2 min — cheap file/HTTP reads
            // Keep live countdowns fresh without re-running the CLI.
            if (_compact || _activeTab is "Overview" or "Blocks" or "Weekly") RenderActive();
            if (!_refreshing && DateTimeOffset.Now - _lastAutoRefresh > TimeSpan.FromMinutes(15))
                _ = RefreshAllAsync();
        };
        _ticker.Start();
    }

    void LoadFromCache()
    {
        var daily = _svc.LoadCached<PeriodReport>("daily");
        var weekly = _svc.LoadCached<PeriodReport>("weekly");
        var monthly = _svc.LoadCached<PeriodReport>("monthly");
        var sessions = _svc.LoadCached<SessionReport>("session");
        var blocks = _svc.LoadCached<BlocksReport>("blocks");
        _daily = daily?.Data;
        _weekly = weekly?.Data;
        _monthly = monthly?.Data;
        _sessions = sessions?.Data;
        _blocks = blocks?.Data;
        foreach (var agent in DetectedAgents())
        {
            if (_svc.LoadCached<AgentReport>($"{agent} daily") is { } ad)
                _agentDaily[agent] = ad.Data;
        }
        var stamps = new[] { daily?.FetchedAt, weekly?.FetchedAt, monthly?.FetchedAt, sessions?.FetchedAt, blocks?.FetchedAt }
            .Where(t => t is not null).Select(t => t!.Value).ToList();
        _lastUpdated = stamps.Count > 0 ? stamps.Max() : null;
    }

    async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _refreshDone = 0;
        _errors.Clear();
        _lastAutoRefresh = DateTimeOffset.Now;
        SetRefreshUi(true);

        // Run all reports concurrently; each re-renders as it lands.
        var tasks = new List<Task>
        {
            RefreshOneAsync<PeriodReport>("daily", r => _daily = r),
            RefreshOneAsync<PeriodReport>("weekly", r => _weekly = r),
            RefreshOneAsync<PeriodReport>("monthly", r => _monthly = r),
            RefreshOneAsync<SessionReport>("session", r => _sessions = r),
            RefreshOneAsync<BlocksReport>("blocks", r => _blocks = r),
        };
        foreach (var agent in DetectedAgents())
        {
            var a = agent;
            tasks.Add(RefreshOneAsync<AgentReport>($"{a} daily", r => { _agentDaily[a] = r; _ = RefreshLimitsAsync(); }));
        }
        _refreshTotal = tasks.Count;
        await Task.WhenAll(tasks);

        _refreshing = false;
        _lastUpdated = DateTimeOffset.Now;
        SetRefreshUi(false);
        UpdateStatusLine();
        RenderActive();
    }

    async Task RefreshOneAsync<T>(string command, Action<T> assign)
    {
        try
        {
            var result = await _svc.FetchAsync<T>(command);
            assign(result.Data); // continuations resume on the UI thread
            RenderActive();
        }
        catch (Exception ex)
        {
            _errors[command] = ex.Message;
        }
        finally
        {
            _refreshDone++;
            UpdateStatusLine();
        }
    }

    async Task RefreshLimitsAsync()
    {
        if (_limitsLoading) return;
        _limitsLoading = true;
        try
        {
            _limits = await _limitsSvc.GetAsync(_blocks, _weekly, _agentDaily);
            Trace($"limits: {_limits.Count} rows ({string.Join("; ", _limits.Select(l => $"{l.Harness} {l.Window} {l.UsedPercent:0}%"))})");
            RenderActive();
        }
        catch (Exception ex) { Trace($"limits FAILED: {ex}"); }
        finally { _limitsLoading = false; }
    }

    /// <summary>Harnesses seen in the session data; names are sanitized before reaching the CLI.</summary>
    List<string> DetectedAgents()
    {
        var agents = (_sessions?.Session ?? [])
            .Select(s => s.Agent)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => new string(a!.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()))
            .Where(a => a.Length > 0)
            .Distinct()
            .ToList();
        if (agents.Count == 0) agents = ["claude", "codex"];
        return agents;
    }

    void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = RefreshLimitsAsync();
        _ = RefreshAllAsync();
    }

    void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        Trace("OnCollapseClick");
        SetCompact(true);
    }

    void SetCompact(bool compact)
    {
        Trace($"SetCompact({compact}) current={_compact}");
        if (_compact == compact) return;
        _compact = compact;
        ApplyChrome();
        App.ApplyLayout(compact);
        RenderActive();
    }

    static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ccusage-desktop.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    void ApplyChrome()
    {
        var full = _compact ? Visibility.Collapsed : Visibility.Visible;
        HeaderBar.Visibility = full;
        TabBar.Visibility = full;
    }

    void SetRefreshUi(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        RefreshSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpinner.IsActive = busy;
        RefreshLabel.Text = busy ? "Refreshing" : "Refresh";
    }

    void UpdateStatusLine()
    {
        UpdatedText.Text = _refreshing
            ? $"Refreshing {_refreshDone}/{_refreshTotal} reports…"
            : _lastUpdated is { } t ? $"Updated {Fmt.Ago(t)}" : "No data yet";
        StatusText.Text = _errors.Count > 0
            ? $"⚠ {string.Join(", ", _errors.Keys)} failed"
            : _refreshing ? "ccusage is scanning transcript logs" : "";
        StatusText.Foreground = _errors.Count > 0 ? new SolidColorBrush(Theme.HotColor) : Theme.Faint;
    }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    void BuildTabs()
    {
        foreach (var name in TabNames)
        {
            var label = new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold, // set once — never toggled (Skia glyph gotcha)
                Foreground = Theme.Muted,
            };
            var chip = new Border
            {
                Child = label,
                Padding = new Thickness(15, 8, 15, 9),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            var tabName = name;
            chip.Tapped += (_, _) => SwitchTab(tabName);
            _tabChips[name] = (chip, label);
            TabBar.Children.Add(chip);
        }
        StyleTabs();
    }

    void SwitchTab(string name)
    {
        if (_activeTab == name) return;
        _activeTab = name;
        StyleTabs();
        RenderActive();
    }

    void StyleTabs()
    {
        foreach (var (name, (chip, label)) in _tabChips)
        {
            var active = name == _activeTab;
            chip.Background = new SolidColorBrush(active ? Color.FromArgb(0xFF, 0x18, 0x2A, 0x28) : Microsoft.UI.Colors.Transparent);
            chip.BorderBrush = new SolidColorBrush(active ? Color.FromArgb(0xFF, 0x2E, 0x54, 0x4C) : Microsoft.UI.Colors.Transparent);
            label.Foreground = active ? Theme.Accent : Theme.Muted;
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    void RenderActive()
    {
        ContentHost.Children.Clear();
        try
        {
            UIElement view = _compact ? BuildCompactView() : _activeTab switch
            {
                "Daily" => BuildPeriodTab(_daily, "Daily", "last 90 days", 90),
                "Weekly" => BuildPeriodTab(_weekly, "Weekly", "all weeks", 0),
                "Monthly" => BuildPeriodTab(_monthly, "Monthly", "all months", 0),
                "Sessions" => BuildSessionsTab(),
                "Blocks" => BuildBlocksTab(),
                _ => BuildOverviewTab(),
            };
            ContentHost.Children.Add(view);
        }
        catch (Exception ex)
        {
            Trace($"RenderActive({_activeTab}, compact={_compact}) FAILED: {ex}");
            ContentHost.Children.Add(new TextBlock
            {
                Text = "Render error — see %TEMP%\\ccusage-desktop.log",
                Foreground = new SolidColorBrush(Theme.HotColor),
                Margin = new Thickness(28),
            });
        }
    }

    static UIElement WrapScroll(StackPanel panel)
    {
        panel.Padding = new Thickness(28, 4, 28, 32);
        panel.Spacing = 16;
        return new ScrollViewer { Content = panel };
    }

    static UIElement EmptyState()
    {
        var panel = new StackPanel
        {
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new ProgressRing { Width = 44, Height = 44, IsActive = true, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "Running ccusage for the first time…",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Scanning every agent's transcript logs can take a few minutes on a large history.\nResults are cached, so future launches are instant.",
            FontSize = 13,
            Foreground = Theme.Muted,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return panel;
    }

    // ── Compact widget (default view: 5-hour block + this week) ─────────────

    UIElement BuildCompactView()
    {
        var scroller = new ScrollViewer();
        var root = new StackPanel { Padding = new Thickness(16, 10, 16, 12), Spacing = 10 };
        scroller.Content = root;

        // Title row: badge + name, expand button
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Theme.AccentColor, Offset = 0 },
                    new GradientStop { Color = Theme.Accent2Color, Offset = 1 },
                },
            },
            Child = new TextBlock
            {
                Text = "$", FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Cascadia Mono"),
                Foreground = new SolidColorBrush(Theme.BgColor),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        brand.Children.Add(new TextBlock { Text = "ccusage", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock
        {
            Text = _refreshing ? $"refreshing {_refreshDone}/{_refreshTotal}…" : _lastUpdated is { } t ? Fmt.Ago(t) : "no data",
            FontSize = 11, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 0, 0),
        });
        titleRow.Children.Add(brand);

        var expand = new Button
        {
            Content = new TextBlock { Text = "Expand", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Muted },
            Padding = new Thickness(10, 5, 10, 6),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1C, 0x25, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x35, 0x42)),
            BorderThickness = new Thickness(1),
        };
        expand.Click += (_, _) => { Trace("ExpandClick"); SetCompact(false); };
        Grid.SetColumn(expand, 1);
        titleRow.Children.Add(expand);
        root.Children.Add(titleRow);

        // ── Limits section (% used / % until reset, per harness)
        root.Children.Add(BuildLimitsCard(compact: true));

        // ── 5-hour block section
        var block = ActiveBlock();
        var blockPanel = new StackPanel { Spacing = 7 };
        blockPanel.Children.Add(CompactLabel("5-HOUR BLOCK", Theme.WarnColor));
        if (block is not null)
        {
            var remaining = block.EndTime - DateTimeOffset.Now;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock
            {
                Text = Fmt.Cost(block.CostUsd), FontSize = 24, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Cascadia Mono"), Foreground = Theme.Text,
            });
            var rightInfo = new TextBlock
            {
                Text = $"{Fmt.Duration(remaining)} left · {Fmt.Cost(block.BurnRate?.CostPerHour ?? 0)}/h",
                FontSize = 12, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
            };
            Grid.SetColumn(rightInfo, 1);
            line.Children.Add(rightInfo);
            blockPanel.Children.Add(line);

            var total = (block.EndTime - block.StartTime).TotalMinutes;
            var elapsed = Math.Clamp((DateTimeOffset.Now - block.StartTime).TotalMinutes, 0, total);
            var track = new Grid { Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x26, 0x31)) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(elapsed, 0.1), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(total - elapsed, 0.1), GridUnitType.Star) });
            track.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop { Color = Theme.AccentColor, Offset = 0 },
                        new GradientStop { Color = Theme.Accent2Color, Offset = 1 },
                    },
                },
            });
            blockPanel.Children.Add(track);
        }
        else
        {
            blockPanel.Children.Add(new TextBlock { Text = "No active block", FontSize = 14, Foreground = Theme.Muted });
        }
        root.Children.Add(CompactCard(blockPanel));

        // ── This week section
        var weekPanel = new StackPanel { Spacing = 7 };
        var week = _weekly?.Rows.LastOrDefault();
        var prevWeek = _weekly?.Rows.Count > 1 ? _weekly.Rows[^2] : null;
        var isCurrentWeek = week is not null && DateTime.TryParse(week.Period, out var ws)
            && ws <= DateTime.Today && DateTime.Today < ws.AddDays(7);
        weekPanel.Children.Add(CompactLabel("THIS WEEK", Theme.AccentColor));
        if (week is not null && isCurrentWeek)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock
            {
                Text = Fmt.Cost(week.TotalCost), FontSize = 24, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Cascadia Mono"), Foreground = Theme.Text,
            });
            var delta = prevWeek is { TotalCost: > 0 }
                ? $" · {(week.TotalCost / prevWeek.TotalCost - 1) * 100:+0;-0}% vs last wk"
                : "";
            var rightInfo = new TextBlock
            {
                Text = $"{Fmt.Tokens(week.TotalTokens)} tokens{delta}",
                FontSize = 12, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 3),
            };
            Grid.SetColumn(rightInfo, 1);
            line.Children.Add(rightInfo);
            weekPanel.Children.Add(line);
        }
        else
        {
            weekPanel.Children.Add(new TextBlock { Text = "No usage this week yet", FontSize = 14, Foreground = Theme.Muted });
        }

        // 7-day mini bars
        if (_daily is not null)
        {
            var byDate = new Dictionary<string, double>();
            foreach (var r in _daily.Rows) byDate[r.Period] = r.TotalCost;
            var days = new List<(string, double, bool)>();
            for (var d = DateTime.Today.AddDays(-6); d <= DateTime.Today; d = d.AddDays(1))
            {
                byDate.TryGetValue(d.ToString("yyyy-MM-dd"), out var cost);
                days.Add((d.ToString("ddd"), cost, d == DateTime.Today));
            }
            weekPanel.Children.Add(BuildBarChart(days, 44, labelEvery: 1, showPeak: false));
        }
        root.Children.Add(CompactCard(weekPanel));

        return scroller;
    }

    Border BuildLimitsCard(bool compact)
    {
        var panel = new StackPanel { Spacing = 9 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(CompactLabel("LIMITS", Theme.Accent2Color));
        var caption = new TextBlock
        {
            Text = _limits.Any(l => l.IsProxy) ? "≈ rows are vs your personal max" : "",
            FontSize = 10.5, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(caption, 1);
        header.Children.Add(caption);
        panel.Children.Add(header);

        if (_limits.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _limitsLoading ? "Reading limits…" : "No limit data found (Claude CLI login or a recent Codex session provides it).",
                FontSize = 12, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap,
            });
            return compact ? CompactCard(panel) : Card(panel);
        }

        foreach (var limit in _limits)
        {
            var (agentLabel, agentColor) = Fmt.AgentInfo(limit.Harness);
            var used = Math.Clamp(limit.UsedPercent, 0, 100);
            var barColor = used >= 80 ? Theme.HotColor : used >= 50 ? Theme.WarnColor : Theme.AccentColor;

            var row = new StackPanel { Spacing = 4 };

            // Line 1: harness + window │ NN% used · MM% left
            var top = new Grid { ColumnSpacing = 8 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(AgentChips([limit.Harness]));
            var windowText = new TextBlock
            {
                Text = limit.Window + (limit.IsProxy ? " ≈" : ""),
                FontSize = 12.5, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(windowText, 1);
            top.Children.Add(windowText);
            var pctText = new TextBlock
            {
                FontSize = 12.5, FontFamily = new FontFamily("Cascadia Mono"), VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(barColor),
                Text = $"{used:0}% used · {100 - used:0}% left",
            };
            Grid.SetColumn(pctText, 2);
            top.Children.Add(pctText);
            row.Children.Add(top);

            // Line 2: fill bar
            var track = new Grid { Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x26, 0x31)) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(used, 0.5), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - used, 0.5), GridUnitType.Star) });
            track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(barColor) });
            row.Children.Add(track);

            // Line 3: reset + staleness meta
            var meta = new List<string>();
            if (limit.ResetsAt is { } resets)
            {
                var local = resets.ToLocalTime();
                meta.Add(local.Date == DateTime.Today
                    ? $"resets {local:HH:mm}"
                    : $"resets {local:ddd d MMM HH:mm}");
            }
            if (limit.IsProxy) meta.Add("vs personal max");
            else if (limit.Note == "est" && limit.AsOf is { } snapAt) meta.Add($"est from {snapAt:HH:mm} snapshot");
            else if (limit.AsOf is { } asOf && DateTimeOffset.Now - asOf > TimeSpan.FromMinutes(30)) meta.Add($"as of {Fmt.Ago(asOf)}");
            if (meta.Count > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", meta), FontSize = 10.5, Foreground = Theme.Faint,
                });
            }

            panel.Children.Add(row);
        }

        return compact ? CompactCard(panel) : Card(panel);
    }

    static UIElement CompactLabel(string text, Color accent)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        panel.Children.Add(new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Muted, CharacterSpacing = 60 });
        return panel;
    }

    static Border CompactCard(UIElement child) => new()
    {
        Background = Theme.Card,
        BorderBrush = Theme.Stroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11),
        Padding = new Thickness(14, 11, 14, 12),
        Child = child,
    };

    // ── Overview tab ─────────────────────────────────────────────────────────

    UIElement BuildOverviewTab()
    {
        if (_daily is null && _monthly is null && _blocks is null) return EmptyState();
        var root = new StackPanel();

        // KPI row
        var kpis = new Grid { ColumnSpacing = 16 };
        for (var i = 0; i < 4; i++) kpis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var todayRow = _daily?.Rows.FirstOrDefault(r => r.Period == today);
        AddKpi(kpis, 0, "TODAY", Fmt.Cost(todayRow?.TotalCost ?? 0),
            todayRow is null ? "no usage yet" : $"{Fmt.Tokens(todayRow.TotalTokens)} tokens · {todayRow.ModelsUsed.Count} models",
            Theme.AccentColor);

        var block = ActiveBlock();
        if (block is not null)
        {
            var remaining = block.EndTime - DateTimeOffset.Now;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            AddKpi(kpis, 1, "CURRENT 5-HOUR BLOCK", Fmt.Cost(block.CostUsd),
                $"{Fmt.Duration(remaining)} left · {Fmt.Cost(block.BurnRate?.CostPerHour ?? 0)}/h burn",
                Theme.WarnColor);
        }
        else
        {
            AddKpi(kpis, 1, "CURRENT 5-HOUR BLOCK", "—", "no active block", Theme.FaintColor);
        }

        var thisMonth = _monthly?.Rows.FirstOrDefault(r => r.Period == DateTime.Now.ToString("yyyy-MM"));
        var prevMonth = _monthly?.Rows.FirstOrDefault(r => r.Period == DateTime.Now.AddMonths(-1).ToString("yyyy-MM"));
        var monthSub = thisMonth is null ? "no usage yet"
            : prevMonth is { TotalCost: > 0 }
                ? $"{Fmt.Tokens(thisMonth.TotalTokens)} tokens · {(thisMonth.TotalCost / prevMonth.TotalCost - 1) * 100:+0;-0}% vs {DateTime.Now.AddMonths(-1):MMM}"
                : $"{Fmt.Tokens(thisMonth.TotalTokens)} tokens";
        AddKpi(kpis, 2, DateTime.Now.ToString("'THIS MONTH ('MMM')'").ToUpperInvariant(), Fmt.Cost(thisMonth?.TotalCost ?? 0), monthSub, Theme.Accent2Color);

        var totals = _monthly?.Totals ?? _daily?.Totals;
        var firstPeriod = _daily?.Rows.FirstOrDefault()?.Period;
        AddKpi(kpis, 3, "ALL TIME", Fmt.Cost(totals?.TotalCost ?? 0),
            totals is null ? "" : $"{Fmt.Tokens(totals.TotalTokens)} tokens{(DateTime.TryParse(firstPeriod, out var fp) ? $" · since {fp:MMM yyyy}" : "")}",
            Theme.GoodColor);

        root.Children.Add(kpis);

        // Limits (% used per harness)
        root.Children.Add(BuildLimitsCard(compact: false));

        // Daily spend chart (last 30 days)
        if (_daily is not null)
            root.Children.Add(BuildDailyChartCard(30, 190));

        // Bottom row: agents + models
        var bottom = new Grid { ColumnSpacing = 16 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var agents = BuildAgentsCard();
        var models = BuildTopModelsCard();
        Grid.SetColumn(models, 1);
        bottom.Children.Add(agents);
        bottom.Children.Add(models);
        root.Children.Add(bottom);

        return WrapScroll(root);
    }

    static void AddKpi(Grid grid, int column, string title, string value, string sub, Color accent)
    {
        var panel = new StackPanel { Spacing = 6 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Muted, CharacterSpacing = 60 });
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = value, FontSize = 30, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Cascadia Mono"), Foreground = Theme.Text,
        });
        panel.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        var card = Card(panel);
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    Border BuildDailyChartCard(int days, double chartHeight)
    {
        var rows = _daily!.Rows;
        var byDate = new Dictionary<string, double>();
        foreach (var r in rows) byDate[r.Period] = r.TotalCost;

        var points = new List<(string Label, double Value, bool Highlight)>();
        var start = DateTime.Today.AddDays(-(days - 1));
        for (var d = start; d <= DateTime.Today; d = d.AddDays(1))
        {
            byDate.TryGetValue(d.ToString("yyyy-MM-dd"), out var cost);
            points.Add((d.ToString("MMM d"), cost, d == DateTime.Today));
        }

        var values = points.Select(p => p.Value).ToList();
        var caption = $"peak {Fmt.Cost(values.Max())} · avg {Fmt.Cost(values.Where(v => v > 0).DefaultIfEmpty(0).Average())} per active day";
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(SectionHeader($"Daily spend — last {days} days", caption));
        panel.Children.Add(BuildBarChart(points, chartHeight, labelEvery: Math.Max(days / 10, 1)));
        return Card(panel);
    }

    Border BuildAgentsCard()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SectionHeader("By agent", "all time, from per-session data"));

        var groups = (_sessions?.Session ?? [])
            .GroupBy(s => s.Agent ?? "unknown")
            .Select(g => (Agent: g.Key, Cost: g.Sum(s => s.TotalCost), Count: g.Count(), Tokens: g.Sum(s => s.TotalTokens)))
            .OrderByDescending(g => g.Cost)
            .ToList();

        if (groups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No session data yet.", FontSize = 13, Foreground = Theme.Faint });
            return Card(panel);
        }

        var totalCost = groups.Sum(g => g.Cost);

        // Stacked proportion bar
        var stack = new Grid { Height = 12, CornerRadius = new CornerRadius(6) };
        for (var i = 0; i < groups.Count; i++)
        {
            stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(groups[i].Cost, 0.01), GridUnitType.Star) });
            var (_, color) = Fmt.AgentInfo(groups[i].Agent);
            var seg = new Border { Background = new SolidColorBrush(color), Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0) };
            Grid.SetColumn(seg, i);
            stack.Children.Add(seg);
        }
        panel.Children.Add(stack);

        foreach (var g in groups)
        {
            var (label, color) = Fmt.AgentInfo(g.Agent);
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = $"{label}   ·   {g.Count:N0} sessions · {Fmt.Tokens(g.Tokens)} tokens", FontSize = 13, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            var pct = new TextBlock { Text = totalCost > 0 ? $"{g.Cost / totalCost * 100:0.#}%" : "", FontSize = 12, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(pct, 2);
            var cost = new TextBlock { Text = Fmt.Cost(g.Cost), FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Cascadia Mono"), Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Width = 86, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(cost, 3);

            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(pct);
            row.Children.Add(cost);
            panel.Children.Add(row);
        }

        return Card(panel);
    }

    Border BuildTopModelsCard()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SectionHeader("Top models", "all time, by cost"));

        var models = (_monthly?.Rows ?? [])
            .SelectMany(r => r.ModelBreakdowns)
            .GroupBy(b => b.ModelName)
            .Select(g => (Model: g.Key, Cost: g.Sum(b => b.Cost), Tokens: g.Sum(b => b.InputTokens + b.OutputTokens + b.CacheCreationTokens + b.CacheReadTokens)))
            .OrderByDescending(m => m.Cost)
            .Take(8)
            .ToList();

        if (models.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No model data yet.", FontSize = 13, Foreground = Theme.Faint });
            return Card(panel);
        }

        var max = models[0].Cost;
        foreach (var m in models)
        {
            var (_, color) = Fmt.ModelFamily(m.Model);
            var row = new StackPanel { Spacing = 5 };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = m.Model, FontSize = 12.5, FontFamily = new FontFamily("Cascadia Mono"), Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis };
            var cost = new TextBlock { Text = $"{Fmt.Tokens(m.Tokens)} · {Fmt.Cost(m.Cost)}", FontSize = 12, Foreground = Theme.Muted };
            Grid.SetColumn(cost, 1);
            top.Children.Add(name);
            top.Children.Add(cost);
            row.Children.Add(top);

            var track = new Grid { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x26, 0x31)) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(m.Cost, 0.001), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(max - m.Cost, 0.001), GridUnitType.Star) });
            var fill = new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(3) };
            track.Children.Add(fill);
            row.Children.Add(track);
            panel.Children.Add(row);
        }

        return Card(panel);
    }

    // ── Daily / Weekly / Monthly tabs ────────────────────────────────────────

    UIElement BuildPeriodTab(PeriodReport? report, string kind, string chartCaption, int chartDays)
    {
        if (report is null) return EmptyState();
        var root = new StackPanel();
        var rows = report.Rows;
        double[] widths = [130, 190, 230, 90, 90, 105, 115, 100];

        // The default expanded tab also leads with limits.
        if (kind == "Weekly") root.Children.Add(BuildLimitsCard(compact: false));

        // Chart
        if (kind == "Daily")
        {
            root.Children.Add(BuildDailyChartCard(chartDays, 170));
        }
        else if (rows.Count > 0)
        {
            var points = rows.Select(r => (Label: FormatPeriod(r.Period, kind), Value: r.TotalCost, Highlight: r == rows[^1])).ToList();
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(SectionHeader($"{kind} spend", chartCaption));
            panel.Children.Add(BuildBarChart(points, 170, labelEvery: kind == "Monthly" ? 1 : Math.Max(rows.Count / 12, 1)));
            root.Children.Add(Card(panel));
        }

        // Table (newest first)
        var table = new StackPanel { Spacing = 0 };
        table.Children.Add(SectionHeader($"{kind} breakdown", $"{rows.Count:N0} periods"));
        table.Children.Add(new Border { Height = 12 });
        table.Children.Add(TableHeader(
            [kind == "Daily" ? "Date" : kind == "Weekly" ? "Week of" : "Month", "Agents", "Models", "Input", "Output", "Cache read", "Total tokens", "Cost"],
            widths));

        var alt = false;
        foreach (var r in Enumerable.Reverse(rows))
        {
            var families = r.ModelsUsed.Select(m => Fmt.ModelFamily(m).Family).Distinct().ToList();
            table.Children.Add(TableRow(alt,
            [
                Cell(FormatPeriod(r.Period, kind)),
                AgentChips(r.Metadata?.Agents ?? []),
                Cell(string.Join(" · ", families), muted: true),
                Cell(Fmt.Tokens(r.InputTokens), mono: true, right: true),
                Cell(Fmt.Tokens(r.OutputTokens), mono: true, right: true),
                Cell(Fmt.Tokens(r.CacheReadTokens), mono: true, right: true),
                Cell(Fmt.Tokens(r.TotalTokens), mono: true, right: true),
                Cell(Fmt.Cost(r.TotalCost), mono: true, right: true, emphasize: true),
            ], widths));
            alt = !alt;
        }

        if (report.Totals is { } t)
        {
            table.Children.Add(TableRow(false,
            [
                Cell("TOTAL", emphasize: true),
                Cell(""),
                Cell(""),
                Cell(Fmt.Tokens(t.InputTokens), mono: true, right: true, emphasize: true),
                Cell(Fmt.Tokens(t.OutputTokens), mono: true, right: true, emphasize: true),
                Cell(Fmt.Tokens(t.CacheReadTokens), mono: true, right: true, emphasize: true),
                Cell(Fmt.Tokens(t.TotalTokens), mono: true, right: true, emphasize: true),
                Cell(Fmt.Cost(t.TotalCost), mono: true, right: true, emphasize: true),
            ], widths));
        }

        root.Children.Add(Card(table));
        return WrapScroll(root);
    }

    static string FormatPeriod(string period, string kind)
    {
        if (kind == "Monthly" && DateTime.TryParse(period + "-01", out var m)) return m.ToString("MMM yyyy");
        if (DateTime.TryParse(period, out var d)) return d.ToString("ddd, MMM d");
        return period;
    }

    // ── Sessions tab ─────────────────────────────────────────────────────────

    UIElement BuildSessionsTab()
    {
        if (_sessions is null) return EmptyState();
        var root = new StackPanel();

        var all = _sessions.Session;
        var top = all.OrderByDescending(s => s.TotalCost).Take(100).ToList();

        var table = new StackPanel { Spacing = 0 };
        table.Children.Add(SectionHeader("Most expensive sessions", $"top {top.Count} of {all.Count:N0} sessions, by cost"));
        table.Children.Add(new Border { Height = 12 });
        double[] widths = [120, 330, 110, 240, 100, 100];
        table.Children.Add(TableHeader(["Agent", "Session", "Last active", "Models", "Tokens", "Cost"], widths));

        var alt = false;
        foreach (var s in top)
        {
            var families = s.ModelsUsed.Select(m => Fmt.ModelFamily(m).Family).Distinct().ToList();
            table.Children.Add(TableRow(alt,
            [
                AgentChips(s.Agent is null ? [] : [s.Agent]),
                Cell(s.Period, mono: true, muted: true),
                Cell(s.Metadata?.LastActivity is { } la ? Fmt.Ago(la) : "—", muted: true),
                Cell(string.Join(" · ", families), muted: true),
                Cell(Fmt.Tokens(s.TotalTokens), mono: true, right: true),
                Cell(Fmt.Cost(s.TotalCost), mono: true, right: true, emphasize: true),
            ], widths));
            alt = !alt;
        }

        root.Children.Add(Card(table));
        return WrapScroll(root);
    }

    // ── Blocks tab ───────────────────────────────────────────────────────────

    UsageBlock? ActiveBlock() => _blocks?.Blocks.LastOrDefault(b => b is { IsActive: true, IsGap: false });

    UIElement BuildBlocksTab()
    {
        if (_blocks is null) return EmptyState();
        var root = new StackPanel();

        var block = ActiveBlock();
        if (block is not null) root.Children.Add(BuildActiveBlockHero(block));

        var past = _blocks.Blocks.Where(b => !b.IsGap && !b.IsActive).Reverse().Take(60).ToList();
        var table = new StackPanel { Spacing = 0 };
        table.Children.Add(SectionHeader("Recent 5-hour billing blocks", $"last {past.Count} completed blocks"));
        table.Children.Add(new Border { Height = 12 });
        double[] widths = [200, 110, 290, 110, 110, 100];
        table.Children.Add(TableHeader(["Started", "Duration", "Models", "Tokens", "Avg burn", "Cost"], widths));

        var alt = false;
        foreach (var b in past)
        {
            var duration = (b.ActualEndTime ?? b.EndTime) - b.StartTime;
            var burn = duration.TotalHours > 0.05 ? b.CostUsd / duration.TotalHours : 0;
            var families = b.Models.Select(m => Fmt.ModelFamily(m).Family).Distinct().ToList();
            table.Children.Add(TableRow(alt,
            [
                Cell(b.StartTime.ToLocalTime().ToString("ddd, MMM d · HH:mm"), mono: true),
                Cell(Fmt.Duration(duration), muted: true),
                Cell(string.Join(" · ", families), muted: true),
                Cell(Fmt.Tokens(b.TotalTokens), mono: true, right: true),
                Cell(Fmt.Cost(burn) + "/h", mono: true, right: true, muted: true),
                Cell(Fmt.Cost(b.CostUsd), mono: true, right: true, emphasize: true),
            ], widths));
            alt = !alt;
        }

        root.Children.Add(Card(table));
        return WrapScroll(root);
    }

    Border BuildActiveBlockHero(UsageBlock block)
    {
        var panel = new StackPanel { Spacing = 14 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Theme.GoodColor), VerticalAlignment = VerticalAlignment.Center,
        });
        title.Children.Add(new TextBlock { Text = "Active block", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Theme.Text });
        title.Children.Add(new TextBlock
        {
            Text = $"started {block.StartTime.ToLocalTime():HH:mm} · {string.Join(", ", block.Models.Select(m => Fmt.ModelFamily(m).Family).Distinct())}",
            FontSize = 12.5, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(title);

        var remaining = block.EndTime - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var rightText = new TextBlock
        {
            Text = $"{Fmt.Duration(remaining)} remaining",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rightText, 1);
        header.Children.Add(rightText);
        panel.Children.Add(header);

        // 5h window progress
        var total = (block.EndTime - block.StartTime).TotalMinutes;
        var elapsed = Math.Clamp((DateTimeOffset.Now - block.StartTime).TotalMinutes, 0, total);
        var track = new Grid { Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x26, 0x31)) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(elapsed, 0.1), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(total - elapsed, 0.1), GridUnitType.Star) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Theme.AccentColor, Offset = 0 },
                    new GradientStop { Color = Theme.Accent2Color, Offset = 1 },
                },
            },
        };
        track.Children.Add(fill);
        panel.Children.Add(track);

        // Stat strip
        var stats = new Grid { ColumnSpacing = 12 };
        var items = new (string Title, string Value)[]
        {
            ("SPENT", Fmt.Cost(block.CostUsd)),
            ("BURN RATE", Fmt.Cost(block.BurnRate?.CostPerHour ?? 0) + "/h"),
            ("TOKENS", Fmt.Tokens(block.TotalTokens)),
            ("PROJECTED", Fmt.Cost(block.Projection?.TotalCost ?? 0)),
            ("ENTRIES", block.Entries.ToString("N0")),
        };
        for (var i = 0; i < items.Length; i++)
        {
            stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cellPanel = new StackPanel { Spacing = 3 };
            cellPanel.Children.Add(new TextBlock { Text = items[i].Title, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Faint, CharacterSpacing = 60 });
            cellPanel.Children.Add(new TextBlock { Text = items[i].Value, FontSize = 19, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Cascadia Mono"), Foreground = Theme.Text });
            var cellCard = new Border
            {
                Background = Theme.CardAlt, CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14, 10, 14, 12), Child = cellPanel,
            };
            Grid.SetColumn(cellCard, i);
            stats.Children.Add(cellCard);
        }
        panel.Children.Add(stats);

        return Card(panel);
    }

    // ── Shared UI helpers ────────────────────────────────────────────────────

    static Border Card(UIElement child) => new()
    {
        Background = Theme.Card,
        BorderBrush = Theme.Stroke,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(13),
        Padding = new Thickness(20, 17, 20, 19),
        Child = child,
    };

    static UIElement SectionHeader(string title, string caption)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Theme.Text });
        var cap = new TextBlock { Text = caption, FontSize = 12, Foreground = Theme.Faint, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(cap, 1);
        grid.Children.Add(cap);
        return grid;
    }

    static UIElement BuildBarChart(IReadOnlyList<(string Label, double Value, bool Highlight)> points, double height, int labelEvery, bool showPeak = true)
    {
        var max = Math.Max(points.Max(p => p.Value), 0.01);
        var maxIndex = -1;
        for (var i = 0; i < points.Count; i++)
            if (points[i].Value == max) { maxIndex = i; break; }

        var chart = new Grid();
        chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < points.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (label, value, highlight) = points[i];

            var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 4 };
            var barHeight = Math.Max(value / max * (height - 24), value > 0 ? 3 : 1.5);
            var color = highlight ? Theme.Accent2Color
                : i == maxIndex && value > 0 ? Theme.AccentColor
                : Color.FromArgb(0xFF, 0x2C, 0x6E, 0x62);
            if (value == 0) color = Color.FromArgb(0xFF, 0x1B, 0x22, 0x2C);
            cell.Children.Add(new Border
            {
                Height = barHeight,
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(2.5, 2.5, 0, 0),
                Margin = new Thickness(points.Count > 60 ? 0.5 : 1.5, 0, points.Count > 60 ? 0.5 : 1.5, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            });
            Grid.SetColumn(cell, i);
            chart.Children.Add(cell);

            if (labelEvery > 0 && (i % labelEvery == 0 || i == points.Count - 1))
            {
                var axis = new TextBlock
                {
                    Text = label, FontSize = 10, Foreground = Theme.Faint,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                Grid.SetRow(axis, 1);
                if (points.Count > 20)
                {
                    PlaceSpanning(chart, axis, i, points.Count, 5);
                }
                else
                {
                    axis.HorizontalAlignment = HorizontalAlignment.Center;
                    Grid.SetColumn(axis, i);
                }
                chart.Children.Add(axis);
            }
        }

        // Peak value label spans several columns so it never clips inside one narrow bar cell.
        if (showPeak && maxIndex >= 0 && points[maxIndex].Value > 0)
        {
            var peak = new TextBlock
            {
                Text = Fmt.Cost(points[maxIndex].Value),
                FontSize = 10.5,
                FontFamily = new FontFamily("Cascadia Mono"),
                Foreground = Theme.Muted,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, Math.Max(points[maxIndex].Value / max * (height - 24) + 5, 8)),
            };
            PlaceSpanning(chart, peak, maxIndex, points.Count, 7);
            chart.Children.Add(peak);
        }
        return chart;
    }

    /// <summary>Spans a label over several grid columns, keeping it visually anchored to <paramref name="index"/> even when clamped at an edge.</summary>
    static void PlaceSpanning(Grid grid, TextBlock text, int index, int count, int maxSpan)
    {
        var span = Math.Min(maxSpan, count);
        var ideal = index - span / 2;
        var first = Math.Clamp(ideal, 0, count - span);
        text.HorizontalAlignment = first < ideal ? HorizontalAlignment.Right
            : first > ideal ? HorizontalAlignment.Left
            : HorizontalAlignment.Center;
        Grid.SetColumn(text, first);
        Grid.SetColumnSpan(text, span);
    }

    static UIElement TableHeader(string[] headers, double[] widths)
    {
        var grid = RowGrid(widths);
        for (var i = 0; i < headers.Length; i++)
        {
            var numeric = headers[i] is "Input" or "Output" or "Cache read" or "Total tokens" or "Tokens" or "Cost" or "Avg burn";
            var text = new TextBlock
            {
                Text = headers[i].ToUpperInvariant(),
                FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Faint, CharacterSpacing = 50,
                HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 8, 12, 9),
            BorderBrush = Theme.Stroke,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    static UIElement TableRow(bool alt, UIElement[] cells, double[] widths)
    {
        var grid = RowGrid(widths);
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 8, 12, 8),
            Background = alt ? new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x1C, 0x26)) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(6),
        };
    }

    static Grid RowGrid(double[] widths)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < widths.Length; i++)
        {
            // Third column (models/session text) flexes; the rest are fixed.
            grid.ColumnDefinitions.Add(i == 2
                ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = widths[i] }
                : new ColumnDefinition { Width = new GridLength(widths[i]) });
        }
        return grid;
    }

    static UIElement Cell(string text, bool mono = false, bool right = false, bool muted = false, bool emphasize = false)
        => new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            FontFamily = mono ? new FontFamily("Cascadia Mono") : FontFamily.XamlAutoFontFamily,
            FontWeight = emphasize ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = muted ? Theme.Muted : Theme.Text,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

    static UIElement AgentChips(IEnumerable<string> agents)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        foreach (var agent in agents)
        {
            var (fullLabel, color) = Fmt.AgentInfo(agent);
            var label = fullLabel.Replace(" Code", "").Replace(" CLI", ""); // compact for table cells
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 3),
                Child = new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) },
            };
            panel.Children.Add(chip);
        }
        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "—", FontSize = 12, Foreground = Theme.Faint });
        return panel;
    }
}
