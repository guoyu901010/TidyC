using System.Text.Json.Serialization;

namespace TidyC.Core.Scan;

/// <summary>
/// Top level factual model of one scan. All byte figures are disk-allocated
/// bytes (cluster aligned) unless the property name says otherwise, so the
/// reconciliation against the NTFS volume stays honest.
/// </summary>
public sealed class ScanResult
{
    [JsonPropertyName("scan_id")]
    public string ScanId { get; set; } = $"SCAN_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("drive_root")]
    public string DriveRoot { get; set; } = "C:\\";

    [JsonPropertyName("volume_serial_number")]
    public string VolumeSerialNumber { get; set; } = "0x00000000";

    [JsonPropertyName("cluster_size_bytes")]
    public uint ClusterSizeBytes { get; set; } = 4096;

    [JsonPropertyName("engine_type")]
    public string EngineType { get; set; } = ScanEngines.MftStreamReader;

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("reconciliation")]
    public VolumeReconciliation Reconciliation { get; set; } = new();

    [JsonPropertyName("categories")]
    public StorageSpectrum Categories { get; set; } = new();

    [JsonPropertyName("total_records_read")]
    public long TotalRecordsRead { get; set; }

    [JsonPropertyName("total_files_count")]
    public long TotalFilesCount { get; set; }

    [JsonPropertyName("total_directories_count")]
    public long TotalDirectoriesCount { get; set; }

    [JsonPropertyName("hard_link_deduplicated_count")]
    public long HardLinkDeduplicatedCount { get; set; }

    [JsonPropertyName("hard_link_saved_bytes")]
    public long HardLinkSavedBytes { get; set; }

    [JsonPropertyName("reparse_points_skipped_count")]
    public long ReparsePointsSkippedCount { get; set; }

    [JsonPropertyName("access_denied_count")]
    public long AccessDeniedCount { get; set; }

    [JsonPropertyName("is_elevated")]
    public bool IsElevated { get; set; }

    [JsonPropertyName("top_largest_files")]
    public List<FileFact> TopLargestFiles { get; set; } = new();

    [JsonPropertyName("top_largest_folders")]
    public List<FolderFact> TopLargestFolders { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    /// <summary>In-memory space tree. Never serialised: it can hold millions of nodes.</summary>
    [JsonIgnore]
    public SpaceNode Root { get; set; } = new() { Name = "C:\\", FullPath = "C:\\", Kind = NodeKind.Volume };

    [JsonIgnore]
    public VolumeFacts Volume { get; set; } = new();
}

public sealed class VolumeReconciliation
{
    [JsonPropertyName("total_volume_bytes")]
    public long TotalVolumeBytes { get; set; } = 512L * 1024 * 1024 * 1024;

    [JsonPropertyName("ntfs_physical_used_bytes")]
    public long NtfsPhysicalUsedBytes { get; set; }

    [JsonPropertyName("engine_attributed_bytes")]
    public long EngineAttributedBytes { get; set; }

    [JsonPropertyName("permission_blindspot_bytes")]
    public long PermissionBlindspotBytes { get; set; }

    [JsonPropertyName("whitelist_safe_cleanable_bytes")]
    public long WhitelistSafeCleanableBytes { get; set; }

    [JsonPropertyName("attribution_ratio")]
    public double AttributionRatio => NtfsPhysicalUsedBytes <= 0
        ? 0d
        : Math.Round((double)EngineAttributedBytes / NtfsPhysicalUsedBytes, 6);

    [JsonPropertyName("reconciled")]
    public bool Reconciled => PermissionBlindspotBytes >= 0 && EngineAttributedBytes <= NtfsPhysicalUsedBytes * 1.02 + (64L * 1024 * 1024);
}

public sealed class StorageSpectrum
{
    [JsonPropertyName("windows_system_bytes")]
    public long WindowsSystemBytes { get; set; }

    [JsonPropertyName("appdata_bytes")]
    public long AppDataBytes { get; set; }

    [JsonPropertyName("user_profiles_bytes")]
    public long UserProfilesBytes { get; set; }

    [JsonPropertyName("program_files_bytes")]
    public long ProgramFilesBytes { get; set; }

    [JsonPropertyName("protected_system_bytes")]
    public long ProtectedSystemBytes { get; set; }

    [JsonPropertyName("quarantine_bytes")]
    public long QuarantineBytes { get; set; }

    [JsonPropertyName("other_bytes")]
    public long OtherBytes { get; set; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes => WindowsSystemBytes + AppDataBytes + UserProfilesBytes +
                              ProgramFilesBytes + ProtectedSystemBytes + QuarantineBytes + OtherBytes;
}

public sealed class FileFact
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("allocated_bytes")]
    public long AllocatedBytes { get; set; }

    [JsonPropertyName("logical_bytes")]
    public long LogicalBytes { get; set; }

    [JsonPropertyName("last_write_utc")]
    public DateTime LastWriteUtc { get; set; }

    [JsonPropertyName("file_id")]
    public string FileId { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = StorageCategories.Other;
}

public sealed class FolderFact
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("allocated_bytes")]
    public long AllocatedBytes { get; set; }

    [JsonPropertyName("file_count")]
    public long FileCount { get; set; }

    [JsonPropertyName("directory_count")]
    public long DirectoryCount { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = StorageCategories.Other;
}

public sealed class VolumeFacts
{
    public string Root { get; set; } = "C:\\";
    public string FileSystem { get; set; } = "NTFS";
    public uint SerialNumber { get; set; }
    public uint ClusterSizeBytes { get; set; } = 4096;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsNtfs { get; set; } = true;
    public bool NtfsVolumeDataAvailable { get; set; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public string SerialHex => $"0x{SerialNumber:X8}";
}

public static class ScanEngines
{
    public const string MftStreamReader = "MFT_STREAM_READER";
    public const string DirectoryTreeWalker = "DIRECTORY_TREE_WALKER";
}

public enum NodeKind
{
    Volume,
    Directory,
    File,
    ReparsePoint,
}

public static class StorageCategories
{
    public const string WindowsSystem = "WindowsSystem";
    public const string AppData = "AppData";
    public const string UserProfiles = "UserProfiles";
    public const string ProgramFiles = "ProgramFiles";
    public const string ProtectedSystem = "ProtectedSystem";
    public const string Quarantine = "Quarantine";
    public const string Other = "Other";

    public static readonly string[] All =
    {
        WindowsSystem, AppData, UserProfiles, ProgramFiles, ProtectedSystem, Quarantine, Other,
    };
}
