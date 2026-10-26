using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyC.App.Services;
using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IAppShell
{
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _activePage = "overview";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText = "就绪 · 尚未扫描";

    [ObservableProperty]
    private string _scanStage = string.Empty;

    [ObservableProperty]
    private string _scanDetailText = string.Empty;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastText = string.Empty;

    [ObservableProperty]
    private string _engineText = "等待扫描";

    [ObservableProperty]
    private string _elevationText = string.Empty;

    public MainViewModel()
    {
        Overview = new OverviewViewModel(this);
        Treemap = new TreemapViewModel(this);
        LargeFiles = new LargeFilesViewModel(this);
        AppData = new AppDataViewModel(this);
        Wizard = new CleanWizardViewModel(this);
        Quarantine = new QuarantineViewModel(this);
        Records = new ScanRecordsViewModel(this);

        Navigation = new ObservableCollection<NavigationItem>
        {
            new() { Key = "overview", Label = "总览", Glyph = "\uE80F", Hint = "物理对账与分类全息谱图" },
            new() { Key = "treemap", Label = "占用树图", Glyph = "\uE8A9", Hint = "Squarified 矩形树图下钻" },
            new() { Key = "large", Label = "大文件", Glyph = "\uE8B7", Hint = "Top 100 巨石文件与 Top 50 文件夹" },
            new() { Key = "appdata", Label = "AppData", Glyph = "\uE7C3", Hint = "三分支四级安全判定" },
            new() { Key = "clean", Label = "安全清理", Glyph = "\uE74D", Hint = "白名单向导与同卷原子搬移" },
            new() { Key = "quarantine", Label = "隔离区", Glyph = "\uE72E", Hint = "14 天可逆批次与一键还原" },
            new() { Key = "records", Label = "扫描记录", Glyph = "\uE81C", Hint = "谁在长胖 · 增量差值对比" },
        };

        _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        _currentPage = Overview;
        ActivePage = "overview";
        RefreshAll();

        ElevationText = Services.IsElevated ? "管理员 · 特权通道已就绪" : "标准权限 · 可在需要时提权";

        if (!TidyC.App.App.CaptureMode && !Services.IsRuleSetValid)
        {
            ShowToast("规则库校验失败：" + (Services.RuleError ?? string.Empty).Split('\n')[0]);
        }
        else if (!TidyC.App.App.CaptureMode && Services.IsDemo)
        {
            ShowToast("演示数据已加载：所有数字为示例，可安全浏览全部视图");
        }

        if (Services.IsDemo && Services.Scan is { } demoScan)
        {
            StatusText =
                $"演示快照 · {demoScan.TotalFilesCount:N0} 个文件 · " +
                $"{Size(demoScan.Reconciliation.EngineAttributedBytes)} 已归因 · 未访问真实磁盘";
            ScanProgress = 100;
        }

        if (Services.QuarantineWarning is not null)
        {
            StatusText = "隔离区已回退：" + Services.Quarantine.Root;
        }
    }

    public OverviewViewModel Overview { get; }

    public TreemapViewModel Treemap { get; }

    public LargeFilesViewModel LargeFiles { get; }

    public AppDataViewModel AppData { get; }

    public CleanWizardViewModel Wizard { get; }

    public QuarantineViewModel Quarantine { get; }

    public ScanRecordsViewModel Records { get; }

    public ObservableCollection<NavigationItem> Navigation { get; }

    public bool IsDemo => Services.IsDemo;

    public bool IsElevated => Services.IsElevated;

    public bool HasRuleError => !Services.IsRuleSetValid;

    public string RuleError => Services.RuleError ?? string.Empty;

    public string QuarantineRoot => Services.Quarantine.Root;

    public string VersionText => "TidyC 2.4.0 · .NET 8 WPF";

    public bool HasScan => Services.Scan is not null;

    partial void OnActivePageChanged(string value)
    {
        CurrentPage = value switch
        {
            "treemap" => Treemap,
            "large" => LargeFiles,
            "appdata" => AppData,
            "clean" => Wizard,
            "quarantine" => Quarantine,
            "records" => Records,
            _ => Overview,
        };

        if (value == "quarantine")
        {
            Quarantine.Refresh();
        }
        else if (value == "records")
        {
            Records.Refresh();
        }
        else if (value == "clean")
        {
            Wizard.Refresh();
        }
    }

    public void Navigate(string page) => ActivePage = page;

    public void ShowToast(string message)
    {
        if (TidyC.App.App.CaptureMode)
        {
            return;
        }

        ToastText = message;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void StartCleanup(IReadOnlyList<CleanupCandidate>? candidates)
    {
        Wizard.LoadSelection(candidates);
        Navigate("clean");
    }

    private CancellationTokenSource? _scanCts;

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsScanning || !Services.IsRuleSetValid)
        {
            return;
        }

        IsScanning = true;
        ScanProgress = 0;
        ScanDetailText = string.Empty;
        StatusText = "正在扫描 C:（只读，不写入任何文件）…";
        using var scanCts = new CancellationTokenSource();
        _scanCts = scanCts;
        try
        {
            if (Services.IsDemo)
            {
                ScanStage = "正在装载演示快照…";
                ScanProgress = 35;
                await Task.Delay(420);
                ScanProgress = 80;
                Services.LoadDemoSnapshot();
                ScanProgress = 100;
                StatusText = "演示快照已载入 · 未访问真实磁盘";
                ScanStage = string.Empty;
                RefreshAll();
                ShowToast("演示快照已重新载入");
                return;
            }

            bool mft = MftScanner.IsAvailableFor(@"C:\");
            ScanStage = mft
                ? "正在顺序直读 $MFT 元数据…"
                : "未提权：正在多线程目录遍历（提权后可用 MFT 直读，更快）…";
            ScanDetailText = string.Empty;
            ScanProgress = 12;

            var progress = new Progress<ScanProgressSnapshot>(snapshot =>
            {
                ScanDetailText = snapshot.RecordsRead > 0 && snapshot.Files == 0
                    ? $"{snapshot.Phase} · {snapshot.RecordsRead:N0} 条记录 · {snapshot.ElapsedSeconds:0.0}s"
                    : $"{snapshot.Phase} · {snapshot.Files:N0} 文件 · {Size(snapshot.AttributedBytes)} · {snapshot.ElapsedSeconds:0.0}s";
            });

            ScanResult scan = await Services.Scanner.ScanAsync(@"C:\", new ScanOptions(), scanCts.Token, progress);
            ScanProgress = 55;
            ScanStage = "正在生成 AppData 四级安全判定…";
            AppDataAnalysisReport report = await Task.Run(
                () => Services.AppData.Analyze(new AppDataScanRequest(), ct: scanCts.Token),
                scanCts.Token);
            ScanProgress = 80;

            ScanStage = "正在核对白名单可安全释放量…";
            CleanupPlan plan = await Task.Run(() => Services.Planner.Build(scanCts.Token), scanCts.Token);
            ScanProgress = 96;

            Services.ApplyScan(scan, report, plan);
            Services.History.Append(scan);

            ScanProgress = 100;
            ScanDetailText =
                $"{scan.TotalFilesCount:N0} 文件 · {Size(scan.Reconciliation.EngineAttributedBytes)} · {scan.DurationSeconds:0.00}s";
            StatusText =
                $"扫描完成 · {scan.TotalFilesCount:N0} 个文件 / {scan.TotalDirectoriesCount:N0} 个目录 · " +
                $"{Size(scan.Reconciliation.EngineAttributedBytes)} 已归因 · {scan.DurationSeconds:0.00}s";
            ScanStage = string.Empty;
            RefreshAll();
            ShowToast($"扫描完成，白名单可释放 {Size(plan.TotalBytes)}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消 · 未写入任何扫描结果";
            ScanStage = string.Empty;
            ScanDetailText = string.Empty;
            ShowToast("扫描已取消");
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
            ScanStage = string.Empty;
            ScanDetailText = string.Empty;
            ShowToast("扫描失败：" + ex.Message);
        }
        finally
        {
            _scanCts = null;
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        try
        {
            _scanCts?.Cancel();
            ShowToast("正在取消扫描…");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool _elevating;

    [RelayCommand]
    private async Task ElevateAsync()
    {
        if (_elevating)
        {
            return;
        }

        if (Services.IsElevated)
        {
            ShowToast("当前已是管理员，MFT 直读与特权清理全部可用");
            return;
        }

        _elevating = true;
        try
        {
            string marker = Path.Combine(AppPaths.DataDirectory, "elevated-started.json");
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                if (File.Exists(marker))
                {
                    File.Delete(marker);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            string elevationArguments = $"--elevated --marker \"{marker}\"" +
                                        (Services.IsDemo ? " --demo" : string.Empty);
            ElevationHelper.ElevationAttempt attempt = ElevationHelper.RelaunchElevated(elevationArguments);
            if (!attempt.Started)
            {
                StatusText = "提权失败：" + attempt.Message;
                string target = string.IsNullOrEmpty(attempt.ExecutablePath) ? "TidyC.exe" : attempt.ExecutablePath;
                MessageBox.Show(
                    $"无法以管理员身份重新启动：\n{attempt.Message}\n\n" +
                    "可以手动操作：\n" +
                    "1. 关闭当前 TidyC 窗口\n" +
                    $"2. 右键 {target}\n" +
                    "3. 选择「以管理员身份运行」\n\n" +
                    "提权后才会启用 MFT 直读（秒级全盘）、特权清理与自检对拍。",
                    "TidyC 提权",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            StatusText = "已请求管理员权限，正在等待管理员实例启动…";
            ShowToast("已请求 UAC，请在弹窗中确认");

            // The elevated instance writes this marker as soon as it starts; only
            // then is it safe to close the standard-privilege window.
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                if (File.Exists(marker))
                {
                    StatusText = "管理员实例已启动，正在切换…";
                    ShowToast("管理员实例已启动，正在关闭当前标准权限窗口…");
                    await Task.Delay(900);
                    Application.Current.Shutdown(0);
                    return;
                }
            }

            StatusText = "提权实例未在预期时间内启动";
            MessageBox.Show(
                "已请求管理员权限，但管理员实例未在 25 秒内启动。\n\n" +
                "常见原因：UAC 被取消、被安全软件拦截，或单文件版的解压目录不可写。\n" +
                $"可手动操作：右键 {attempt.ExecutablePath} → 以管理员身份运行。\n\n" +
                "若使用单文件版，可改用同目录下的 TidyC.cmd（会设置可写的解压目录）后重试。",
                "TidyC 提权",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _elevating = false;
        }
    }

    [RelayCommand]
    private void OpenQuarantine() => Navigate("quarantine");

    [RelayCommand]
    private void OpenRules()
    {
        MessageBox.Show(
            Services.RuleReport.ToText(),
            "内置白名单规则集校验",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public void RefreshAll()
    {
        EngineText = Services.EngineBadge;
        Overview.Refresh();
        Treemap.Refresh();
        LargeFiles.Refresh();
        AppData.Refresh();
        Wizard.Refresh();
        Quarantine.Refresh(force: true);
        Records.Refresh(force: true);
        OnPropertyChanged(nameof(HasScan));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(IsElevated));
        OnPropertyChanged(nameof(HasRuleError));
        OnPropertyChanged(nameof(QuarantineRoot));
    }
}
