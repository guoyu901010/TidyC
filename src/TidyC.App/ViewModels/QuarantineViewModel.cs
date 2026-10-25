using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyC.Core.Clean;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public sealed class QuarantineRecordRow
{
    public required string OriginalPath { get; init; }

    public required string QuarantinePath { get; init; }

    public required string SizeText { get; init; }

    public required string FileId { get; init; }

    public required string HashText { get; init; }

    public required string StateText { get; init; }

    public required string MovedText { get; init; }

    public required System.Windows.Media.Brush StateBrush { get; init; }

    public string? ConflictPath { get; init; }

    public bool HasConflict => !string.IsNullOrEmpty(ConflictPath);
}

public sealed class QuarantineBatchRow
{
    public required QuarantineBatch Batch { get; init; }

    public required IReadOnlyList<QuarantineRecordRow> Records { get; init; }

    public string BatchId => Batch.BatchId;

    public string Description => string.IsNullOrWhiteSpace(Batch.Description) ? "（未命名批次）" : Batch.Description;

    public string SizeText => ByteSize.Format(Batch.TotalBytes);

    public string ItemCountText => $"{Batch.ItemCount} 项";

    public string CreatedText => Batch.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string MoveDurationText => $"{Batch.MoveDurationSeconds:0.000}s";

    public string ExpiresText => Batch.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string StatusText => Batch.Status switch
    {
        "ACTIVE" => "受保暂存中",
        "RESTORED" => "已全量还原",
        "PARTIAL" => "部分还原",
        "PURGED" => "已物理抹除",
        "EMPTY" => "空批次",
        _ => Batch.Status,
    };

    public string DaysLeftText
    {
        get
        {
            int days = Batch.RemainingDays(DateTime.UtcNow);
            return Batch.Status == "PURGED"
                ? "簇位已释放"
                : days <= 0
                    ? "已到期"
                    : $"{days} 天后到期";
        }
    }

    public bool IsActive => Batch.Status is "ACTIVE" or "PARTIAL";

    public bool IsExpired => IsActive && Batch.IsExpired(DateTime.UtcNow);

    public bool CanRestore => IsActive && Batch.Records.Any(r => !r.IsRestored && !r.Purged);

    public bool CanPurge => Batch.Records.Any(r => !r.Purged && !r.IsRestored);

    public System.Windows.Media.Brush StatusBrush => Batch.Status switch
    {
        "ACTIVE" => (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Risk.Safe.Light"),
        "PARTIAL" => (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Risk.Caution.Warm"),
        "RESTORED" => (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Primary.Soft"),
        "PURGED" => (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Text.Tertiary"),
        _ => (System.Windows.Media.Brush)Application.Current.FindResource("Brush.Text.Secondary"),
    };
}

public partial class QuarantineViewModel : ViewModelBase
{
    private readonly IAppShell _shell;

    [ObservableProperty]
    private string _stagedText = "—";

    [ObservableProperty]
    private string _purgedText = "—";

    [ObservableProperty]
    private string _batchCountText = "0 批次";

    [ObservableProperty]
    private string _expiryText = "—";

    [ObservableProperty]
    private string _rootText = string.Empty;

    [ObservableProperty]
    private string _statusText = "隔离区为空";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasBatches;

    [ObservableProperty]
    private QuarantineBatchRow? _selectedBatch;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private bool _hasWarning;

    public QuarantineViewModel(IAppShell shell)
    {
        _shell = shell;
    }

    public ObservableCollection<QuarantineBatchRow> Batches { get; } = new();

    public bool IsDemo => Services.IsDemo;

    public bool HasSelectedBatch => SelectedBatch is not null;

    partial void OnSelectedBatchChanged(QuarantineBatchRow? value) =>
        OnPropertyChanged(nameof(HasSelectedBatch));

    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public void Refresh(bool force = false)
    {
        // Navigation re-entry must be cheap; explicit actions force a reload.
        if (!force && (DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds < 800)
        {
            return;
        }

        _lastRefreshUtc = DateTime.UtcNow;
        QuarantineStatistics stats = Services.Quarantine.GetStatistics();
        StagedText = ByteSize.Format(stats.StagedBytes);
        PurgedText = ByteSize.Format(stats.PurgedBytes);
        BatchCountText = $"{stats.ActiveBatchCount} 批次 / {stats.ActiveItemCount} 项";
        ExpiryText = stats.NextExpiryUtc is { } next
            ? Math.Max(0, (int)Math.Ceiling((next - DateTime.UtcNow).TotalDays)) + " 天后"
            : "暂无";
        RootText = Services.Quarantine.Root;
        HasWarning = Services.QuarantineWarning is not null;
        WarningText = Services.QuarantineWarning ?? string.Empty;

        string? selectedId = SelectedBatch?.BatchId;
        Batches.Clear();
        foreach (QuarantineBatch batch in Services.Quarantine.LoadBatches())
        {
            Batches.Add(BuildRow(batch));
        }

        HasBatches = Batches.Count > 0;
        SelectedBatch = Batches.FirstOrDefault(b => b.BatchId == selectedId) ?? Batches.FirstOrDefault();
        StatusText = HasBatches
            ? $"{Batches.Count} 个批次 · 暂存 {StagedText} · 已物理释放 {PurgedText}"
            : "隔离区为空 · 所有清理都从这里的可逆暂存开始";
    }

    private QuarantineBatchRow BuildRow(QuarantineBatch batch)
    {
        var records = batch.Records.Select(record =>
        {
            string state = record.Purged
                ? "已物理抹除"
                : record.IsRestored
                    ? "已还原"
                    : "暂存中";
            string brush = record.Purged
                ? "Brush.Text.Tertiary"
                : record.IsRestored
                    ? "Brush.Primary.Soft"
                    : "Brush.Risk.Safe.Light";

            return new QuarantineRecordRow
            {
                OriginalPath = record.OriginalPath,
                QuarantinePath = record.QuarantinePath,
                SizeText = ByteSize.Format(record.SizeBytes),
                FileId = string.IsNullOrEmpty(record.FileIndexHex) ? "—" : record.FileIndexHex,
                HashText = string.IsNullOrEmpty(record.Sha256Hash) ? "未计算（大文件）" : record.Sha256Hash[..16] + "…",
                StateText = state,
                MovedText = record.MovedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                StateBrush = (System.Windows.Media.Brush)Application.Current.FindResource(brush),
                ConflictPath = record.ConflictPath,
            };
        }).ToList();

        return new QuarantineBatchRow { Batch = batch, Records = records };
    }

    [RelayCommand]
    private async Task RestoreAsync(QuarantineBatchRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            $"将把批次 {row.BatchId} 的暂存文件原路还原：\n\n{row.Description}\n{row.ItemCountText} · {row.SizeText}\n\n" +
            "还原过程会校验 SHA-256 与 FileId 指纹；若原路径已存在同名新文件，会自动追加 _restore_conflict_ 时间戳。",
            "一键原子还原",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在原子还原…";
        try
        {
            QuarantineRestoreResult result = await Services.Quarantine.RestoreBatchAsync(row.BatchId);
            StatusText = result.IsSuccess
                ? $"还原完成：{result.RestoredCount} 项，冲突让位 {result.ConflictCount} 项，耗时 {result.DurationSeconds:0.00}s"
                : $"部分还原：成功 {result.RestoredCount} · 失败 {result.FailedCount}";
            _shell.ShowToast(result.IsSuccess ? $"已还原 {result.RestoredCount} 项" : "还原未完全成功，详见状态栏");
            Refresh(force: true);
        }
        catch (Exception ex)
        {
            StatusText = "还原失败：" + ex.Message;
            _shell.ShowToast("还原失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PurgeAsync(QuarantineBatchRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            $"即将物理抹除批次 {row.BatchId} 的隔离副本：\n\n{row.Description}\n{row.ItemCountText} · {row.SizeText}\n\n" +
            "抹除会调用句柄级删除真正释放簇位，此操作不可撤销。确认继续？",
            "物理彻底抹除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在物理抹除…";
        try
        {
            QuarantinePurgeResult result = await Services.Quarantine.PurgeBatchAsync(row.BatchId);
            StatusText = result.IsSuccess
                ? $"已抹除 {result.PurgedCount} 项，净释放 {ByteSize.Format(result.FreedBytes)}"
                : $"抹除部分失败：{string.Join("; ", result.Errors.Take(2))}";
            _shell.ShowToast($"物理释放 {ByteSize.Format(result.FreedBytes)}");
            Refresh(force: true);
        }
        catch (Exception ex)
        {
            StatusText = "抹除失败：" + ex.Message;
            _shell.ShowToast("抹除失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PurgeExpiredAsync()
    {
        if (IsBusy)
        {
            return;
        }

        int expired = Batches.Count(b => b.IsExpired);
        if (expired == 0)
        {
            _shell.ShowToast("当前没有已到期的批次；到期前系统不会自动静默抹除");
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            $"检测到 {expired} 个已到期批次。\n\n到期批次只有在手动确认后才会物理抹除，抹除不可撤销。",
            "清空并物理释放过期项",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<QuarantinePurgeResult> results = await Services.Quarantine.PurgeExpiredAsync();
            long freed = results.Sum(r => r.FreedBytes);
            StatusText = $"已清空 {results.Count} 个过期批次，净释放 {ByteSize.Format(freed)}";
            _shell.ShowToast($"净释放 {ByteSize.Format(freed)}");
            Refresh(force: true);
        }
        catch (Exception ex)
        {
            StatusText = "清理过期项失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenJournals()
    {
        string directory = Services.Quarantine.JournalDirectory;
        Directory.CreateDirectory(directory);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.ShowToast("无法打开日志目录：" + ex.Message);
        }
    }

    [RelayCommand]
    private void RevealRoot()
    {
        Directory.CreateDirectory(Services.Quarantine.Root);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.Quarantine.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.ShowToast("无法打开隔离区：" + ex.Message);
        }
    }

    [RelayCommand]
    private void SelectBatch(QuarantineBatchRow? row)
    {
        if (row is not null)
        {
            SelectedBatch = row;
        }
    }
}
