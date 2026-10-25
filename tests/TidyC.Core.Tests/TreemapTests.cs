using TidyC.Core.Scan;

namespace TidyC.Core.Tests;

public sealed class TreemapTests
{
    [Fact]
    public void Layout_KeepsAreasProportional()
    {
        double[] values = { 6, 3, 1 };

        IReadOnlyList<TreemapRect> rects = SquarifiedTreemap.Layout(values, 400, 200);

        Assert.Equal(3, rects.Count);
        double total = rects.Sum(r => r.Area);
        Assert.InRange(total, 80_000 * 0.99, 80_000 * 1.01);
        Assert.InRange(rects[0].Area / total, 0.599, 0.601);
        Assert.InRange(rects[1].Area / total, 0.299, 0.301);
        Assert.InRange(rects[2].Area / total, 0.099, 0.101);
    }

    [Fact]
    public void Layout_StaysInsideBoundsWithoutOverlap()
    {
        double[] values = { 17, 13, 9, 7, 5, 3, 2, 1 };
        const double width = 640;
        const double height = 360;

        IReadOnlyList<TreemapRect> rects = SquarifiedTreemap.Layout(values, width, height);

        Assert.Equal(values.Length, rects.Count);
        foreach (TreemapRect rect in rects)
        {
            Assert.InRange(rect.X, -0.001, width);
            Assert.InRange(rect.Y, -0.001, height);
            Assert.InRange(rect.X + rect.Width, 0, width + 0.001);
            Assert.InRange(rect.Y + rect.Height, 0, height + 0.001);
        }

        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                double overlapX = Math.Max(0, Math.Min(rects[i].X + rects[i].Width, rects[j].X + rects[j].Width) - Math.Max(rects[i].X, rects[j].X));
                double overlapY = Math.Max(0, Math.Min(rects[i].Y + rects[i].Height, rects[j].Y + rects[j].Height) - Math.Max(rects[i].Y, rects[j].Y));
                Assert.True(overlapX * overlapY < 0.01, $"矩形 {i} 与 {j} 重叠");
            }
        }

        Assert.InRange(rects.Sum(r => r.Area), width * height * 0.99, width * height * 1.01);
    }

    [Fact]
    public void Layout_HandlesEmptyAndSingleInput()
    {
        Assert.Empty(SquarifiedTreemap.Layout(Array.Empty<double>(), 100, 100));

        IReadOnlyList<TreemapRect> single = SquarifiedTreemap.Layout(new[] { 42.0 }, 320, 180);

        TreemapRect rect = Assert.Single(single);
        Assert.Equal(0, rect.X, 3);
        Assert.Equal(0, rect.Y, 3);
        Assert.Equal(320, rect.Width, 3);
        Assert.Equal(180, rect.Height, 3);
    }

    [Fact]
    public void Tiler_AssignsNestedRectsAndHitTests()
    {
        var root = new SpaceNode { Name = "C:\\", FullPath = "C:\\", Kind = NodeKind.Volume };
        var users = new SpaceNode { Name = "Users", FullPath = @"C:\Users", Kind = NodeKind.Directory };
        var windows = new SpaceNode { Name = "Windows", FullPath = @"C:\Windows", Kind = NodeKind.Directory };
        users.AddDirectFile(6_000, 6_000);
        windows.AddDirectFile(10_000, 10_000);
        root.AddChild(users);
        root.AddChild(windows);
        root.Aggregate();

        TreemapTiler.TileStats stats = TreemapTiler.Tile(root, 800, 400);

        Assert.True(stats.Blocks >= 2);
        Assert.True(users.LayoutWidth > 0 && windows.LayoutWidth > 0);
        Assert.InRange(users.LayoutWidth * users.LayoutHeight, 799 * 399 * (6_000 / 16_000.0) * 0.95,
            800 * 400 * (6_000 / 16_000.0) * 1.05);

        SpaceNode? hit = TreemapTiler.HitTest(root, windows.LayoutX + (windows.LayoutWidth / 2), windows.LayoutY + (windows.LayoutHeight / 2));
        Assert.Same(windows, hit);
    }
}
