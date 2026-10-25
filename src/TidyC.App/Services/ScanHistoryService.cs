using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.Services;

public sealed class ScanHistoryEntry
{
    [JsonPropertyName("scan_id")]
    public string ScanId { get; set; } = string.Empty;

    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; }

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("files")]
    public long Files { get; set; }

    [JsonPropertyName("directories")]
    public long Directories { get; set; }

    [JsonPropertyName("attributed_bytes")]
    public long AttributedBytes { get; set; }

    [JsonPropertyName("used_bytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("windows_bytes")]
    public long WindowsBytes { get; set; }

    [JsonPropertyName("appdata_bytes")]
    public long AppDataBytes { get; set; }

    [JsonPropertyName("user_bytes")]
    public long UserBytes { get; set; }

    [JsonPropertyName("program_bytes")]
    public long ProgramBytes { get; set; }

    [JsonPropertyName("protected_bytes")]
    public long ProtectedBytes { get; set; }
}

public sealed class ScanHistoryDiff
{
    public ScanHistoryEntry? Previous { get; init; }

    public ScanHistoryEntry? Latest { get; init; }

    public long TotalDelta => (Latest?.AttributedBytes ?? 0) - (Previous?.AttributedBytes ?? 0);

    public IReadOnlyList<(string Label, long Delta)> CategoryDeltas
    {
        get
        {
            if (Latest is null || Previous is null)
            {
                return Array.Empty<(string, long)>();
            }

            return new List<(string, long)>
            {
                ("系统核心", Latest.WindowsBytes - Previous.WindowsBytes),
                ("AppData", Latest.AppDataBytes - Previous.AppDataBytes),
                ("用户目录", Latest.UserBytes - Previous.UserBytes),
                ("安装目录", Latest.ProgramBytes - Previous.ProgramBytes),
                ("受保区域", Latest.ProtectedBytes - Previous.ProtectedBytes),
            }.OrderByDescending(pair => Math.Abs(pair.Item2)).ToList();
        }
    }
}

/// <summary>Append-only scan history under %LOCALAPPDATA%\TidyC\scan-history.json.</summary>
public sealed class ScanHistoryService
{
    private const int MaxEntries = 24;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    public ScanHistoryService(string? storagePath = null)
    {
        StoragePath = storagePath ?? Path.Combine(AppPaths.DataDirectory, "scan-history.json");
    }

    public string StoragePath { get; }

    public IReadOnlyList<ScanHistoryEntry> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(StoragePath))
            {
                return Array.Empty<ScanHistoryEntry>();
            }

            try
            {
                string json = File.ReadAllText(StoragePath);
                List<ScanHistoryEntry>? entries = JsonSerializer.Deserialize<List<ScanHistoryEntry>>(json, Json);
                return entries?.OrderByDescending(e => e.GeneratedAt).ToList() ?? new List<ScanHistoryEntry>();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return Array.Empty<ScanHistoryEntry>();
            }
        }
    }

    public ScanHistoryEntry Append(ScanResult scan)
    {
        var entry = new ScanHistoryEntry
        {
            ScanId = scan.ScanId,
            GeneratedAt = scan.GeneratedAt,
            Engine = scan.EngineType,
            DurationSeconds = scan.DurationSeconds,
            Files = scan.TotalFilesCount,
            Directories = scan.TotalDirectoriesCount,
            AttributedBytes = scan.Reconciliation.EngineAttributedBytes,
            UsedBytes = scan.Reconciliation.NtfsPhysicalUsedBytes,
            WindowsBytes = scan.Categories.WindowsSystemBytes,
            AppDataBytes = scan.Categories.AppDataBytes,
            UserBytes = scan.Categories.UserProfilesBytes,
            ProgramBytes = scan.Categories.ProgramFilesBytes,
            ProtectedBytes = scan.Categories.ProtectedSystemBytes,
        };

        lock (_gate)
        {
            try
            {
                var all = Load().ToList();
                all.Insert(0, entry);
                if (all.Count > MaxEntries)
                {
                    all = all.Take(MaxEntries).ToList();
                }

                string? directory = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(StoragePath, JsonSerializer.Serialize(all, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // History is best-effort telemetry; never fail a scan because of it.
            }
        }

        return entry;
    }

    public ScanHistoryDiff Diff()
    {
        IReadOnlyList<ScanHistoryEntry> entries = Load();
        return new ScanHistoryDiff
        {
            Latest = entries.ElementAtOrDefault(0),
            Previous = entries.ElementAtOrDefault(1),
        };
    }

    public string FormatDelta(long delta) => ByteSize.FormatSigned(delta) + (delta > 0 ? " ↑" : delta < 0 ? " ↓" : string.Empty);
}
