using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace TidyC.Setup;

public partial class MainWindow : Window
{
    private bool _installing;

    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = InstallerEngine.DefaultInstallDir;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Match the native title bar to the dark installer body on Windows 10/11.
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
            }
        }
        catch (Exception)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 TidyC 安装位置",
            InitialDirectory = Directory.Exists(InstallPathBox.Text) ? InstallPathBox.Text : InstallerEngine.DefaultInstallDir,
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            InstallPathBox.Text = dialog.FolderName;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_installing)
        {
            Close();
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            return;
        }

        string path = InstallPathBox.Text.Trim();
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "安装路径无效，请重新选择。", "TidyC 安装程序", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (path.Length < 4 || string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "不能直接安装到磁盘根目录，请选择子目录。", "TidyC 安装程序", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (File.Exists(Path.Combine(path, "TidyC.exe")))
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                "该目录已存在 TidyC，是否覆盖安装？\n（隔离区与扫描记录不会受影响）",
                "TidyC 安装程序",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _installing = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        InstallPathBox.IsEnabled = false;
        InstallProgress.Value = 0;
        StatusText.Text = "正在准备安装…";

        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgress.Value = p.Percent;
            StatusText.Text = p.Message;
        });

        try
        {
            bool createDesktopShortcut = DesktopShortcutCheck.IsChecked == true;
            bool launchAfterInstall = LaunchCheck.IsChecked == true;
            InstallResult result = await Task.Run(() => InstallerEngine.Install(
                path,
                createDesktopShortcut,
                launchAfterInstall,
                progress));

            InstallProgress.Value = 100;
            StatusText.Text = "安装完成";
            string warningText = result.Warnings.Count == 0
                ? "开始菜单中已创建快捷方式，可在「设置 → 应用」中卸载。"
                : "注意：以下项目未能完成（不影响程序使用）：\n· " + string.Join("\n· ", result.Warnings);
            MessageBox.Show(
                this,
                $"TidyC 已安装到：\n{path}\n\n{warningText}",
                "TidyC 安装程序",
                MessageBoxButton.OK,
                result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            Close();
        }
        catch (Exception ex)
        {
            try
            {
                string log = Environment.GetEnvironmentVariable("TIDYC_SETUP_LOG")
                             ?? Path.Combine(Path.GetTempPath(), "TidyC-Setup-ui.log");
                File.AppendAllText(log, $"{DateTime.Now:O} UI install failed: {ex}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }

            StatusText.Text = "安装失败";
            MessageBox.Show(
                this,
                "安装失败：\n" + ex.Message,
                "TidyC 安装程序",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            _installing = false;
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            InstallPathBox.IsEnabled = true;
        }
    }
}
