using TidyC.Core.Clean;

namespace TidyC.Core.Clean;

public sealed class QuarantineConfiguration
{
    public const string DefaultRoot = @"C:\TidyC_Quarantine";

    public string Root { get; init; } = DefaultRoot;

    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Files above this size skip SHA-256 (hashing a 30 GB file would stall the UI).</summary>
    public long HashSizeLimitBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Production keeps whitelist enforcement on; temp-dir tests turn it off.</summary>
    public bool EnforceWhitelist { get; init; } = true;

    /// <summary>
    /// Refuse paths under C:\Windows, Program Files and personal folders. Only a
    /// test harness pointing at a temp tree may turn this off.
    /// </summary>
    public bool EnforceProtectedPaths { get; init; } = true;
}

public sealed class QuarantineRequest
{
    public string Path { get; init; } = string.Empty;

    public FileIdentity? ExpectedIdentity { get; init; }
}

public sealed class QuarantineRecord
{
    public string OriginalPath { get; set; } = string.Empty;

    public string QuarantinePath { get; set; } = string.Empty;

    /// <summary>FileId fingerprint captured before the rename; verified again on restore.</summary>
    public string FileIndexHex { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256Hash { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public bool IsRestored { get; set; }

    public bool Purged { get; set; }

    public DateTime MovedAt { get; set; }

    public DateTime? RestoredAt { get; set; }

    public DateTime? PurgedAt { get; set; }

    public string? ConflictPath { get; set; }

    public string? LastError { get; set; }
}

public sealed class QuarantineBatch
{
    public static readonly TimeSpan DefaultRetentionPeriod = TimeSpan.FromDays(14);

    public string BatchId { get; set; } = $"BATCH_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.Add(DefaultRetentionPeriod);

    public string Description { get; set; } = string.Empty;

    public long TotalBytes { get; set; }

    public int ItemCount { get; set; }

    /// <summary>ACTIVE / RESTORED / PARTIAL / PURGED.</summary>
    public string Status { get; set; } = "ACTIVE";

    public double MoveDurationSeconds { get; set; }

    public DateTime? PurgedAt { get; set; }

    public List<QuarantineRecord> Records { get; set; } = new();

    public List<string> Skipped { get; set; } = new();

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAt;

    public long RemainingBytes => Records.Where(r => !r.IsRestored && !r.Purged).Sum(r => r.SizeBytes);

    public int RemainingDays(DateTime utcNow) =>
        Math.Max(0, (int)Math.Ceiling((ExpiresAt - utcNow).TotalDays));
}

public sealed class QuarantineRestoreResult
{
    public string BatchId { get; init; } = string.Empty;

    public int RestoredCount { get; set; }

    public int FailedCount { get; set; }

    public int SkippedCount { get; set; }

    public int ConflictCount { get; set; }

    public double DurationSeconds { get; set; }

    public List<string> Errors { get; } = new();

    public bool IsSuccess => FailedCount == 0 && Errors.Count == 0;
}

public sealed class QuarantinePurgeResult
{
    public string BatchId { get; init; } = string.Empty;

    public int PurgedCount { get; set; }

    public long FreedBytes { get; set; }

    public double DurationSeconds { get; set; }

    public List<string> Errors { get; } = new();

    public bool IsSuccess => Errors.Count == 0;
}

public sealed class QuarantineStatistics
{
    /// <summary>Bytes still sitting in the quarantine — recoverable, still consuming C:.</summary>
    public long StagedBytes { get; set; }

    /// <summary>Bytes physically freed by purge — the only number that means "disk got bigger".</summary>
    public long PurgedBytes { get; set; }

    public int ActiveBatchCount { get; set; }

    public int ExpiredBatchCount { get; set; }

    public int TotalBatchCount { get; set; }

    public int ActiveItemCount { get; set; }

    public DateTime? NextExpiryUtc { get; set; }

    public string Root { get; set; } = QuarantineConfiguration.DefaultRoot;
}
