using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyC.Core.Clean;
using TidyC.Core.Rules;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public sealed class CleanupRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public CleanupRow(CleanupCandidate candidate, bool selected, Action changed)
    {
        Candidate = candidate;
        _isSelected = selected;
        _changed = changed;
    }

    public CleanupCandidate Candidate { get; }

    public CleanRule Rule => Candidate.Rule;

    public string AppName => Candidate.Rule.AppName;

    public string RuleId => Candidate.Rule.Id;

    public string Path => Candidate.Path;

    public string SizeText => ByteSize.Format(Candidate.SizeBytes);

    public long SizeBytes => Candidate.SizeBytes;

    public RiskLevel Risk => Candidate.Rule.Risk;

    public string RiskText => Candidate.Rule.Risk.ToChineseLabel();

    public string Purpose => Candidate.Rule.Purpose;

    public string Consequence => Candidate.Rule.Consequence;

    public string Detail => $"{Candidate.FileCount:N0} 个文件 · {Candidate.Rule.Category}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _changed();
            }
        }
    }
}

public sealed class CleanupGroupRow
{
    public required string AppName { get; init; }

    public required string TotalText { get; init; }

    public required List<CleanupRow> Rows { get; init; }
}

public partial class CleanWizardViewModel : ViewModelBase
{
    private readonly IAppShell _shell;
    private string _planSignature = string.Empty;

    [ObservableProperty]
    private int _step = 1;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _totalSelectedText = "0 B";

    [ObservableProperty]
    private string _selectedCountText = "0 项已选择";

    [ObservableProperty]
    private string _greenText = "—";

    [ObservableProperty]
    private string _yellowText = "—";

    [ObservableProperty]
    private bool _hasCandidates;

    [ObservableProperty]
    private bool _showResult;

    [ObservableProperty]
    private string _resultTitle = string.Empty;

    [ObservableProperty]
    private string _resultDetail = string.Empty;

    [ObservableProperty]
    private string _resultBatchId = string.Empty;

    [ObservableProperty]
    private string _description = "安全清理（白名单缓存的同卷原子搬移）";

    [ObservableProperty]
    private string _quarantineHint = string.Empty;

    public CleanWizardViewModel(IAppShell shell)
    {
        _shell = shell;
        LoadPlanCommand = new RelayCommand(() => LoadSelection(null));
    }

    public ObservableCollection<CleanupGroupRow> Groups { get; } = new();

    public ObservableCollection<CleanupRow> SelectedRows { get; } = new();

    public ObservableCollection<string> JournalLines { get; } = new();

    public IRelayCommand LoadPlanCommand { get; }

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    public bool Step1Complete => Step > 1;

    public bool Step2Complete => Step > 2;

    public bool IsDemo => Services.IsDemo;

    private bool CanGoNext =>
        Step == 2 || Groups.SelectMany(g => g.Rows).Any(row => row.IsSelected);

    public string DemoHint => Services.IsDemo
        ? "演示模式：确认后将模拟搬移流程，不会写入真实磁盘"
        : "同卷原子改名（MoveFileEx + FileId 二次校验），秒级完成且 14 天可逆";

    public void Refresh()
    {
        CleanupPlan? plan = Services.Plan;
        string signature = plan is null ? "none" : $"{plan.Candidates.Count}:{plan.TotalBytes}";
        if (signature == _planSignature && Groups.Count > 0)
        {
            return;
        }

        _planSignature = signature;
        if (Step == 3)
        {
            return;
        }

        LoadSelection(null, resetStep: false);
    }

    public void LoadSelection(IReadOnlyList<CleanupCandidate>? candidates, bool resetStep = true)
    {
        Groups.Clear();
        JournalLines.Clear();
        ShowResult = false;
        TotalSelectedText = "0 B";
        SelectedCountText = "0 项已选择";

        if (resetStep)
        {
            Step = 1;
        }

        CleanupPlan? plan = Services.Plan;
        List<CleanupCandidate> source = candidates is { Count: > 0 }
            ? candidates.ToList()
            : plan?.Candidates.ToList() ?? new List<CleanupCandidate>();

        HashSet<string> preselected = candidates is { Count: > 0 }
            ? new HashSet<string>(candidates.Select(c => c.Path), StringComparer.OrdinalIgnoreCase)
            : source.Where(c => c.Rule.Risk == RiskLevel.Green).Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, CleanupCandidate> group in source.GroupBy(c => c.Rule.AppName))
        {
            List<CleanupRow> rows = group
                .OrderByDescending(c => c.SizeBytes)
                .Select(c => new CleanupRow(c, preselected.Contains(c.Path), Recalculate))
                .ToList();

            Groups.Add(new CleanupGroupRow
            {
                AppName = group.Key,
                TotalText = ByteSize.Format(group.Sum(c => c.SizeBytes)),
                Rows = rows,
            });
        }

        HasCandidates = Groups.Count > 0;
        GreenText = ByteSize.Format(source.Where(c => c.Rule.Risk == RiskLevel.Green).Sum(c => c.SizeBytes));
        YellowText = ByteSize.Format(source.Where(c => c.Rule.Risk == RiskLevel.Yellow).Sum(c => c.SizeBytes));
        _planSignature = plan is null ? "none" : $"{plan.Candidates.Count}:{plan.TotalBytes}";
        Recalculate();
        Step = Math.Clamp(Step, 1, 3);
    }

    private void Recalculate()
    {
        List<CleanupRow> selected = Groups.SelectMany(g => g.Rows).Where(r => r.IsSelected).ToList();
        SelectedRows.Clear();
        foreach (CleanupRow row in selected)
        {
            SelectedRows.Add(row);
        }

        long bytes = selected.Sum(r => r.SizeBytes);
        TotalSelectedText = ByteSize.Format(bytes);
        SelectedCountText = $"{selected.Count} 项已选择";
        QuarantineHint = Services.IsDemo
            ? "将模拟搬移 " + ByteSize.Format(bytes) + " 到隔离区"
            : $"将把 {ByteSize.Format(bytes)} 以同卷原子改名搬移到 {Services.Quarantine.Root}";
        NextCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (CleanupRow row in Groups.SelectMany(g => g.Rows))
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (CleanupRow row in Groups.SelectMany(g => g.Rows))
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (Step == 1)
        {
            if (!Groups.SelectMany(g => g.Rows).Any(r => r.IsSelected))
            {
                _shell.ShowToast("请至少选择一项白名单缓存");
                return;
            }

            Step = 2;
            return;
        }

        if (Step == 2)
        {
            await MoveAsync();
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 1)
        {
            Step--;
        }
    }

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(Step1Complete));
        OnPropertyChanged(nameof(Step2Complete));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private async Task MoveAsync()
    {
        List<CleanupRow> selected = Groups.SelectMany(g => g.Rows).Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsBusy = true;
        ProgressText = "正在校验 FileId 指纹并执行同卷原子改名…";
        JournalLines.Clear();

        try
        {
            if (Services.IsDemo)
            {
                await Task.Delay(700);
                foreach (CleanupRow row in selected)
                {
                    JournalLines.Add($"ATOMIC_MOVE_START   {row.RuleId}  {row.Path}");
                    JournalLines.Add($"ATOMIC_MOVE_COMMITTED  0x{row.RuleId.GetHashCode():X16}  {row.SizeText}  SUCCESS");
                }

                JournalLines.Add($"ATOMIC_MOVE_BATCH_COMMITTED  items={selected.Count}  bytes={selected.Sum(r => r.SizeBytes)}");
                ShowResult = true;
                ResultTitle = "演示模式：已模拟完成安全清理";
                ResultDetail =
                    $"模拟把 {selected.Count} 项（{ByteSize.Format(selected.Sum(r => r.SizeBytes))}）搬移到隔离区；" +
                    "真实运行时这些文件会以零拷贝原子改名进入 14 天可逆批次。";
                ResultBatchId = "BATCH_DEMO_SIMULATED";
                Step = 3;
                _shell.ShowToast("演示模式：已模拟搬移 " + TotalSelectedText);
                return;
            }

            QuarantineBatch batch = await Services.Quarantine.MoveToQuarantineAsync(
                selected.Select(r => r.Candidate.Path),
                Description);

            foreach (QuarantineRecord record in batch.Records)
            {
                JournalLines.Add($"ATOMIC_MOVE_COMMITTED  {record.FileIndexHex}  {ByteSize.Format(record.SizeBytes)}  {record.QuarantinePath}");
            }

            foreach (string skipped in batch.Skipped)
            {
                JournalLines.Add("SKIPPED  " + skipped);
            }

            JournalLines.Add(
                $"ATOMIC_MOVE_BATCH_COMMITTED  items={batch.ItemCount}  bytes={batch.TotalBytes}  duration={batch.MoveDurationSeconds:0.000}s");

            ShowResult = true;
            ResultBatchId = batch.BatchId;
            ResultTitle = batch.ItemCount > 0 ? "安全清理完成 · 全部可逆" : "没有文件被移动";
            ResultDetail =
                $"{batch.ItemCount} 项（{ByteSize.Format(batch.TotalBytes)}）已暂存隔离区，耗时 {batch.MoveDurationSeconds:0.000}s。" +
                (batch.Skipped.Count > 0 ? $" {batch.Skipped.Count} 项被安全跳过（锁定 / 不存在 / 禁清区）。" : string.Empty) +
                $" 有效期至 {batch.ExpiresAt:yyyy-MM-dd}，到期前可一键原路还原。";
            Step = 3;
            Services.Quarantine.GetStatistics();
            _shell.ShowToast($"已安全移入隔离区 {ByteSize.Format(batch.TotalBytes)}");
        }
        catch (Exception ex)
        {
            ProgressText = "搬移失败：" + ex.Message;
            _shell.ShowToast("清理失败：" + ex.Message);
            JournalLines.Add("ERROR  " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GoQuarantine() => _shell.Navigate("quarantine");

    [RelayCommand]
    private void Restart()
    {
        LoadSelection(null);
        JournalLines.Clear();
        ShowResult = false;
        Step = 1;
    }
}
