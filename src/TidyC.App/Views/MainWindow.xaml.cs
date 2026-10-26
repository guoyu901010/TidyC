using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TidyC.App.ViewModels;

namespace TidyC.App.Views;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x00000002;

    private readonly Dictionary<object, Border> _pageCache = new();
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        PreloadPages();
        ShowPage(_viewModel.CurrentPage);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        // Let the window paint first, then lay out the remaining pages at idle
        // priority so the very first click on each nav item is instant too.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(PrewarmPages));
    }

    private void PrewarmPages()
    {
        foreach (object page in EnumeratePages())
        {
            Border host = EnsurePage(page);
            if (host.Visibility != Visibility.Visible)
            {
                host.Visibility = Visibility.Visible;
                host.IsHitTestVisible = false;
                Panel.SetZIndex(host, 0);
            }
        }

        ShowPage(_viewModel.CurrentPage);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            ShowPage(_viewModel.CurrentPage);
        }
    }

    /// <summary>
    /// Parses every page once, at startup, so switching pages is a simple
    /// content swap instead of a fresh XAML parse (which caused the visible
    /// hitch on AppData / Treemap).
    /// </summary>
    private void PreloadPages()
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (object page in EnumeratePages())
        {
            EnsurePage(page);
        }

        stopwatch.Stop();
        Debug.WriteLine($"TidyC: preloaded {_pageCache.Count} pages in {stopwatch.ElapsedMilliseconds} ms");
    }

    private IEnumerable<object> EnumeratePages()
    {
        yield return _viewModel.Overview;
        yield return _viewModel.Treemap;
        yield return _viewModel.LargeFiles;
        yield return _viewModel.AppData;
        yield return _viewModel.Wizard;
        yield return _viewModel.Quarantine;
        yield return _viewModel.Records;
    }

    private Border EnsurePage(object viewModel)
    {
        if (_pageCache.TryGetValue(viewModel, out Border? cached))
        {
            return cached;
        }

        UserControl view = viewModel switch
        {
            OverviewViewModel => new OverviewView(),
            TreemapViewModel => new TreemapView(),
            LargeFilesViewModel => new LargeFilesView(),
            AppDataViewModel => new AppDataView(),
            CleanWizardViewModel => new CleanWizardView(),
            QuarantineViewModel => new QuarantineView(),
            ScanRecordsViewModel => new ScanRecordsView(),
            _ => new UserControl
            {
                Content = new TextBlock { Text = "未知页面", Margin = new Thickness(24) },
            },
        };

        view.DataContext = viewModel;

        // A page lives in its own opaque host: once shown it stays in the tree,
        // so switching pages is only a ZIndex change — no measure/arrange churn.
        var host = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("Brush.Surface.Base"),
            Child = view,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            ClipToBounds = true,
        };
        Panel.SetZIndex(host, 0);
        KeyboardNavigation.SetTabNavigation(host, KeyboardNavigationMode.None);

        _pageCache[viewModel] = host;
        PageHost.Children.Add(host);
        return host;
    }

    private void ShowPage(object? page)
    {
        if (page is null)
        {
            return;
        }

        Border selected = EnsurePage(page);
        selected.Visibility = Visibility.Visible;
        selected.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(selected, KeyboardNavigationMode.Continue);
        Panel.SetZIndex(selected, 1);

        foreach (Border host in _pageCache.Values)
        {
            if (ReferenceEquals(host, selected))
            {
                continue;
            }

            // Hidden (not Collapsed) keeps the prewarmed measure/arrange so the
            // next switch is still instant, while taking the page out of
            // rendering and the accessibility tree.
            host.Visibility = Visibility.Hidden;
            host.IsHitTestVisible = false;
            KeyboardNavigation.SetTabNavigation(host, KeyboardNavigationMode.None);
            Panel.SetZIndex(host, 0);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyWorkAreaBounds(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaBounds(IntPtr hwnd, IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        NativeRect work = monitorInfo.Work;
        NativeRect screen = monitorInfo.Monitor;
        info.MaxPosition.X = work.Left - screen.Left;
        info.MaxPosition.Y = work.Top - screen.Top;
        info.MaxSize.X = work.Right - work.Left;
        info.MaxSize.Y = work.Bottom - work.Top;
        info.MaxTrackSize = info.MaxSize;
        Marshal.StructureToPtr(info, lParam, true);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TidyC");
        Directory.CreateDirectory(directory);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
