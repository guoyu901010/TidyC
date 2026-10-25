using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TidyC.Core.Rules;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public sealed class AppDataItemRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public AppDataItemRow(AppDataItem model, Action selectionChanged)
    {
        Model = model;
        _selectionChanged = selectionChanged;
        _isSelected = model.RiskLevel == RiskLevel.Green;
    }

    public AppDataItem Model { get; }

    public string AppName { get; init; } = string.Empty;

    public RiskLevel RiskLevel => Model.RiskLevel;

    public bool IsSelectable => Model.RiskLevel.IsSelectable();

    public bool IsLocked => !IsSelectable;

    public string RelativePath => Model.RelativePath;

    public string SizeText => ByteSize.Format(Model.SizeBytes);

    public long SizeBytes => Model.SizeBytes;

    public string Purpose => Model.Purpose;

    public string Evidence => Model.Evidence;

    public string Consequence => Model.Consequence;

    public string RuleId => Model.RuleId;

    public string FileCountText => $"{Model.FileCount:N0} 个文件";

    public string KindText => Model.IsDirectory ? "目录" : "文件";

    public string LockHint => IsSelectable
        ? "可安全移入隔离区，14 天内可原路还原"
        : RiskLevel == RiskLevel.Red
            ? "严禁清理：用户数据 / 凭证，硬编码禁止勾选"
            : "未识别项：默认保留，硬编码禁止勾选";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }
}

public sealed class AppGroupRow
{
    public required string AppName { get; init; }

    public required string AppId { get; init; }

    public required string TotalText { get; init; }

    public required string SelectableText { get; init; }

    public required string RiskSummary { get; init; }

    public required List<AppDataItemRow> Items { get; init; }

    public int ItemCount => Items.Count;
}

public partial class AppDataViewModel : ViewModelBase
{
    private readonly IAppShell _shell;

    [ObservableProperty]
    private bool _scopeLocal = true;

    [ObservableProperty]
    private bool _scopeRoaming = true;

    [ObservableProperty]
    private bool _scopeLocalLow = true;

    [ObservableProperty]
    private bool _includeGray = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "等待扫描";

    [ObservableProperty]
    private string _greenText = "—";

    [ObservableProperty]
    private string _yellowText = "—";

    [ObservableProperty]
    private string _redText = "—";

    [ObservableProperty]
    private string _grayText = "—";

    [ObservableProperty]
    private string _totalText = "—";

    [ObservableProperty]
    private string _selectedText = "0 B";

    [ObservableProperty]
    private string _selectedCountText = "0 项已选择";

    [ObservableProperty]
    private bool _hasReport;

    [ObservableProperty]
    private string _rulesetText = string.Empty;

    [ObservableProperty]
    private string _scanMeta = "尚未分析 AppData";

    public AppDataViewModel(IAppShell shell)
    {
        _shell = shell;
    }

    public ObservableCollection<AppGroupRow> Groups { get; } = new();

    public bool IsDemo => Services.IsDemo;

    public void Refresh()
    {
        AppDataAnalysisReport? report = Services.AppDataReport;
        Groups.Clear();
        HasReport = report is not null;

        if (report is null)
        {
            StatusText = "扫描后自动生成 AppData 四级判定";
            GreenText = YellowText = RedText = GrayText = "0 B";
            TotalText = "0 B";
            RulesetText = "规则集签名 " + RuleValidator.ComputeSignature(BuiltInRules.All);
            ScanMeta = "尚未分析 AppData";
            RecalculateSelection();
            return;
        }

        foreach (ApplicationGroup group in report.Applications)
        {
            var rows = group.Items.Select(item => new AppDataItemRow(item, RecalculateSelection) { AppName = group.AppName }).ToList();
            Groups.Add(new AppGroupRow
            {
                AppName = group.AppName,
                AppId = group.AppId,
                TotalText = ByteSize.Format(group.TotalBytes),
                SelectableText = ByteSize.Format(group.SelectableBytes),
                RiskSummary = BuildRiskSummary(group),
                Items = rows,
            });
        }

        AppDataSummary summary = report.Summary;
        GreenText = ByteSize.Format(summary.GreenSafeBytes);
        YellowText = ByteSize.Format(summary.YellowCautionBytes);
        RedText = ByteSize.Format(summary.RedPreserveBytes);
        GrayText = ByteSize.Format(summary.GrayUnrecognizedBytes);
        TotalText = ByteSize.Format(summary.TotalSizeBytes);
        RulesetText = "规则集签名 " + report.RulesetSignature;
        ScanMeta = $"{report.GeneratedAt:yyyy-MM-dd HH:mm:ss} · 引擎 {report.EngineVersion} · 耗时 {report.ScanDurationSeconds:0.00}s · {report.Applications.Count} 个应用分组";
        StatusText = $"共 {report.Applications.Sum(a => a.Items.Count)} 个条目 · 红/灰锁定 {report.Applications.Sum(a => a.Items.Count(i => i.IsLocked))} 项";
        RecalculateSelection();
    }

    private static string BuildRiskSummary(ApplicationGroup group)
    {
        int green = group.Items.Count(i => i.RiskLevel == RiskLevel.Green);
        int yellow = group.Items.Count(i => i.RiskLevel == RiskLevel.Yellow);
        int red = group.Items.Count(i => i.RiskLevel == RiskLevel.Red);
        int gray = group.Items.Count(i => i.RiskLevel == RiskLevel.Gray);
        var parts = new List<string>();
        if (green > 0)
        {
            parts.Add($"绿 {green}");
        }

        if (yellow > 0)
        {
            parts.Add($"黄 {yellow}");
        }

        if (red > 0)
        {
            parts.Add($"红 {red}");
        }

        if (gray > 0)
        {
            parts.Add($"灰 {gray}");
        }

        return string.Join(" · ", parts);
    }

    private void RecalculateSelection()
    {
        IEnumerable<AppDataItemRow> selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected && i.IsSelectable);
        long bytes = selected.Sum(i => i.SizeBytes);
        int count = selected.Count();
        SelectedText = ByteSize.Format(bytes);
        SelectedCountText = $"{count} 项已选择";
        MoveCommand.NotifyCanExecuteChanged();
    }

    public bool CanMove => Groups.SelectMany(g => g.Items).Any(i => i.IsSelected && i.IsSelectable);

    private IAsyncRelayCommand? _moveCommand;

    public IAsyncRelayCommand MoveCommand => _moveCommand ??= new AsyncRelayCommand(MoveSelectedAsync, () => CanMove);

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (Services.IsDemo)
        {
            _shell.ShowToast("演示模式：已按当前范围重新计算示例报告");
            Refresh();
            await Task.CompletedTask;
            return;
        }

        var scopes = new List<string>();
        if (ScopeLocal)
        {
            scopes.Add("Local");
        }

        if (ScopeRoaming)
        {
            scopes.Add("Roaming");
        }

        if (ScopeLocalLow)
        {
            scopes.Add("LocalLow");
        }

        if (scopes.Count == 0)
        {
            StatusText = "至少选择一个 AppData 分支";
            return;
        }

        IsBusy = true;
        StatusText = "正在分析 AppData（Local / Roaming / LocalLow）…";
        try
        {
            AppDataAnalysisReport report = await Task.Run(() => Services.AppData.Analyze(new AppDataScanRequest
            {
                Scopes = scopes,
                IncludeGray = IncludeGray,
            }));

            Services.ApplyAppData(report);
            Refresh();
            StatusText = $"分析完成 · {report.Applications.Sum(a => a.Items.Count)} 个条目";
        }
        catch (Exception ex)
        {
            StatusText = "分析失败：" + ex.Message;
            _shell.ShowToast("AppData 分析失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (AppDataItemRow item in Groups.SelectMany(g => g.Items))
        {
            item.IsSelected = item.RiskLevel is RiskLevel.Green or RiskLevel.Yellow;
        }

        RecalculateSelection();
    }

    [RelayCommand]
    private void SelectGreenOnly()
    {
        foreach (AppDataItemRow item in Groups.SelectMany(g => g.Items))
        {
            item.IsSelected = item.RiskLevel == RiskLevel.Green;
        }

        RecalculateSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (AppDataItemRow item in Groups.SelectMany(g => g.Items))
        {
            item.IsSelected = false;
        }

        RecalculateSelection();
    }

    private async Task MoveSelectedAsync()
    {
        List<AppDataItemRow> selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected && i.IsSelectable).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        List<CleanupCandidate> candidates = new();
        foreach (AppDataItemRow row in selected)
        {
            CleanRule? rule = BuiltInRules.All.FirstOrDefault(r => r.Id == row.RuleId);
            if (rule is null)
            {
                continue;
            }

            candidates.Add(new CleanupCandidate
            {
                Rule = rule,
                Path = row.Model.AbsolutePath,
                SizeBytes = row.SizeBytes,
                FileCount = row.Model.FileCount,
                IsDirectory = row.Model.IsDirectory,
            });
        }

        _shell.StartCleanup(candidates);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        AppDataAnalysisReport? report = Services.AppDataReport;
        if (report is null)
        {
            _shell.ShowToast("尚未生成 AppData 报告");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 AppData 四级判定报告",
            Filter = "JSON 报告 (*.json)|*.json",
            FileName = "appdata-report.json",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, AppDataJson.Serialize(report));
            _shell.ShowToast("已导出 " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast("导出失败：" + ex.Message);
        }
    }
}
