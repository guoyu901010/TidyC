using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TidyC.App.Services;
using TidyC.App.ViewModels;
using TidyC.App.Views;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.App;

public partial class App : Application
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>True while rendering deterministic screenshots; suppresses toasts and demo warnings.</summary>
    public static bool CaptureMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(
                "TidyC 遇到未处理的异常：\n\n" + args.Exception.Message + "\n\n详情已写入日志。",
                "TidyC 异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunSelfTest();
            Shutdown(code);
            return;
        }

        // Announce a successful elevated start so the standard-privilege instance
        // can close itself once the admin window is really alive. The marker path
        // is passed explicitly because the two processes may resolve different
        // writable data directories.
        if (e.Args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                string marker = GetArgument(e.Args, "--marker")
                                ?? Path.Combine(AppPaths.DataDirectory, "elevated-started.json");
                string? markerDirectory = Path.GetDirectoryName(marker);
                if (!string.IsNullOrEmpty(markerDirectory))
                {
                    Directory.CreateDirectory(markerDirectory);
                }

                File.WriteAllText(
                    marker,
                    $"{{\"pid\":{Environment.ProcessId},\"time\":\"{DateTime.UtcNow:O}\"}}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        int benchIndex = Array.FindIndex(e.Args, a => a.Equals("--scan-bench", StringComparison.OrdinalIgnoreCase));
        if (benchIndex >= 0 && benchIndex + 1 < e.Args.Length)
        {
            int benchCode = RunScanBench(e.Args[benchIndex + 1], GetArgument(e.Args, "--out"));
            Shutdown(benchCode);
            return;
        }

        if (e.Args.Any(a => a.Equals("--mft-probe", StringComparison.OrdinalIgnoreCase)))
        {
            int probeCode = RunMftProbe(GetArgument(e.Args, "--out"));
            Shutdown(probeCode);
            return;
        }

        int optimizeIndex = Array.FindIndex(e.Args, a => a.Equals("--optimize-wsl", StringComparison.OrdinalIgnoreCase));
        if (optimizeIndex >= 0 && optimizeIndex + 1 < e.Args.Length)
        {
            RunWslCompact(e.Args[optimizeIndex + 1]);
            Shutdown(0);
            return;
        }

        int screenshotIndex = Array.FindIndex(e.Args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (screenshotIndex >= 0)
        {
            string directory = screenshotIndex + 1 < e.Args.Length
                ? e.Args[screenshotIndex + 1]
                : Path.Combine(AppPaths.DataDirectory, "screenshots");
            Directory.CreateDirectory(directory);
            string logPath = Path.Combine(directory, "screenshot.log");
            File.AppendAllText(logPath, $"{DateTime.Now:O} start dir={directory}\n");
            CaptureMode = true;

            try
            {
                bool emptyState = e.Args.Any(a => a.Equals("--empty", StringComparison.OrdinalIgnoreCase));
                AppServices.Initialize(demo: !emptyState);
                File.AppendAllText(logPath, $"{DateTime.Now:O} services ready demo={!emptyState} root={AppServices.Current.Quarantine.Root}\n");

                var shotWindow = new MainWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 0,
                    Top = 0,
                    Width = 1440,
                    Height = 920,
                    ShowInTaskbar = false,
                };
                MainWindow = shotWindow;
                shotWindow.Show();
                File.AppendAllText(logPath, $"{DateTime.Now:O} window shown actual={shotWindow.ActualWidth}x{shotWindow.ActualHeight}\n");
                _ = CaptureScreensAsync(shotWindow, directory, logPath);
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"{DateTime.Now:O} FATAL {ex}\n");
                Shutdown(1);
            }

            return;
        }

        bool demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        AppServices.Initialize(demo);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static int RunSelfTest()
    {
        AttachConsole(AttachParentProcess);
        SetConsoleOutputCP(65001);
        SetConsoleCP(65001);
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
        }

        Console.WriteLine();
        Console.WriteLine("==== TidyC 双引擎对拍自检 (--self-test) ====");

        try
        {
            SelfTestReport report = DualEngineSelfTest
                .RunAsync(@"C:\", new ScanOptions())
                .GetAwaiter()
                .GetResult();

            string reportDir = AppPaths.DataDirectory;
            Directory.CreateDirectory(reportDir);
            string json = DualEngineSelfTest.Serialize(report);
            string archivePath = Path.Combine(reportDir, "self-test-report.json");
            File.WriteAllText(archivePath, json);

            string sideBySide = Path.Combine(AppContext.BaseDirectory, "self-test-report.json");
            File.WriteAllText(sideBySide, json);

            Console.WriteLine(report.ToString());
            Console.WriteLine("报告：" + archivePath);
            Console.WriteLine("报告：" + sideBySide);
            return report.Passed ? 0 : 1;
        }
        catch (MftUnavailableException ex)
        {
            Console.WriteLine("MFT 直读不可用：" + ex.Message);
            Console.WriteLine("自检需要管理员权限；请通过 UAC 提权后重新运行 `TidyC.exe --self-test`。");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("自检失败：" + ex);
            return 1;
        }
    }

    private static async Task CaptureScreensAsync(MainWindow window, string directory, string logPath)
    {
        await Task.Delay(1400);
        var viewModel = window.DataContext as MainViewModel;
        if (viewModel is null)
        {
            return;
        }

        (string Page, string File)[] pages =
        {
            ("overview", "01-overview.png"),
            ("treemap", "02-treemap.png"),
            ("large", "03-large-files.png"),
            ("appdata", "04-appdata.png"),
            ("clean", "05-clean-wizard.png"),
            ("quarantine", "06-quarantine.png"),
            ("records", "07-scan-records.png"),
        };

        foreach ((string page, string file) in pages)
        {
            viewModel.Navigate(page);
            await Task.Delay(420);
            Capture(window, Path.Combine(directory, file), logPath);
        }

        // Navigation timing: pass 1 is the first visit, pass 2 hits the view cache.
        foreach (int pass in new[] { 1, 2 })
        {
            foreach ((string page, _) in pages)
            {
                var nav = Stopwatch.StartNew();
                viewModel.Navigate(page);
                double setMs = nav.Elapsed.TotalMilliseconds;
                window.UpdateLayout();
                nav.Stop();
                File.AppendAllText(
                    logPath,
                    $"{DateTime.Now:O} nav pass{pass} {page} set={setMs:0.0}ms layout={nav.Elapsed.TotalMilliseconds - setMs:0.0}ms\n");
                await Task.Delay(50);
            }
        }

        viewModel.Navigate("clean");
        await Task.Delay(250);
        viewModel.Wizard.NextCommand.Execute(null);
        await Task.Delay(350);
        Capture(window, Path.Combine(directory, "05b-wizard-confirm.png"), logPath);

        viewModel.Wizard.NextCommand.Execute(null);
        await Task.Delay(1200);
        Capture(window, Path.Combine(directory, "05c-wizard-result.png"), logPath);

        viewModel.Navigate("quarantine");
        await Task.Delay(300);
        if (viewModel.Quarantine.Batches.Count > 0)
        {
            viewModel.Quarantine.SelectedBatch = viewModel.Quarantine.Batches[0];
            await Task.Delay(250);
            Capture(window, Path.Combine(directory, "06b-quarantine-detail.png"), logPath);
        }

        window.Close();
        Current.Shutdown(0);
    }

    private static void Capture(Window window, string path, string logPath)
    {
        try
        {
            if (window.Content is not FrameworkElement element || element.ActualWidth < 10 || element.ActualHeight < 10)
            {
                File.AppendAllText(logPath, $"{DateTime.Now:O} skip {path} (no layout)\n");
                return;
            }

            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(element.ActualWidth),
                (int)Math.Ceiling(element.ActualHeight),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(element);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
            File.AppendAllText(logPath, $"{DateTime.Now:O} saved {Path.GetFileName(path)} {bitmap.PixelWidth}x{bitmap.PixelHeight}\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"{DateTime.Now:O} capture-failed {path}: {ex}\n");
        }
    }

    private static int RunMftProbe(string? outputPath)
    {
        AttachConsole(AttachParentProcess);
        SetConsoleOutputCP(65001);
        SetConsoleCP(65001);

        object payload;
        try
        {
            var scanner = new MftScanner();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ScanResult scan = scanner.Scan("C:\\", new ScanOptions());
            stopwatch.Stop();
            payload = new
            {
                ok = true,
                elevated = ElevationHelper.IsElevated,
                engine = scan.EngineType,
                source = MftScanner.LastSource,
                record_size = MftScanner.LastRecordSize,
                read_seconds = Math.Round(MftScanner.LastReadSeconds, 3),
                build_seconds = Math.Round(MftScanner.LastBuildSeconds, 3),
                mft_open_error = MftScanner.LastMftOpenError,
                backup_privilege = MftScanner.LastBackupPrivilege,
                rejected_by_status = MftScanner.LastRejectedByStatus.ToDictionary(p => p.Key.ToString(), p => p.Value),
                records_read = scan.TotalRecordsRead,
                rejected_records = MftScanner.LastRejectedRecords,
                top_files = scan.TopLargestFiles.Take(5).Select(f => new { path = f.Path, allocated = f.AllocatedBytes, logical = f.LogicalBytes }).ToArray(),
                files = scan.TotalFilesCount,
                directories = scan.TotalDirectoriesCount,
                bytes = scan.Reconciliation.EngineAttributedBytes,
                elapsed_seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            };
        }
        catch (Exception ex)
        {
            payload = new
            {
                ok = false,
                elevated = ElevationHelper.IsElevated,
                ntfs = ElevationHelper.GetVolumeFacts("C:\\").IsNtfs,
                error = ex.GetType().Name + ": " + ex.Message,
                detail = ex.ToString(),
            };
        }

        string json = System.Text.Json.JsonSerializer.Serialize(
            payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllText(outputPath, json);
        }

        return 0;
    }

    private static int RunScanBench(string root, string? outputPath)
    {
        AttachConsole(AttachParentProcess);
        SetConsoleOutputCP(65001);
        SetConsoleCP(65001);
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
        }

        Console.WriteLine($"TidyC scan bench · {root}");
        try
        {
            var service = new ScanService();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ScanResult scan = service
                .ScanAsync(root, new ScanOptions(), CancellationToken.None, new ConsoleScanProgress())
                .GetAwaiter()
                .GetResult();
            stopwatch.Stop();

            Console.WriteLine(
                $"engine={scan.EngineType} files={scan.TotalFilesCount:N0} dirs={scan.TotalDirectoriesCount:N0} " +
                $"bytes={scan.Reconciliation.EngineAttributedBytes:N0} elapsed={stopwatch.Elapsed.TotalSeconds:0.00}s");
            foreach (string warning in scan.Warnings)
            {
                Console.WriteLine("  warn: " + warning);
            }

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                string? directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                System.IO.File.WriteAllText(
                    outputPath,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        root,
                        engine = scan.EngineType,
                        elevated = ElevationHelper.IsElevated,
                        mft_available = MftScanner.IsAvailableFor(root),
                        top_files = scan.TopLargestFiles.Take(5).Select(f => new { path = f.Path, allocated = f.AllocatedBytes, logical = f.LogicalBytes }).ToArray(),
                files = scan.TotalFilesCount,
                        directories = scan.TotalDirectoriesCount,
                        attributed_bytes = scan.Reconciliation.EngineAttributedBytes,
                        elapsed_seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                        access_denied = scan.AccessDeniedCount,
                        warnings = scan.Warnings,
                        run_at = DateTime.Now,
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("bench failed: " + ex);
            return 1;
        }
    }

    private sealed class ConsoleScanProgress : IProgress<ScanProgressSnapshot>
    {
        private int _lastSecond = -1;

        public void Report(ScanProgressSnapshot snapshot)
        {
            int second = (int)snapshot.ElapsedSeconds;
            if (second == _lastSecond)
            {
                return;
            }

            _lastSecond = second;
            Console.WriteLine(
                $"  {snapshot.Phase} files={snapshot.Files:N0} dirs={snapshot.Directories:N0} " +
                $"bytes={snapshot.AttributedBytes:N0} t={snapshot.ElapsedSeconds:0.0}s");
        }
    }

    private static void RunWslCompact(string vhdxPath)
    {
        try
        {
            var service = new WslService();
            (bool success, string output) = service.CompactAsync(vhdxPath).GetAwaiter().GetResult();
            string logDir = AppPaths.DataDirectory;
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "wsl-compact.log"), output);

            MessageBox.Show(
                (success ? "虚拟磁盘压缩已完成。\n\n" : "压缩未成功。\n\n") + output,
                "WSL2 虚拟磁盘压缩",
                MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "WSL2 虚拟磁盘压缩失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            string directory = AppPaths.DataDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:O}] {exception}\n\n");
        }
        catch (IOException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint wCodePageID);
}
