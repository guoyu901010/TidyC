using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyC.App.Services;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public sealed class HistoryRow
{
    public required string DateText { get; init; }

    public required string EngineText { get; init; }

    public required string SizeText { get; init; }

    public required string FilesText { get; init; }

    public required string DurationText { get; init; }

    public bool IsLatest { get; init; }
}

public sealed class DeltaRow
{
    public required string Label { get; init; }

    public required string DeltaText { get; init; }

    public required string Detail { get; init; }

    public double Ratio { get; init; }

    public long BarValue { get; init; }

    public required System.Windows.Media.Brush Brush { get; init; }
}

public sealed class WslRow
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string SizeText { get; init; }

    public long SizeBytes { get; init; }

    public bool Simulated { get; init; }
}

public partial class ScanRecordsViewModel : ViewModelBase
{
    private readonly IAppShell _shell;

    [ObservableProperty]
    private string _growthTitle = "谁在长胖 · 增量差值对比";

    [ObservableProperty]
    private string _growthDetail = "完成两次扫描后，这里会显示各分类的净增长与最大增长目录";

    [ObservableProperty]
    private string _latestText = "—";

    [ObservableProperty]
    private string _previousText = "—";

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    private bool _hasWsl;

    [ObservableProperty]
    private WslRow? _selectedDisk;

    [ObservableProperty]
    private string _wslStatus = "未检测到 WSL2 虚拟磁盘";

    [ObservableProperty]
    private string _wslOutput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _historyPath = string.Empty;

    public ScanRecordsViewModel(IAppShell shell)
    {
        _shell = shell;
    }

    public ObservableCollection<HistoryRow> History { get; } = new();

    public ObservableCollection<DeltaRow> Deltas { get; } = new();

    public ObservableCollection<WslRow> WslDisks { get; } = new();

    public bool IsDemo => Services.IsDemo;

    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _wslLoaded;
    private bool _wslLoading;

    public void Refresh(bool force = false)
    {
        // Page navigation re-entry should not hit the disk again.
        if (!force && (DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds < 800)
        {
            return;
        }

        _lastRefreshUtc = DateTime.UtcNow;

        HistoryPath = Services.History.StoragePath;
        IReadOnlyList<ScanHistoryEntry> entries = Services.History.Load();
        History.Clear();
        Deltas.Clear();
        HasHistory = entries.Count > 0;

        for (int i = 0; i < entries.Count; i++)
        {
            ScanHistoryEntry entry = entries[i];
            History.Add(new HistoryRow
            {
                DateText = entry.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                EngineText = entry.Engine == "MFT_STREAM_READER" ? "MFT 直读" : "目录遍历",
                SizeText = ByteSize.Format(entry.AttributedBytes),
                FilesText = $"{entry.Files:N0} 文件",
                DurationText = $"{entry.DurationSeconds:0.00}s",
                IsLatest = i == 0,
            });
        }

        ScanHistoryDiff diff = Services.History.Diff();
        if (diff.Latest is null)
        {
            GrowthTitle = "还没有扫描记录";
            GrowthDetail = "完成一次扫描后，TidyC 会把摘要写入本地历史，用于跨时间对比";
            LatestText = "—";
            PreviousText = "—";
        }
        else
        {
            LatestText = $"{diff.Latest.AttributedBytes / (double)(1024L * 1024 * 1024):0.0} GB · {diff.Latest.GeneratedAt.ToLocalTime():MM-dd HH:mm}";
            PreviousText = diff.Previous is null
                ? "首次扫描"
                : $"{diff.Previous.AttributedBytes / (double)(1024L * 1024 * 1024):0.0} GB · {diff.Previous.GeneratedAt.ToLocalTime():MM-dd HH:mm}";

            if (diff.Previous is null)
            {
                GrowthTitle = "首次扫描已记录基线";
                GrowthDetail = "再次扫描后即可自动生成“谁在长胖”的增量对比与 Top 增长目录";
            }
            else
            {
                int days = Math.Max(1, (int)(diff.Latest.GeneratedAt - diff.Previous.GeneratedAt).TotalDays);
                IReadOnlyList<(string Label, long Delta)> categoryDeltas = diff.CategoryDeltas;
                (string Label, long Delta) biggest = categoryDeltas.FirstOrDefault();
                GrowthTitle = diff.TotalDelta >= 0
                    ? $"最近 {days} 天 C 盘净增 {ByteSize.Format(diff.TotalDelta)}"
                    : $"最近 {days} 天 C 盘净减 {ByteSize.Format(-diff.TotalDelta)}";
                GrowthDetail = biggest.Label is null
                    ? "分类差分不可用"
                    : $"最大变化：{biggest.Label} {ByteSize.FormatSigned(biggest.Delta)} · 引擎 {diff.Latest.Engine}";

                long maxAbs = Math.Max(1, categoryDeltas.Select(pair => Math.Abs(pair.Delta)).DefaultIfEmpty(1).Max());
                foreach ((string label, long delta) in categoryDeltas)
                {
                    Deltas.Add(new DeltaRow
                    {
                        Label = label,
                        DeltaText = ByteSize.FormatSigned(delta),
                        Detail = delta >= 0 ? "增长" : "缩小",
                        Ratio = Math.Abs(delta) / (double)maxAbs,
                        BarValue = (long)Math.Round(Math.Abs(delta) * 1000d / maxAbs),
                        Brush = delta >= 0
                            ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Brush.Risk.Caution.Warm")
                            : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Brush.Risk.Safe.Light"),
                    });
                }
            }
        }

        if (force || !_wslLoaded)
        {
            _ = LoadWslAsync();
        }
    }

    private async Task LoadWslAsync()
    {
        if (_wslLoading)
        {
            return;
        }

        _wslLoading = true;
        try
        {
            IReadOnlyList<WslDisk> discovered = Services.IsDemo
                ? Array.Empty<WslDisk>()
                : await Task.Run(() => Services.Wsl.Discover());

            ApplyWslDisks(discovered);
            _wslLoaded = true;
        }
        catch (Exception ex)
        {
            WslStatus = "检测 WSL2 虚拟磁盘失败：" + ex.Message;
        }
        finally
        {
            _wslLoading = false;
        }
    }

    /// <summary>Only called from the UI thread (or its synchronization context).</summary>
    private void ApplyWslDisks(IReadOnlyList<WslDisk> discovered)
    {
        WslDisks.Clear();
        foreach (WslDisk disk in discovered)
        {
            WslDisks.Add(new WslRow
            {
                Name = disk.Name,
                Path = disk.VhdxPath,
                SizeText = ByteSize.Format(disk.SizeBytes),
                SizeBytes = disk.SizeBytes,
            });
        }

        if (WslDisks.Count == 0 && Services.IsDemo)
        {
            WslDisks.Add(new WslRow
            {
                Name = "演示数据 · Ubuntu-22.04",
                Path = @"C:\Users\Admin\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState\ext4.vhdx",
                SizeText = ByteSize.Format(24_300L * 1024 * 1024),
                SizeBytes = 24_300L * 1024 * 1024,
                Simulated = true,
            });
        }

        HasWsl = WslDisks.Count > 0;
        SelectedDisk = WslDisks.FirstOrDefault();
        WslStatus = HasWsl
            ? $"发现 {WslDisks.Count} 个 WSL2 虚拟磁盘（稀疏文件只增不减）"
            : "未检测到 WSL2 虚拟磁盘";

        if (string.IsNullOrWhiteSpace(WslOutput))
        {
            WslOutput =
                "尚未执行压缩。\n\n" +
                "标准流程：\n" +
                "1) wsl --shutdown 停止全部发行版\n" +
                "2) Optimize-VHD -Path <vhdx> -Mode Full（Hyper-V 模块可用时）\n" +
                "3) 若模块缺失，回退 diskpart：attach vdisk readonly → compact vdisk → detach vdisk\n\n" +
                "TidyC 只调用系统工具，绝不直接删除或手改 vhdx 文件。";
        }
    }

    [RelayCommand]
    private async Task RescanWslAsync()
    {
        _wslLoaded = false;
        await LoadWslAsync();
        _shell.ShowToast(HasWsl ? $"重新检测完成，发现 {WslDisks.Count} 个虚拟磁盘" : "未检测到 WSL2 虚拟磁盘");
    }

    [RelayCommand]
    private async Task CompactWslAsync()
    {
        if (SelectedDisk is null || IsBusy)
        {
            return;
        }

        if (SelectedDisk.Simulated || Services.IsDemo)
        {
            WslOutput =
                "演示模式：将在管理员权限下执行\n" +
                "1) wsl --shutdown\n" +
                "2) Optimize-VHD -Path <vhdx> -Mode Full（Hyper-V 可用时）\n" +
                "3) 回退方案：diskpart attach vdisk readonly / compact vdisk / detach vdisk\n" +
                $"预期可回收约 {ByteSize.Format((long)(SelectedDisk.SizeBytes * 0.35))} 的稀疏空洞。";
            _shell.ShowToast("演示模式：已展示压缩步骤");
            return;
        }

        if (!Services.IsElevated)
        {
            bool launched = Services.Wsl.RelaunchElevatedForCompact(SelectedDisk.Path);
            _shell.ShowToast(launched
                ? "已请求管理员权限，请在提权窗口中完成压缩"
                : "UAC 提权被取消，无法压缩虚拟磁盘");
            return;
        }

        IsBusy = true;
        WslStatus = "正在压缩虚拟磁盘（先关闭 WSL，再调用系统工具）…";
        try
        {
            (bool success, string output) = await Services.Wsl.CompactAsync(SelectedDisk.Path);
            WslOutput = output;
            WslStatus = success ? "压缩完成，已回收稀疏空洞" : "压缩未成功，请查看下方输出";
            _wslLoaded = false;
            await LoadWslAsync();
        }
        catch (Exception ex)
        {
            WslStatus = "压缩失败：" + ex.Message;
            WslOutput = ex.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RevealHistory()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Services.History.StoragePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.ShowToast("无法打开历史文件：" + ex.Message);
        }
    }

    [RelayCommand]
    private void RevealDisk(WslRow? row)
    {
        WslRow? disk = row ?? SelectedDisk;
        if (disk is null || disk.Simulated)
        {
            _shell.ShowToast("演示条目没有真实文件");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{disk.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.ShowToast("无法定位虚拟磁盘：" + ex.Message);
        }
    }
}
