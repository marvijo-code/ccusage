using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcusageDesktop.Models;

public sealed class PeriodReport
{
    [JsonPropertyName("daily")] public List<UsageRow>? Daily { get; set; }
    [JsonPropertyName("weekly")] public List<UsageRow>? Weekly { get; set; }
    [JsonPropertyName("monthly")] public List<UsageRow>? Monthly { get; set; }
    [JsonPropertyName("totals")] public UsageTotals? Totals { get; set; }

    public List<UsageRow> Rows => Daily ?? Weekly ?? Monthly ?? [];
}

/// <summary>
/// Tolerant shape for per-agent reports (`ccusage &lt;agent&gt; daily/weekly`) — the per-agent
/// formatters disagree on field names (date vs week vs period, totalCost vs costUSD).
/// </summary>
public sealed class AgentReport
{
    [JsonPropertyName("daily")] public List<FlexRow>? Daily { get; set; }
    [JsonPropertyName("weekly")] public List<FlexRow>? Weekly { get; set; }

    public List<FlexRow> Rows => Daily ?? Weekly ?? [];
}

public sealed class FlexRow
{
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("week")] public string? Week { get; set; }
    [JsonPropertyName("period")] public string? Period { get; set; }
    [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
    [JsonPropertyName("totalCost")] public double? TotalCost { get; set; }
    [JsonPropertyName("costUSD")] public double? CostUsd { get; set; }
    [JsonPropertyName("modelBreakdowns")] public List<ModelBreakdown>? ModelBreakdowns { get; set; }

    public string? Day => Date ?? Week ?? Period;
    public double Cost => TotalCost ?? CostUsd ?? 0;
}

public sealed class SessionReport
{
    [JsonPropertyName("session")] public List<UsageRow> Session { get; set; } = [];
    [JsonPropertyName("totals")] public UsageTotals? Totals { get; set; }
}

public sealed class BlocksReport
{
    [JsonPropertyName("blocks")] public List<UsageBlock> Blocks { get; set; } = [];
}

public sealed class UsageBlock
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("startTime")] public DateTimeOffset StartTime { get; set; }
    [JsonPropertyName("endTime")] public DateTimeOffset EndTime { get; set; }
    [JsonPropertyName("actualEndTime")] public DateTimeOffset? ActualEndTime { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("isGap")] public bool IsGap { get; set; }
    [JsonPropertyName("entries")] public int Entries { get; set; }
    [JsonPropertyName("models")] public List<string> Models { get; set; } = [];
    [JsonPropertyName("costUSD")] public double CostUsd { get; set; }
    [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
    [JsonPropertyName("tokenCounts")] public BlockTokenCounts? TokenCounts { get; set; }
    [JsonPropertyName("burnRate")] public BlockBurnRate? BurnRate { get; set; }
    [JsonPropertyName("projection")] public BlockProjection? Projection { get; set; }
}

public sealed class BlockTokenCounts
{
    [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("cacheCreationInputTokens")] public long CacheCreationInputTokens { get; set; }
    [JsonPropertyName("cacheReadInputTokens")] public long CacheReadInputTokens { get; set; }
}

public sealed class BlockBurnRate
{
    [JsonPropertyName("tokensPerMinute")] public double TokensPerMinute { get; set; }
    [JsonPropertyName("costPerHour")] public double CostPerHour { get; set; }
}

public sealed class BlockProjection
{
    [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
    [JsonPropertyName("totalCost")] public double TotalCost { get; set; }
    [JsonPropertyName("remainingMinutes")] public int RemainingMinutes { get; set; }
}

public sealed class UsageRow
{
    [JsonPropertyName("period")] public string Period { get; set; } = "";
    [JsonPropertyName("agent")] public string? Agent { get; set; }
    [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("cacheCreationTokens")] public long CacheCreationTokens { get; set; }
    [JsonPropertyName("cacheReadTokens")] public long CacheReadTokens { get; set; }
    [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
    [JsonPropertyName("totalCost")] public double TotalCost { get; set; }
    [JsonPropertyName("modelsUsed")] public List<string> ModelsUsed { get; set; } = [];
    [JsonPropertyName("modelBreakdowns")] public List<ModelBreakdown> ModelBreakdowns { get; set; } = [];
    [JsonPropertyName("metadata")] public RowMetadata? Metadata { get; set; }
}

public sealed class RowMetadata
{
    [JsonPropertyName("agents")] public List<string> Agents { get; set; } = [];
    [JsonPropertyName("lastActivity")] public DateTimeOffset? LastActivity { get; set; }
}

public sealed class ModelBreakdown
{
    [JsonPropertyName("modelName")] public string ModelName { get; set; } = "";
    [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("cacheCreationTokens")] public long CacheCreationTokens { get; set; }
    [JsonPropertyName("cacheReadTokens")] public long CacheReadTokens { get; set; }
    [JsonPropertyName("cost")] public double Cost { get; set; }
}

public sealed class UsageTotals
{
    [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
    [JsonPropertyName("cacheCreationTokens")] public long CacheCreationTokens { get; set; }
    [JsonPropertyName("cacheReadTokens")] public long CacheReadTokens { get; set; }
    [JsonPropertyName("totalTokens")] public long TotalTokens { get; set; }
    [JsonPropertyName("totalCost")] public double TotalCost { get; set; }
}

public static class UsageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
