using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App.Controls;

/// <summary>
/// Squarified treemap host. Layout comes from Core (<see cref="TreemapTiler"/>);
/// this control only paints, hit-tests and raises drill-down events.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root),
        typeof(SpaceNode),
        typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStructureChanged));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode),
        typeof(SpaceNode),
        typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxDepthProperty = DependencyProperty.Register(
        nameof(MaxDepth),
        typeof(int),
        typeof(TreemapControl),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsRender, OnStructureChanged));

    private SpaceNode? _hover;
    private Size _tiledSize;
    private bool _tiled;

    public TreemapControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
    }

    public SpaceNode? Root
    {
        get => (SpaceNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public SpaceNode? SelectedNode
    {
        get => (SpaceNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public int MaxDepth
    {
        get => (int)GetValue(MaxDepthProperty);
        set => SetValue(MaxDepthProperty, value);
    }

    public event EventHandler<SpaceNode>? NodeActivated;

    public event EventHandler<SpaceNode>? NodeSelected;

    private static void OnStructureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TreemapControl)d;
        control._tiled = false;
        control._hover = null;
        control.InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _tiled = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);

        if (Root is null || ActualWidth < 8 || ActualHeight < 8)
        {
            DrawPlaceholder(dc, bounds);
            return;
        }

        if (!_tiled || _tiledSize != RenderSize)
        {
            TreemapTiler.Tile(Root, ActualWidth, ActualHeight, MaxDepth, 4000);
            _tiled = true;
            _tiledSize = RenderSize;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (SpaceNode node in Root.SelfAndDescendants())
        {
            if (node.LayoutWidth <= 0 || node.LayoutHeight <= 0)
            {
                continue;
            }

            var rect = new Rect(node.LayoutX, node.LayoutY, node.LayoutWidth, node.LayoutHeight);
            var inner = Rect.Inflate(rect, -1.2, -1.2);
            if (inner.Width < 1.2 || inner.Height < 1.2)
            {
                continue;
            }

            Color baseColor;
            try
            {
                baseColor = ColorFromCategory(node.Category);
            }
            catch (FormatException)
            {
                baseColor = Color.FromRgb(0x4B, 0x4B, 0x4D);
            }

            double factor = Math.Min(0.9, 0.34 + (node.Depth * 0.13));
            var brush = new SolidColorBrush(Blend(baseColor, factor));
            brush.Freeze();

            dc.DrawRoundedRectangle(brush, null, inner, 2.5, 2.5);

            bool hasTiledChildren = node.Children.Any(c => c.LayoutWidth > 0);
            if (!hasTiledChildren && inner.Width > 58 && inner.Height > 20)
            {
                DrawLabel(dc, node, inner, dpi.PixelsPerDip);
            }
        }

        if (_hover is not null && _hover.LayoutWidth > 0)
        {
            var rect = new Rect(_hover.LayoutX, _hover.LayoutY, _hover.LayoutWidth, _hover.LayoutHeight);
            Rect inner = Rect.Inflate(rect, -1, -1);
            if (inner.Width > 0 && inner.Height > 0)
            {
                var overlay = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
                overlay.Freeze();
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.4);
                pen.Freeze();
                dc.DrawRoundedRectangle(overlay, pen, inner, 2.5, 2.5);
            }
        }

        if (SelectedNode is not null && SelectedNode.LayoutWidth > 0)
        {
            var rect = new Rect(SelectedNode.LayoutX, SelectedNode.LayoutY, SelectedNode.LayoutWidth, SelectedNode.LayoutHeight);
            Rect inner = Rect.Inflate(rect, -1.4, -1.4);
            if (inner.Width > 0 && inner.Height > 0)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), 2);
                pen.Freeze();
                dc.DrawRoundedRectangle(null, pen, inner, 2.5, 2.5);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Before the first scan the treemap has no root; hovering must be inert
        // rather than calling HitTest(null).
        if (Root is null)
        {
            if (_hover is not null || ToolTip is not null)
            {
                _hover = null;
                ToolTip = null;
                InvalidateVisual();
            }

            return;
        }

        Point point = e.GetPosition(this);
        SpaceNode? hit = TreemapTiler.HitTest(Root, point.X, point.Y);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            ToolTip = hit is null || ReferenceEquals(hit, Root)
                ? null
                : $"{hit.FullPath}\n{ByteSize.Format(hit.AllocatedBytes)} · {hit.FileCount:N0} 个文件";
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Root is null)
        {
            return;
        }

        Point point = e.GetPosition(this);
        SpaceNode? hit = TreemapTiler.HitTest(Root, point.X, point.Y);
        if (hit is null)
        {
            return;
        }

        SelectedNode = hit;
        NodeSelected?.Invoke(this, hit);

        if (e.ClickCount >= 2 && hit.Children.Count > 0)
        {
            NodeActivated?.Invoke(this, hit);
        }
    }

    private void DrawLabel(DrawingContext dc, SpaceNode node, Rect rect, double pixelsPerDip)
    {
        double fontSize = rect.Height > 46 ? 12 : 10.5;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(
            node.Name,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(4, rect.Width - 12),
            MaxTextHeight = Math.Max(4, rect.Height - 8),
            Trimming = TextTrimming.CharacterEllipsis,
        };

        dc.DrawText(text, new Point(rect.X + 6, rect.Y + 4));

        if (rect.Height > 40 && rect.Width > 76)
        {
            var sizeText = new FormattedText(
                ByteSize.Format(node.AllocatedBytes),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                10,
                new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                pixelsPerDip)
            {
                MaxTextWidth = Math.Max(4, rect.Width - 12),
                Trimming = TextTrimming.CharacterEllipsis,
            };

            dc.DrawText(sizeText, new Point(rect.X + 6, rect.Y + 4 + fontSize + 2));
        }
    }

    private void DrawPlaceholder(DrawingContext dc, Rect bounds)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x15));
        dc.DrawRectangle(brush, null, bounds);
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(
            "扫描后在此渲染 Squarified 矩形树图",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            12,
            new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6E)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center,
        };

        dc.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
    }

    private static Color ColorFromCategory(string category)
    {
        string hex = category switch
        {
            StorageCategories.WindowsSystem => "#3A5A97",
            StorageCategories.AppData => "#D83B01",
            StorageCategories.UserProfiles => "#107C41",
            StorageCategories.ProgramFiles => "#5C2D91",
            StorageCategories.ProtectedSystem => "#8A8886",
            StorageCategories.Quarantine => "#0078D4",
            _ => "#4B4B4D",
        };

        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private static Color Blend(Color color, double amount)
    {
        // Darken toward the surface colour so nesting reads as depth.
        double red = color.R * amount + 0x14 * (1 - amount);
        double green = color.G * amount + 0x14 * (1 - amount);
        double blue = color.B * amount + 0x15 * (1 - amount);
        return Color.FromRgb((byte)red, (byte)green, (byte)blue);
    }
}
