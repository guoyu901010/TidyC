using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TidyC.App.Controls;
using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public sealed class SpectrumLegendItem
{
    public required string Label { get; init; }

    public required string BytesText { get; init; }

    public required System.Windows.Media.Brush Brush { get; init; }
}

public sealed class TopFolderRow
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string SizeText { get; init; }

    public long SizeBytes { get; init; }

    public long MaxBytes { get; init; }

    public double Ratio { get; init; }

    public required System.Windows.Media.Brush Brush { get; init; }

    public required string CategoryLabel { get; init; }
}

public partial class OverviewViewModel : ViewModelBase
{
    private readonly IAppShell _shell;

    [ObservableProperty]
    private string _volumeTitle = "C: 本地系统卷";

    [ObservableProperty]
    private string _volumeSubtitle = "卷序列号 — · 簇大小 — · FRN 去重待命 · 反 TOCTOU 竞争防护";

    [ObservableProperty]
    private string _engineBadge = "等待首次扫描";

    [ObservableProperty]
    private bool _engineReady;

    [ObservableProperty]
    private string _physicalUsedText = "—";

    [ObservableProperty]
    private string _physicalDetailText = "扫描后显示 NTFS 真实占用";

    [ObservableProperty]
    private string _attributedText = "—";

    [ObservableProperty]
    private string _attributedDetailText = "引擎归因与硬链接去重";

    [ObservableProperty]
    private string _attributionPercent = "—";

    [ObservableProperty]
    private double _attributionRatio;

    [ObservableProperty]
    private string _blindspotText = "—";

    [ObservableProperty]
    private string _blindspotDetailText = "ACL 保护的目录与系统元数据";

    [ObservableProperty]
    private string _cleanableText = "—";

    [ObservableProperty]
    private string _cleanableDetailText = "白名单可安全释放（14 天可逆）";

    [ObservableProperty]
    private string _freeText = "—";

    [ObservableProperty]
    private string _capacityText = "总盘容量 —";

    [ObservableProperty]
    private string _scannedAtText = "尚未扫描";

    [ObservableProperty]
    private bool _hasScan;

    [ObservableProperty]
    private bool _hasRecommendation;

    [ObservableProperty]
    private string _recommendationTitle = string.Empty;

    [ObservableProperty]
    private string _recommendationDetail = string.Empty;

    [ObservableProperty]
    private string _hardLinkText = "硬链接去重节省 —";

    [ObservableProperty]
    private string _fileSummaryText = "— 个文件 · — 个目录";

    [ObservableProperty]
    private string _durationText = "—";

    public OverviewViewModel(IAppShell shell)
    {
        _shell = shell;
        ScanCommand = new AsyncRelayCommand(shell.ScanAsync, () => !Services.IsDemo || true);
    }

    public ObservableCollection<SpectrumSegment> Spectrum { get; } = new();

    public ObservableCollection<SpectrumLegendItem> Legend { get; } = new();

    public ObservableCollection<TopFolderRow> TopFolders { get; } = new();

    public IAsyncRelayCommand ScanCommand { get; }

    public void Refresh()
    {
        ScanResult? scan = Services.Scan;
        HasScan = scan is not null;

        if (scan is null)
        {
            EngineBadge = ScanService.CanUseMft(@"C:\") ? "MFT 直读引擎待命 · 点击扫描" : "目录遍历引擎待命 · 建议提权启用 MFT";
            EngineReady = ScanService.CanUseMft(@"C:\");
            PhysicalUsedText = "0 B";
            AttributedText = "0 B";
            BlindspotText = "0 B";
            CleanableText = Services.Plan is null ? "0 B" : Size(Services.Plan.TotalBytes);
            FreeText = Size(GetVolumeFree());
            CapacityText = "总盘容量 " + Size(GetVolumeTotal());
            ScannedAtText = "尚未扫描 · 点击右上角开始";
            FileSummaryText = "等待扫描";
            DurationText = "—";
            AttributionPercent = "0.0%";
            AttributionRatio = 0;
            Spectrum.Clear();
            Legend.Clear();
            TopFolders.Clear();
            HasRecommendation = Services.Plan is { IsEmpty: false };
            if (HasRecommendation)
            {
                RecommendationTitle = $"检测到 {Size(Services.Plan!.TotalBytes)} 白名单无害缓存，建议执行安全清理";
                RecommendationDetail = "所有文件将以零 I/O 瞬时同卷改名暂存至隔离区，享受 14 天完整可逆保护。";
            }

            return;
        }

        VolumeReconciliation reconciliation = scan.Reconciliation;
        EngineReady = scan.EngineType == ScanEngines.MftStreamReader;
        EngineBadge = EngineReady
            ? "● NTFS 驱动已就绪 · MFT 直读引擎"
            : "● 目录遍历引擎 · 未提权（建议以管理员重启）";

        VolumeSubtitle =
            $"卷序列号 {scan.VolumeSerialNumber} · 簇大小 {scan.ClusterSizeBytes:N0} 字节 · " +
            $"FRN 去重已激活 · 反 TOCTOU 竞争防护 · {scan.EngineType}";

        PhysicalUsedText = Size(reconciliation.NtfsPhysicalUsedBytes);
        PhysicalDetailText = $"总盘容量 {Size(reconciliation.TotalVolumeBytes)}（剩余 {Size(Math.Max(0, scan.Volume.FreeBytes))}）";

        AttributedText = Size(reconciliation.EngineAttributedBytes);
        AttributedDetailText = $"硬链接去重节省 {Size(scan.HardLinkSavedBytes)}（FRN 映射）";
        HardLinkText = $"硬链接去重节省 {Size(scan.HardLinkSavedBytes)} · 去重 {scan.HardLinkDeduplicatedCount:N0} 次";

        double ratio = reconciliation.NtfsPhysicalUsedBytes > 0
            ? Math.Clamp(reconciliation.EngineAttributedBytes / (double)reconciliation.NtfsPhysicalUsedBytes, 0, 1)
            : 0;
        AttributionRatio = ratio;
        AttributionPercent = (ratio * 100).ToString("0.0") + "%";

        BlindspotText = Size(reconciliation.PermissionBlindspotBytes);
        BlindspotDetailText = $"System Volume Information 等 ACL 盲区 · 拒绝访问 {scan.AccessDeniedCount:N0} 处";

        long cleanable = Services.Plan?.TotalBytes ?? reconciliation.WhitelistSafeCleanableBytes;
        CleanableText = Size(cleanable);
        CleanableDetailText = Services.Plan is { IsEmpty: false }
            ? $"含 AppData 缓存、系统日志、转储（{Services.Plan.Candidates.Count} 项）"
            : "当前未发现白名单可清理项";

        FreeText = Size(Math.Max(0, scan.Volume.FreeBytes));
        CapacityText = $"总盘容量 {Size(scan.Volume.TotalBytes)}";
        ScannedAtText = $"{scan.GeneratedAt:yyyy-MM-dd HH:mm:ss} · 引擎 {scan.EngineType}";
        FileSummaryText = $"{scan.TotalFilesCount:N0} 个文件 · {scan.TotalDirectoriesCount:N0} 个目录";
        DurationText = $"{scan.DurationSeconds:0.00}s";
        AttributionPercent = (ratio * 100).ToString("0.0") + "%";

        BuildSpectrum(scan);
        BuildTopFolders(scan);

        HasRecommendation = cleanable > 0;
        if (HasRecommendation)
        {
            RecommendationTitle = $"检测到 {Size(cleanable)} 白名单无害缓存与转储数据，建议执行安全清理";
            RecommendationDetail = "所有文件将以零 I/O 瞬时同卷改名暂存至隔离区，享受 14 天完整可逆保护。";
        }
    }

    private void BuildSpectrum(ScanResult scan)
    {
        Spectrum.Clear();
        Legend.Clear();
        StorageSpectrum spectrum = scan.Categories;

        void Add(string category, string label, long bytes, string brushKey)
        {
            if (bytes <= 0)
            {
                return;
            }

            var brush = (System.Windows.Media.Brush)Application.Current.FindResource(brushKey);
            Spectrum.Add(new SpectrumSegment { Category = category, Label = label, Bytes = bytes });
            Legend.Add(new SpectrumLegendItem { Label = label, BytesText = Size(bytes), Brush = brush });
        }

        Add(StorageCategories.WindowsSystem, "系统核心", spectrum.WindowsSystemBytes, "Brush.Spectrum.Windows");
        Add(StorageCategories.AppData, "AppData", spectrum.AppDataBytes, "Brush.Spectrum.AppData");
        Add(StorageCategories.UserProfiles, "用户目录", spectrum.UserProfilesBytes, "Brush.Spectrum.UserProfiles");
        Add(StorageCategories.ProgramFiles, "安装目录", spectrum.ProgramFilesBytes, "Brush.Spectrum.ProgramFiles");
        Add(StorageCategories.ProtectedSystem, "受保区域", spectrum.ProtectedSystemBytes, "Brush.Spectrum.ProtectedSystem");
        Add(StorageCategories.Quarantine, "隔离区", spectrum.QuarantineBytes, "Brush.Spectrum.Quarantine");
        Add(StorageCategories.Other, "其他", spectrum.OtherBytes, "Brush.Spectrum.Other");
    }

    private void BuildTopFolders(ScanResult scan)
    {
        TopFolders.Clear();
        long max = Math.Max(1, scan.Root.Children.Count == 0 ? 1 : scan.Root.Children.Max(c => c.AllocatedBytes));
        foreach (SpaceNode child in scan.Root.Children.OrderByDescending(c => c.AllocatedBytes).Take(6))
        {
            string brushKey = child.Category switch
            {
                StorageCategories.WindowsSystem => "Brush.Spectrum.Windows",
                StorageCategories.AppData => "Brush.Spectrum.AppData",
                StorageCategories.UserProfiles => "Brush.Spectrum.UserProfiles",
                StorageCategories.ProgramFiles => "Brush.Spectrum.ProgramFiles",
                StorageCategories.ProtectedSystem => "Brush.Spectrum.ProtectedSystem",
                StorageCategories.Quarantine => "Brush.Spectrum.Quarantine",
                _ => "Brush.Spectrum.Other",
            };

            TopFolders.Add(new TopFolderRow
            {
                Name = child.Name,
                Path = child.FullPath,
                SizeText = Size(child.AllocatedBytes),
                SizeBytes = child.AllocatedBytes,
                MaxBytes = max,
                Ratio = child.AllocatedBytes / (double)max,
                Brush = (System.Windows.Media.Brush)Application.Current.FindResource(brushKey),
                CategoryLabel = child.Category switch
                {
                    StorageCategories.WindowsSystem => "系统",
                    StorageCategories.AppData => "AppData",
                    StorageCategories.UserProfiles => "用户",
                    StorageCategories.ProgramFiles => "安装",
                    StorageCategories.ProtectedSystem => "受保",
                    StorageCategories.Quarantine => "隔离区",
                    _ => "其他",
                },
            });
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        ScanResult? scan = Services.Scan;
        if (scan is null)
        {
            _shell.ShowToast("还没有扫描结果可导出");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出物理对账报告",
            Filter = "JSON 报告 (*.json)|*.json|JSONL 流水 (*.jsonl)|*.jsonl",
            FileName = $"tidyc-reconciliation-{DateTime.Now:yyyyMMdd_HHmmss}.json",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var payload = new
        {
            scan = scan,
            appdata = Services.AppDataReport,
            cleanup_plan = Services.Plan is null
                ? null
                : new
                {
                    green_bytes = Services.Plan.GreenBytes,
                    yellow_bytes = Services.Plan.YellowBytes,
                    total_bytes = Services.Plan.TotalBytes,
                    candidates = Services.Plan.Candidates.Select(c => new
                    {
                        rule_id = c.Rule.Id,
                        path = c.Path,
                        bytes = c.SizeBytes,
                        risk = c.Rule.Risk.ToContractString(),
                    }),
                },
            ruleset_signature = RuleValidator.ComputeSignature(BuiltInRules.All),
            exported_at = DateTime.Now,
        };

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(payload, options));
            _shell.ShowToast("对账报告已导出：" + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast("导出失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenWizard() => _shell.StartCleanup(null);

    [RelayCommand]
    private void OpenQuarantine() => _shell.Navigate("quarantine");

    private static long GetVolumeTotal()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
            return drive.TotalSize;
        }
        catch (Exception)
        {
            return 512L * 1024 * 1024 * 1024;
        }
    }

    private static long GetVolumeFree()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
            return drive.AvailableFreeSpace;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
