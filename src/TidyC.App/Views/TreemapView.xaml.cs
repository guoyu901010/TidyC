using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TidyC.App.ViewModels;
using TidyC.Core.Scan;

namespace TidyC.App.Views;

public partial class TreemapView : UserControl
{
    public TreemapView()
    {
        InitializeComponent();
    }

    private void Treemap_NodeSelected(object? sender, SpaceNode node) =>
        (DataContext as TreemapViewModel)?.Select(node);

    private void Treemap_NodeActivated(object? sender, SpaceNode node) =>
        (DataContext as TreemapViewModel)?.Activate(node);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (TreemapHostBorder.ActualWidth < 8 || TreemapHostBorder.ActualHeight < 8)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出树图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"tidyc-treemap-{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            const double scale = 2.0;
            double width = TreemapHostBorder.ActualWidth;
            double height = TreemapHostBorder.ActualHeight;

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawRectangle(
                    new VisualBrush(TreemapHostBorder),
                    null,
                    new Rect(0, 0, width, height));
                context.Pop();
            }

            var bitmap = new RenderTargetBitmap(
                (int)(width * scale),
                (int)(height * scale),
                96 * scale,
                96 * scale,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show("导出失败：" + ex.Message, "导出树图", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
