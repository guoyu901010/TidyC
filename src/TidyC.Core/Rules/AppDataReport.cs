using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyC.Core.Rules;

/// <summary>
/// Contract for <c>appdata-report.json</c>. Field names are snake_case on
/// purpose: the file is consumed by external tooling and must stay stable.
/// </summary>
public sealed class AppDataAnalysisReport
{
    [JsonPropertyName("scan_id")]
    public string ScanId { get; set; } = string.Empty;

    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; set; } = "v2.4.0-fluent";

    [JsonPropertyName("ruleset_signature")]
    public string RulesetSignature { get; set; } = string.Empty;

    [JsonPropertyName("target_scopes")]
    public List<string> TargetScopes { get; set; } = new() { "Local", "Roaming", "LocalLow" };

    [JsonPropertyName("scan_duration_seconds")]
    public double ScanDurationSeconds { get; set; }

    [JsonPropertyName("summary")]
    public AppDataSummary Summary { get; set; } = new();

    [JsonPropertyName("applications")]
    public List<ApplicationGroup> Applications { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();
}

public sealed class AppDataSummary
{
    [JsonPropertyName("total_size_bytes")]
    public long TotalSizeBytes { get; set; }

    [JsonPropertyName("green_safe_bytes")]
    public long GreenSafeBytes { get; set; }

    [JsonPropertyName("yellow_caution_bytes")]
    public long YellowCautionBytes { get; set; }

    [JsonPropertyName("red_preserve_bytes")]
    public long RedPreserveBytes { get; set; }

    [JsonPropertyName("gray_unrecognized_bytes")]
    public long GrayUnrecognizedBytes { get; set; }

    [JsonPropertyName("selectable_bytes")]
    public long SelectableBytes => GreenSafeBytes + YellowCautionBytes;
}

public sealed class ApplicationGroup
{
    [JsonPropertyName("app_name")]
    public string AppName { get; set; } = string.Empty;

    [JsonPropertyName("app_id")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("items")]
    public List<AppDataItem> Items { get; set; } = new();

    public long SelectableBytes
    {
        get
        {
            long total = 0;
            foreach (AppDataItem item in Items)
            {
                if (item.RiskLevel.IsSelectable())
                {
                    total += item.SizeBytes;
                }
            }

            return total;
        }
    }
}

public sealed class AppDataItem
{
    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("absolute_path")]
    public string AbsolutePath { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("file_count")]
    public long FileCount { get; set; }

    [JsonPropertyName("risk_level")]
    public RiskLevel RiskLevel { get; set; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = string.Empty;

    [JsonPropertyName("consequence")]
    public string Consequence { get; set; } = string.Empty;

    [JsonPropertyName("rule_id")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("is_locked")]
    public bool IsLocked => !RiskLevel.IsSelectable();

    [JsonPropertyName("is_selectable")]
    public bool IsSelectable => RiskLevel.IsSelectable();

    [JsonPropertyName("is_directory")]
    public bool IsDirectory { get; set; } = true;

    [JsonPropertyName("last_write_utc")]
    public DateTime LastWriteUtc { get; set; }
}

public static class AppDataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new RiskLevelConverter() },
    };

    public static string Serialize(AppDataAnalysisReport report) =>
        System.Text.Json.JsonSerializer.Serialize(report, Options);
}
