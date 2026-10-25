using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TidyC.Core.Sys;

namespace TidyC.App.Controls;

public sealed class SpectrumSegment
{
    public string Label { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public string Category { get; init; } = string.Empty;
}

/// <summary>One-line, proportional holographic spectrum of the volume usage.</summary>
public sealed class SpectrumBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<SpectrumSegment>),
        typeof(SpectrumBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private int _hoverIndex = -1;

    public SpectrumBar()
    {
        Height = 30;
        ClipToBounds = true;
    }

    public IReadOnlyList<SpectrumSegment>? Segments
    {
        get => (IReadOnlyList<SpectrumSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var empty = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x21));
        dc.DrawRoundedRectangle(empty, null, bounds, 4, 4);

        IReadOnlyList<SpectrumSegment>? segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            return;
        }

        long total = segments.Sum(s => Math.Max(0, s.Bytes));
        if (total <= 0)
        {
            return;
        }

        const double gap = 2;
        double available = Math.Max(0, ActualWidth - (gap * (segments.Count - 1)));
        double x = 0;
        int index = 0;

        foreach (SpectrumSegment segment in segments)
        {
            double width = available * (Math.Max(0, segment.Bytes) / (double)total);
            if (width < 1)
            {
                index++;
                continue;
            }

            var rect = new Rect(x, 0, width, ActualHeight);
            var brush = new SolidColorBrush(index == _hoverIndex ? Lighten(CategoryColor(segment.Category)) : CategoryColor(segment.Category));
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, rect, Math.Min(4, width / 2), 4);
            x += width + gap;
            index++;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        int index = IndexAt(point.X);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            ToolTip = index >= 0 && Segments is not null && index < Segments.Count
                ? $"{Segments[index].Label} · {ByteSize.Format(Segments[index].Bytes)}"
                : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        ToolTip = null;
        InvalidateVisual();
    }

    private int IndexAt(double x)
    {
        IReadOnlyList<SpectrumSegment>? segments = Segments;
        if (segments is null || segments.Count == 0 || ActualWidth <= 0)
        {
            return -1;
        }

        long total = segments.Sum(s => Math.Max(0, s.Bytes));
        if (total <= 0)
        {
            return -1;
        }

        const double gap = 2;
        double available = Math.Max(0, ActualWidth - (gap * (segments.Count - 1)));
        double cursor = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            double width = available * (Math.Max(0, segments[i].Bytes) / (double)total);
            if (x >= cursor && x <= cursor + width)
            {
                return i;
            }

            cursor += width + gap;
        }

        return -1;
    }

    private static Color Lighten(Color color) =>
        Color.FromRgb(
            (byte)Math.Min(255, color.R * 1.25 + 12),
            (byte)Math.Min(255, color.G * 1.25 + 12),
            (byte)Math.Min(255, color.B * 1.25 + 12));

    internal static Color CategoryColor(string category) => category switch
    {
        "WindowsSystem" => Color.FromRgb(0x3A, 0x5A, 0x97),
        "AppData" => Color.FromRgb(0xD8, 0x3B, 0x01),
        "UserProfiles" => Color.FromRgb(0x10, 0x7C, 0x41),
        "ProgramFiles" => Color.FromRgb(0x5C, 0x2D, 0x91),
        "ProtectedSystem" => Color.FromRgb(0x8A, 0x88, 0x86),
        "Quarantine" => Color.FromRgb(0x00, 0x78, 0xD4),
        _ => Color.FromRgb(0x4B, 0x4B, 0x4D),
    };
}

/// <summary>Thin in-row bar used by the largest-file / folder tables.</summary>
public sealed class ProportionBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(long),
        typeof(ProportionBar),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(long),
        typeof(ProportionBar),
        new FrameworkPropertyMetadata(1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush),
        typeof(Brush),
        typeof(ProportionBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ProportionBar()
    {
        Height = 5;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public long Value
    {
        get => (long)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public long Maximum
    {
        get => (long)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var track = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, ActualWidth, ActualHeight), 2.5, 2.5);

        long maximum = Math.Max(1, Maximum);
        double ratio = Math.Clamp(Value / (double)maximum, 0, 1);
        double width = ActualWidth * ratio;
        if (width < 2)
        {
            return;
        }

        Brush brush = BarBrush ?? new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, width, ActualHeight), 2.5, 2.5);
    }
}

/// <summary>Donut gauge used for the attribution ratio in the overview.</summary>
public sealed class DonutGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent),
        typeof(double),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness),
        typeof(double),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(9d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundBrushProperty = DependencyProperty.Register(
        nameof(ForegroundBrush),
        typeof(Brush),
        typeof(DonutGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public Brush? ForegroundBrush
    {
        get => (Brush?)GetValue(ForegroundBrushProperty);
        set => SetValue(ForegroundBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2)
        {
            return;
        }

        double thickness = Math.Max(2, RingThickness);
        double radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), thickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        double percent = Math.Clamp(Percent, 0, 1);
        if (percent <= 0.0001)
        {
            return;
        }

        Brush brush = ForegroundBrush ?? new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        if (percent >= 0.9999)
        {
            dc.DrawEllipse(null, new Pen(brush, thickness), center, radius, radius);
            return;
        }

        double angle = (percent * 360) - 90;
        double radians = angle * Math.PI / 180;
        Point start = new(center.X, center.Y - radius);
        Point end = new(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, percent > 0.5, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
