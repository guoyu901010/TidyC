using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Tests.TestSupport;

namespace TidyC.Core.Tests;

public sealed class DirectoryTreeWalkerTests
{
    [Fact]
    public async Task Walk_CountsFilesAndAllocatedBytes()
    {
        using var tree = new TempTree("walk-count");
        tree.File(@"alpha\one.bin", 100);
        tree.File(@"alpha\nested\two.bin", 5000);
        tree.File("root.bin", 1);

        var walker = new DirectoryTreeWalker();
        ScanResult result = await walker.ScanAsync(tree.Root, new ScanOptions { TopFileCount = 10 });

        Assert.Equal(ScanEngines.DirectoryTreeWalker, result.EngineType);
        Assert.Equal(3, result.TotalFilesCount);
        Assert.True(result.Reconciliation.EngineAttributedBytes >= 5101);
        Assert.True(result.Reconciliation.PermissionBlindspotBytes >= 0);
        Assert.NotEmpty(result.TopLargestFiles);
        Assert.Equal(result.Reconciliation.EngineAttributedBytes, result.Root.AllocatedBytes);
        Assert.Contains(result.Root.Children, node => node.Name.Equals("alpha", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Walk_SkipsJunctionTargets()
    {
        using var tree = new TempTree("walk-junction");
        string inside = tree.Dir("inside");
        tree.File(@"inside\real.bin", 100);
        string outside = tree.Dir("outside");
        tree.File(@"outside\huge.bin", 4 * 1024 * 1024);
        if (!Junctions.TryCreate(outside, Path.Combine(inside, "link")))
        {
            return;
        }

        var walker = new DirectoryTreeWalker();
        ScanResult result = await walker.ScanAsync(tree.Root, new ScanOptions());

        Assert.Equal(1, result.TotalFilesCount);
        Assert.True(result.Reconciliation.EngineAttributedBytes < 1024 * 1024);
        Assert.True(result.ReparsePointsSkippedCount >= 1);
    }

    [Fact]
    public async Task Walk_RespectsMaxDepth()
    {
        using var tree = new TempTree("walk-depth");
        tree.File(@"level1\level2\level3\deep.bin", 4096);
        tree.File(@"level1\shallow.bin", 4096);

        var walker = new DirectoryTreeWalker();
        ScanResult result = await walker.ScanAsync(tree.Root, new ScanOptions { MaxDepth = 2 });

        Assert.Equal(1, result.TotalFilesCount);
        Assert.Contains(result.Warnings, w => w.Contains("深度限制", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupPlanner_FindsWhitelistedTargets()
    {
        using var fixture = new AppDataFixture();
        fixture.Tree.File(@"profile\AppData\Local\Pip\Cache\wheel.bin", 512 * 1024);
        CleanRule rule = TestRules.Rule("Plan_01", RiskLevel.Green, new[] { @"%LOCALAPPDATA%\Pip\Cache" });
        var planner = new CleanupPlanner(new[] { rule }, fixture.Probe, fixture.Expander);

        CleanupPlan plan = planner.Build(enforceProtectedPaths: false);

        Assert.False(plan.IsEmpty);
        Assert.True(plan.GreenBytes > 0);
        Assert.True(plan.TotalBytes > 500 * 1024);
        Assert.All(plan.Candidates, c => Assert.True(c.Risk.IsSelectable()));
    }
}
