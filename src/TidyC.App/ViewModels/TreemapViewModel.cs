using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.ViewModels;

public sealed class BreadcrumbItem
{
    public required string Name { get; init; }

    public required string Path { get; init; }
}

public partial class TreemapViewModel : ViewModelBase
{
    private readonly IAppShell _shell;
    private SpaceNode? _drillRoot;
    private string? _loadedScanId;

    [ObservableProperty]
    private SpaceNode? _root;

    [ObservableProperty]
    private SpaceNode? _selectedNode;

    [ObservableProperty]
    private string _currentPath = "C:\\";

    [ObservableProperty]
    private string _selectedName = "未选择节点";

    [ObservableProperty]
    private string _selectedPath = "在左侧矩形树图中单击任意色块查看详情";

    [ObservableProperty]
    private string _selectedSize = "—";

    [ObservableProperty]
    private string _selectedCounts = "—";

    [ObservableProperty]
    private string _selectedHint = string.Empty;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _canMoveSelection;

    [ObservableProperty]
    private string _rootSummary = "等待扫描";

    [ObservableProperty]
    private string _depthHint = "Squarified 矩形树图 · 深度 6 层 · 护栏 6000 块";

    public TreemapViewModel(IAppShell shell)
    {
        _shell = shell;
        DrillUpCommand = new RelayCommand(DrillUp, () => _drillRoot?.Parent is not null);
    }

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    public IRelayCommand DrillUpCommand { get; }

    public void Refresh()
    {
        ScanResult? scan = Services.Scan;
        if (scan is null)
        {
            Root = null;
            _drillRoot = null;
            _loadedScanId = null;
            Breadcrumbs.Clear();
            RootSummary = "等待扫描";
            SelectedNode = null;
            HasSelection = false;
            return;
        }

        if (_loadedScanId != scan.ScanId)
        {
            _drillRoot = scan.Root;
            _loadedScanId = scan.ScanId;
            SelectedNode = null;
            HasSelection = false;
        }

        if (_drillRoot is null)
        {
            _drillRoot = scan.Root;
        }

        Root = _drillRoot;
        RootSummary = $"{Size(_drillRoot.AllocatedBytes)} · {_drillRoot.FileCount:N0} 个文件 · {_drillRoot.DirectoryCount:N0} 个子目录";
        RebuildBreadcrumbs();
        DrillUpCommand.NotifyCanExecuteChanged();
    }

    public void Select(SpaceNode? node)
    {
        SelectedNode = node;
        if (node is null)
        {
            HasSelection = false;
            SelectedName = "未选择节点";
            SelectedPath = "在左侧矩形树图中单击任意色块查看详情";
            SelectedSize = "—";
            SelectedCounts = "—";
            SelectedHint = string.Empty;
            CanMoveSelection = false;
            MoveSelectedCommand.NotifyCanExecuteChanged();
            return;
        }

        HasSelection = true;
        SelectedName = node.Name;
        SelectedPath = node.FullPath;
        SelectedSize = $"{Size(node.AllocatedBytes)}（分配 {node.AllocatedBytes:N0} 字节）";
        SelectedCounts = $"文件数 {node.FileCount:N0} · 子目录数 {node.DirectoryCount:N0} · 重解析点跳过 {node.ReparseSkippedCount:N0}";

        CleanupCandidate? candidate = FindCandidate(node.FullPath);
        CanMoveSelection = candidate is not null;
        SelectedHint = candidate is not null
            ? $"命中白名单规则 {candidate.Rule.Id}（{candidate.Rule.Risk.ToChineseLabel()}），可安全移入隔离区"
            : RuleValidator.IsForbiddenCleanRoot(node.FullPath)
                ? "该路径位于硬编码禁清区（Windows / Program Files / 个人目录），禁止清理"
                : "未命中白名单规则，默认保留；如需清理请先确认用途";
        MoveSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Activate(SpaceNode? node)
    {
        if (node is null || node.Children.Count == 0)
        {
            return;
        }

        _drillRoot = node;
        SelectedNode = null;
        HasSelection = false;
        Refresh();
    }

    [RelayCommand]
    private void NavigateCrumb(BreadcrumbItem? item)
    {
        if (item is null || Services.Scan is null)
        {
            return;
        }

        SpaceNode? target = Services.Scan.Root
            .SelfAndDescendants()
            .FirstOrDefault(n => PathKey.AreEqual(n.FullPath, item.Path));

        if (target is not null)
        {
            _drillRoot = target;
            SelectedNode = null;
            HasSelection = false;
            Refresh();
        }
    }

    private void DrillUp()
    {
        SpaceNode? parent = _drillRoot?.Parent;
        if (parent is null)
        {
            return;
        }

        _drillRoot = parent;
        SelectedNode = null;
        HasSelection = false;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelection))]
    private async Task MoveSelectedAsync()
    {
        if (SelectedNode is null)
        {
            return;
        }

        CleanupCandidate? candidate = FindCandidate(SelectedNode.FullPath);
        if (candidate is null)
        {
            _shell.ShowToast("该目录不在白名单内，默认保留（硬编码禁止清理）");
            return;
        }

        _shell.StartCleanup(new[] { candidate });
        await Task.CompletedTask;
    }

    private CleanupCandidate? FindCandidate(string path)
    {
        CleanupPlan? plan = Services.Plan;
        if (plan is null)
        {
            return null;
        }

        return plan.Candidates.FirstOrDefault(c =>
            PathKey.AreEqual(c.Path, path) || PathKey.IsStrictlyUnder(path, c.Path) || PathKey.IsStrictlyUnder(c.Path, path));
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var chain = new List<BreadcrumbItem>();
        SpaceNode? cursor = _drillRoot;
        while (cursor is not null)
        {
            chain.Insert(0, new BreadcrumbItem { Name = cursor.Name, Path = cursor.FullPath });
            cursor = cursor.Parent;
        }

        foreach (BreadcrumbItem item in chain)
        {
            Breadcrumbs.Add(item);
        }

        CurrentPath = _drillRoot?.FullPath ?? "C:\\";
    }
}
