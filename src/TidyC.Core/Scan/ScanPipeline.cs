using System.Text.Json;
using System.Text.Json.Serialization;
using TidyC.Core.Sys;

namespace TidyC.Core.Scan;

/// <summary>Live counters surfaced to the UI while a scan is running.</summary>
public sealed record ScanProgressSnapshot(
    string Phase,
    long RecordsRead,
    long Files,
    long Directories,
    long AttributedBytes,
    double ElapsedSeconds);

/// <summary>Accumulates engine output into the frozen <see cref="ScanResult"/> contract.</summary>
public sealed class ScanResultBuilder
{
    public ScanResultBuilder(string root, string engineType, VolumeFacts volume, ScanOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(options);

        RootPath = PathKey.Normalize(root);
        DriveRoot = Path.GetPathRoot(RootPath) ?? @"C:\";
        EngineType = engineType;
        Volume = volume;
        Options = options;
        TopFiles = new TopNCollector(options.TopFileCount);
        Root = new SpaceNode { Name = RootPath, FullPath = RootPath, Kind = NodeKind.Volume };
    }

    public string RootPath { get; }

    public string DriveRoot { get; }

    public string EngineType { get; }

    public VolumeFacts Volume { get; }

    public ScanOptions Options { get; }

    public SpaceNode Root { get; }

    public SpectrumAccumulator Spectrum { get; } = new();

    public TopNCollector TopFiles { get; }

    public List<string> Warnings { get; } = new();

    public long Files { get; set; }

    public long Directories { get; set; }

    public long AttributedBytes { get; set; }

    public long HardLinkDeduplicated { get; set; }

    public long HardLinkSavedBytes { get; set; }

    public long ReparsePointsSkipped { get; set; }

    public long AccessDenied { get; set; }

    public long WhitelistCleanableBytes { get; set; }

    public ScanResult Build(double durationSeconds, long recordsRead)
    {
        Root.Aggregate();
        TreeOps.ApplyCategories(Root, DriveRoot);

        var result = new ScanResult
        {
            ScanId = $"SCAN_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
            GeneratedAt = DateTime.UtcNow,
            DriveRoot = DriveRoot,
            VolumeSerialNumber = Volume.SerialHex,
            ClusterSizeBytes = Volume.ClusterSizeBytes,
            EngineType = EngineType,
            DurationSeconds = Math.Round(durationSeconds, 3),
            IsElevated = ElevationHelper.IsElevated,
            TotalRecordsRead = recordsRead,
            TotalFilesCount = Files,
            TotalDirectoriesCount = Directories,
            HardLinkDeduplicatedCount = HardLinkDeduplicated,
            HardLinkSavedBytes = HardLinkSavedBytes,
            ReparsePointsSkippedCount = ReparsePointsSkipped,
            AccessDeniedCount = AccessDenied,
            Categories = Spectrum.ToSpectrum(),
            Root = Root,
            Volume = Volume,
        };

        result.TopLargestFiles = TopFiles.Snapshot();
        result.TopLargestFolders = TreeOps.TopDirectories(Root, Options.TopFolderCount);
        // Directories carry their own index allocation in the MFT tree, so the
        // root total is the honest attributed figure; the file-only counter can
        // be slightly lower.
        long attributed = Math.Max(AttributedBytes, Root.AllocatedBytes);
        result.Reconciliation = TreeOps.Reconcile(Volume, attributed, WhitelistCleanableBytes);
        result.Warnings.AddRange(Warnings);
        return result;
    }
}

/// <summary>
/// Engine selection with honest fallback: MFT needs admin + NTFS, otherwise the
/// multithreaded directory walker runs and the UI says so.
/// </summary>
public sealed class ScanService
{
    private readonly IFileSystemProbe? _probe;

    public ScanService(IFileSystemProbe? probe = null) => _probe = probe;

    public static bool CanUseMft(string root) => MftScanner.IsAvailableFor(root);

    public async Task<ScanResult> ScanAsync(
        string root,
        ScanOptions options,
        CancellationToken ct = default,
        IProgress<ScanProgressSnapshot>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);

        if (options.PreferMft && MftScanner.IsAvailableFor(root))
        {
            try
            {
                return await new MftScanner().ScanAsync(root, options, ct, progress).ConfigureAwait(false);
            }
            catch (MftUnavailableException ex)
            {
                ScanResult fallback = await new DirectoryTreeWalker(_probe)
                    .ScanAsync(root, options, ct, progress)
                    .ConfigureAwait(false);
                fallback.Warnings.Add("MFT 直读不可用，已自动回退目录遍历引擎：" + ex.Message);
                return fallback;
            }
        }

        ScanResult walked = await new DirectoryTreeWalker(_probe)
            .ScanAsync(root, options, ct, progress)
            .ConfigureAwait(false);

        walked.Warnings.Add(MftScanner.IsAvailableFor(root)
            ? "按用户设置使用目录遍历引擎"
            : "未提权或非 NTFS：本次使用目录遍历引擎（无 FRN 硬链接去重）");
        return walked;
    }
}

public sealed class SelfTestReport
{
    [JsonPropertyName("generated_at")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; } = 0.9999;

    [JsonPropertyName("mft_engine")]
    public string MftEngine { get; set; } = ScanEngines.MftStreamReader;

    [JsonPropertyName("walker_engine")]
    public string WalkerEngine { get; set; } = ScanEngines.DirectoryTreeWalker;

    [JsonPropertyName("mft_files")]
    public long MftFiles { get; set; }

    [JsonPropertyName("walker_files")]
    public long WalkerFiles { get; set; }

    [JsonPropertyName("mft_directories")]
    public long MftDirectories { get; set; }

    [JsonPropertyName("walker_directories")]
    public long WalkerDirectories { get; set; }

    [JsonPropertyName("mft_bytes")]
    public long MftBytes { get; set; }

    [JsonPropertyName("walker_bytes")]
    public long WalkerBytes { get; set; }

    [JsonPropertyName("file_consistency")]
    public double FileConsistency { get; set; }

    [JsonPropertyName("byte_consistency")]
    public double ByteConsistency { get; set; }

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    public override string ToString()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"双引擎对拍自检 · {Root} · {(Passed ? "PASS" : "FAIL")}");
        builder.AppendLine($"  阈值 {Threshold:P2}：文件数一致性 {FileConsistency:P4}，字节一致性 {ByteConsistency:P4}");
        builder.AppendLine($"  MFT    : {MftFiles:N0} 文件 / {MftDirectories:N0} 目录 / {MftBytes:N0} 字节");
        builder.AppendLine($"  遍历   : {WalkerFiles:N0} 文件 / {WalkerDirectories:N0} 目录 / {WalkerBytes:N0} 字节");
        foreach (string note in Notes)
        {
            builder.AppendLine("  - " + note);
        }

        return builder.ToString();
    }
}

/// <summary>MFT reader vs directory walker metadata cross-check (--self-test).</summary>
public static class DualEngineSelfTest
{
    public static async Task<SelfTestReport> RunAsync(
        string root,
        ScanOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ScanOptions();
        ScanResult mft = await new MftScanner().ScanAsync(root, options, ct).ConfigureAwait(false);
        ScanResult walker = await new DirectoryTreeWalker().ScanAsync(root, options, ct).ConfigureAwait(false);
        return Compare(mft, walker, options);
    }

    public static SelfTestReport Compare(ScanResult mft, ScanResult walker, ScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mft);
        ArgumentNullException.ThrowIfNull(walker);
        options ??= new ScanOptions();

        double fileConsistency = Ratio(mft.TotalFilesCount, walker.TotalFilesCount);
        double byteConsistency = Ratio(mft.Reconciliation.EngineAttributedBytes, walker.Reconciliation.EngineAttributedBytes);

        var report = new SelfTestReport
        {
            Root = walker.DriveRoot,
            MftFiles = mft.TotalFilesCount,
            WalkerFiles = walker.TotalFilesCount,
            MftDirectories = mft.TotalDirectoriesCount,
            WalkerDirectories = walker.TotalDirectoriesCount,
            MftBytes = mft.Reconciliation.EngineAttributedBytes,
            WalkerBytes = walker.Reconciliation.EngineAttributedBytes,
            FileConsistency = fileConsistency,
            ByteConsistency = byteConsistency,
            Passed = fileConsistency >= 0.9999 && byteConsistency >= 0.9999,
        };

        if (mft.TotalFilesCount != walker.TotalFilesCount)
        {
            report.Notes.Add($"文件数差异 {mft.TotalFilesCount - walker.TotalFilesCount:N0}（MFT 含系统元数据记录，遍历受权限限制）");
        }

        if (mft.Reconciliation.EngineAttributedBytes != walker.Reconciliation.EngineAttributedBytes)
        {
            report.Notes.Add(
                $"字节差异 {mft.Reconciliation.EngineAttributedBytes - walker.Reconciliation.EngineAttributedBytes:N0} " +
                "（MFT 计入 $MFT 元数据与硬链接去重，遍历按簇向上取整）");
        }

        if (mft.DurationSeconds > 0 && walker.DurationSeconds > 0)
        {
            report.Notes.Add($"耗时：MFT {mft.DurationSeconds:0.00}s，遍历 {walker.DurationSeconds:0.00}s");
        }

        return report;
    }

    private static double Ratio(long left, long right)
    {
        long max = Math.Max(Math.Abs(left), Math.Abs(right));
        return max == 0 ? 1d : 1d - (Math.Abs(left - right) / (double)max);
    }

    public static string Serialize(SelfTestReport report) => JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
