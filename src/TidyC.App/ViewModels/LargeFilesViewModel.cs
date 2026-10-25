using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public sealed class FileRow
{
    public required string Rank { get; init; }

    public required string Name { get; init; }

    public required string Directory { get; init; }

    public required string Path { get; init; }

    public required string SizeText { get; init; }

    public long SizeBytes { get; init; }

    public long MaxBytes { get; init; }

    public double Ratio { get; init; }

    public required string CategoryText { get; init; }

    public required System.Windows.Media.Brush BarBrush { get; init; }
}

public sealed class FolderRow
{
    public required string Rank { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string SizeText { get; init; }

    public required string Detail { get; init; }

    public long SizeBytes { get; init; }

    public long MaxBytes { get; init; }

    public double Ratio { get; init; }

    public required System.Windows.Media.Brush BarBrush { get; init; }
}

public partial class LargeFilesViewModel : ViewModelBase
{
    private readonly IAppShell _shell;
    private readonly List<FileRow> _allFiles = new();
    private readonly List<FolderRow> _allFolders = new();

    [ObservableProperty]
    private string _summary = "扫描后列出占用空间最大的文件与目录";

    [ObservableProperty]
    private string _fileCountText = "0 项";

    [ObservableProperty]
    private string _folderCountText = "0 项";

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _hasData;

    public LargeFilesViewModel(IAppShell shell)
    {
        _shell = shell;
    }

    public ObservableCollection<FileRow> Files { get; } = new();

    public ObservableCollection<FolderRow> Folders { get; } = new();

    partial void OnFilterTextChanged(string value) => RebuildVisible();

    public void Refresh()
    {
        _allFiles.Clear();
        _allFolders.Clear();
        HasData = false;

        ScanResult? scan = Services.Scan;
        if (scan is null)
        {
            Summary = "扫描后列出占用空间最大的文件与目录";
            FileCountText = "0 项";
            FolderCountText = "0 项";
            RebuildVisible();
            return;
        }

        List<FileFact> files = scan.TopLargestFiles.OrderByDescending(f => f.AllocatedBytes).Take(100).ToList();
        List<FolderFact> folders = scan.TopLargestFolders.OrderByDescending(f => f.AllocatedBytes).Take(50).ToList();

        long maxFile = Math.Max(1, files.FirstOrDefault()?.AllocatedBytes ?? 1);
        long maxFolder = Math.Max(1, folders.FirstOrDefault()?.AllocatedBytes ?? 1);

        int rank = 1;
        foreach (FileFact file in files)
        {
            _allFiles.Add(new FileRow
            {
                Rank = rank++.ToString("00"),
                Name = Path.GetFileName(file.Path),
                Directory = Path.GetDirectoryName(file.Path) ?? string.Empty,
                Path = file.Path,
                SizeText = Size(file.AllocatedBytes),
                SizeBytes = file.AllocatedBytes,
                MaxBytes = maxFile,
                Ratio = file.AllocatedBytes / (double)maxFile,
                CategoryText = CategoryLabel(file.Category),
                BarBrush = CategoryBrush(file.Category),
            });
        }

        rank = 1;
        foreach (FolderFact folder in folders)
        {
            _allFolders.Add(new FolderRow
            {
                Rank = rank++.ToString("00"),
                Name = Path.GetFileName(folder.Path.TrimEnd('\\')),
                Path = folder.Path,
                SizeText = Size(folder.AllocatedBytes),
                Detail = $"{folder.FileCount:N0} 个文件 · {folder.DirectoryCount:N0} 个子目录",
                SizeBytes = folder.AllocatedBytes,
                MaxBytes = maxFolder,
                Ratio = folder.AllocatedBytes / (double)maxFolder,
                BarBrush = CategoryBrush(folder.Category),
            });
        }

        Summary =
            $"{Size(scan.Reconciliation.EngineAttributedBytes)} 已归因 · Top 文件合计 {Size(files.Sum(f => f.AllocatedBytes))} · " +
            $"Top 目录合计 {Size(folders.Sum(f => f.AllocatedBytes))}";
        HasData = _allFiles.Count > 0 || _allFolders.Count > 0;
        RebuildVisible();
    }

    private void RebuildVisible()
    {
        string filter = FilterText.Trim();
        bool Matches(string path) => filter.Length == 0 || path.Contains(filter, StringComparison.OrdinalIgnoreCase);

        Files.Clear();
        foreach (FileRow row in _allFiles.Where(r => Matches(r.Path)))
        {
            Files.Add(row);
        }

        Folders.Clear();
        foreach (FolderRow row in _allFolders.Where(r => Matches(r.Path)))
        {
            Folders.Add(row);
        }

        FileCountText = $"{Files.Count} 项";
        FolderCountText = $"{Folders.Count} 项";
    }

    [RelayCommand]
    private void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _shell.ShowToast("无法打开资源管理器：" + ex.Message);
        }
    }

    [RelayCommand]
    private void CopyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
            _shell.ShowToast("路径已复制到剪贴板");
        }
        catch (Exception)
        {
        }
    }

    private static string CategoryLabel(string category) => category switch
    {
        StorageCategories.WindowsSystem => "系统",
        StorageCategories.AppData => "AppData",
        StorageCategories.UserProfiles => "用户",
        StorageCategories.ProgramFiles => "安装",
        StorageCategories.ProtectedSystem => "受保",
        StorageCategories.Quarantine => "隔离区",
        _ => "其他",
    };

    private static System.Windows.Media.Brush CategoryBrush(string category)
    {
        string key = category switch
        {
            StorageCategories.WindowsSystem => "Brush.Spectrum.Windows",
            StorageCategories.AppData => "Brush.Spectrum.AppData",
            StorageCategories.UserProfiles => "Brush.Spectrum.UserProfiles",
            StorageCategories.ProgramFiles => "Brush.Spectrum.ProgramFiles",
            StorageCategories.ProtectedSystem => "Brush.Spectrum.ProtectedSystem",
            StorageCategories.Quarantine => "Brush.Spectrum.Quarantine",
            _ => "Brush.Spectrum.Other",
        };

        return (System.Windows.Media.Brush)Application.Current.FindResource(key);
    }
}
