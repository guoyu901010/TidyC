namespace TidyC.Core.Scan;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    public bool Contains(double x, double y) =>
        x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    public override string ToString() => $"({X:0.##},{Y:0.##},{Width:0.##}x{Height:0.##})";
}

/// <summary>
/// Squarified treemap (Bruls, Huizing &amp; van Wijk). Pure geometry: the WPF
/// control only draws what this produces, so the layout is unit-testable.
/// </summary>
public static class SquarifiedTreemap
{
    public static IReadOnlyList<TreemapRect> Layout(IReadOnlyList<double> values, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || width <= 0 || height <= 0)
        {
            return Array.Empty<TreemapRect>();
        }

        var areas = new List<double>(values.Count);
        double total = 0;
        foreach (double value in values)
        {
            if (!(value > 0) || double.IsNaN(value) || double.IsInfinity(value))
            {
                continue;
            }

            areas.Add(value);
            total += value;
        }

        if (areas.Count == 0 || total <= 0)
        {
            return Array.Empty<TreemapRect>();
        }

        double scale = (width * height) / total;
        for (int i = 0; i < areas.Count; i++)
        {
            areas[i] *= scale;
        }

        var result = new List<TreemapRect>(areas.Count);
        LayoutRow(areas, new TreemapRect(0, 0, width, height), result);
        return result;
    }

    private static void LayoutRow(List<double> areas, TreemapRect free, List<TreemapRect> output)
    {
        int index = 0;
        while (index < areas.Count)
        {
            double side = Math.Min(free.Width, free.Height);
            if (side <= 0)
            {
                return;
            }

            var row = new List<double>();
            double rowSum = 0;
            double worst = double.MaxValue;

            while (index < areas.Count)
            {
                row.Add(areas[index]);
                rowSum += areas[index];
                double candidateWorst = Worst(row, rowSum, side);
                if (row.Count == 1 || candidateWorst <= worst)
                {
                    worst = candidateWorst;
                    index++;
                }
                else
                {
                    row.RemoveAt(row.Count - 1);
                    rowSum -= areas[index];
                    break;
                }
            }

            if (row.Count == 0)
            {
                return;
            }

            bool vertical = free.Width >= free.Height;
            double thickness = rowSum / side;
            double offset = 0;

            foreach (double area in row)
            {
                double length = area / thickness;
                TreemapRect rect;
                if (vertical)
                {
                    rect = new TreemapRect(free.X, free.Y + offset, thickness, length);
                }
                else
                {
                    rect = new TreemapRect(free.X + offset, free.Y, length, thickness);
                }

                output.Add(rect);
                offset += length;
            }

            if (vertical)
            {
                free = new TreemapRect(free.X + thickness, free.Y, Math.Max(0, free.Width - thickness), free.Height);
            }
            else
            {
                free = new TreemapRect(free.X, free.Y + thickness, free.Width, Math.Max(0, free.Height - thickness));
            }
        }
    }

    private static double Worst(List<double> row, double sum, double side)
    {
        double max = double.MinValue;
        double min = double.MaxValue;
        foreach (double area in row)
        {
            if (area > max)
            {
                max = area;
            }

            if (area < min)
            {
                min = area;
            }
        }

        if (min <= 0)
        {
            min = double.Epsilon;
        }

        double sumSquared = sum * sum;
        double sideSquared = side * side;
        return Math.Max((sideSquared * max) / sumSquared, sumSquared / (sideSquared * min));
    }
}

/// <summary>Tiles the space tree into layout rectangles for the treemap view.</summary>
public static class TreemapTiler
{
    public const int DefaultMaxDepth = 6;
    public const int DefaultMaxBlocks = 6000;

    public readonly record struct TileStats(int Blocks, int DepthReached, double CoveredArea);

    public static TileStats Tile(
        SpaceNode root,
        double width,
        double height,
        int maxDepth = DefaultMaxDepth,
        int maxBlocks = DefaultMaxBlocks)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (width <= 0 || height <= 0)
        {
            return new TileStats(0, 0, 0);
        }

        root.LayoutX = 0;
        root.LayoutY = 0;
        root.LayoutWidth = width;
        root.LayoutHeight = height;

        int blocks = 0;
        int deepest = 0;
        var queue = new Queue<(SpaceNode Node, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && blocks < maxBlocks)
        {
            (SpaceNode node, int depth) = queue.Dequeue();
            deepest = Math.Max(deepest, depth);

            if (depth >= maxDepth || node.Children.Count == 0)
            {
                continue;
            }

            var candidates = new List<SpaceNode>(node.Children.Count);
            var values = new List<double>(node.Children.Count);
            foreach (SpaceNode child in node.Children)
            {
                if (child.AllocatedBytes <= 0)
                {
                    continue;
                }

                candidates.Add(child);
                values.Add(child.AllocatedBytes);
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            IReadOnlyList<TreemapRect> rects = SquarifiedTreemap.Layout(
                values,
                node.LayoutWidth,
                node.LayoutHeight);

            for (int i = 0; i < rects.Count && blocks < maxBlocks; i++)
            {
                TreemapRect rect = rects[i];
                rect = new TreemapRect(
                    node.LayoutX + rect.X,
                    node.LayoutY + rect.Y,
                    rect.Width,
                    rect.Height);

                SpaceNode child = candidates[i];
                child.LayoutX = rect.X;
                child.LayoutY = rect.Y;
                child.LayoutWidth = rect.Width;
                child.LayoutHeight = rect.Height;
                blocks++;

                if (child.Children.Count > 0 && depth + 1 < maxDepth && rect.Area >= 48)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        double covered = 0;
        foreach (SpaceNode node in root.SelfAndDescendants())
        {
            if (node.Children.Count == 0 && node.LayoutWidth > 0)
            {
                covered += node.LayoutWidth * node.LayoutHeight;
            }
        }

        return new TileStats(blocks, deepest, covered);
    }

    public static SpaceNode? HitTest(SpaceNode root, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(root);
        SpaceNode? best = null;
        foreach (SpaceNode node in root.SelfAndDescendants())
        {
            if (node.LayoutWidth <= 0 || node.LayoutHeight <= 0 || node == root)
            {
                continue;
            }

            if (x >= node.LayoutX && x <= node.LayoutX + node.LayoutWidth &&
                y >= node.LayoutY && y <= node.LayoutY + node.LayoutHeight)
            {
                if (best is null || node.Depth >= best.Depth)
                {
                    best = node;
                }
            }
        }

        return best ?? root;
    }
}
