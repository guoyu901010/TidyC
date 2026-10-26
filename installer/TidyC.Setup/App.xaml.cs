using System.IO;
using System.Windows;

namespace TidyC.Setup;

public partial class App : Application
{
    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Environment.GetEnvironmentVariable("TIDYC_SETUP_LOG") ?? Path.Combine(Path.GetTempPath(), "TidyC-Setup-trace.log"),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Trace("startup args=" + string.Join(" | ", e.Args));

        if (e.Args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            Trace("entering silent install");
            int code = InstallerEngine.RunSilent(e.Args);
            Trace("silent install returned " + code);
            Shutdown(code);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        Trace("main window shown");
    }
}
