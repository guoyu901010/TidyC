using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace TidyC.Setup;

public sealed record InstallProgress(int Percent, string Message);

public sealed record InstallResult(IReadOnlyList<string> Warnings);

public static class InstallerEngine
{
    public const string AppName = "TidyC";
    public const string AppVersion = "2.4.0";

    private const string PayloadResource = "TidyC.Setup.payload.zip";

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        AppName);

    public static InstallResult Install(
        string installDir,
        bool desktopShortcut,
        bool launch,
        IProgress<InstallProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new ArgumentException("安装路径为空", nameof(installDir));
        }

        installDir = Path.GetFullPath(installDir);
        Directory.CreateDirectory(installDir);

        Assembly assembly = typeof(InstallerEngine).Assembly;
        using Stream? payload = assembly.GetManifestResourceStream(PayloadResource);
        if (payload is null)
        {
            throw new InvalidOperationException("安装包缺少 payload.zip，请重新下载完整安装程序。");
        }

        progress?.Report(new InstallProgress(2, "正在解压程序文件…"));
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        string root = installDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        int total = Math.Max(1, archive.Entries.Count);
        int done = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("安装包包含非法路径：" + entry.FullName);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(target, overwrite: true);
            done++;

            if (done % 8 == 0 || done == total)
            {
                int percent = 2 + (int)(done * 78.0 / total);
                progress?.Report(new InstallProgress(percent, $"正在安装：{entry.Name}"));
            }
        }

        string exePath = Path.Combine(installDir, "TidyC.exe");
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException("安装包不完整：未找到 TidyC.exe。");
        }

        progress?.Report(new InstallProgress(84, "正在写入卸载程序…"));
        WriteUninstaller(installDir);

        progress?.Report(new InstallProgress(90, "正在创建快捷方式…"));
        var warnings = new List<string>(CreateShortcuts(installDir, exePath, desktopShortcut));

        progress?.Report(new InstallProgress(96, "正在登记卸载信息…"));
        try
        {
            WriteUninstallRegistry(installDir, exePath);
        }
        catch (Exception ex)
        {
            warnings.Add("卸载登记写入失败：" + ex.Message);
        }

        foreach (string warning in warnings)
        {
            Trace(warning);
        }

        progress?.Report(new InstallProgress(
            100,
            warnings.Count == 0 ? "安装完成" : "安装完成（部分快捷方式/卸载登记被系统策略阻止）"));

        if (launch)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = installDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // Shortcut/launch failure must not fail the install.
            }
        }

        return new InstallResult(warnings);
    }

    public static int RunSilent(string[] args)
    {
        try
        {
            string dir = GetArgument(args, "--dir") ?? DefaultInstallDir;
            bool desktop = !HasFlag(args, "--no-desktop");
            bool launch = HasFlag(args, "--launch");
            Trace($"silent install dir={dir} desktop={desktop} launch={launch}");
            Install(dir, desktop, launch, null);
            Trace("silent install completed");
            return 0;
        }
        catch (Exception ex)
        {
            Trace("silent install failed: " + ex);
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "TidyC-Setup-error.log"),
                    ex.ToString(),
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }

            return 1;
        }
    }

    private static string ResolveLogPath() =>
        Environment.GetEnvironmentVariable("TIDYC_SETUP_LOG")
        ?? Path.Combine(Path.GetTempPath(), "TidyC-Setup-trace.log");

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                ResolveLogPath(),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    private static IReadOnlyList<string> CreateShortcuts(string installDir, string exePath, bool desktopShortcut)
    {
        var warnings = new List<string>();
        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");

        string vbs = Path.Combine(installDir, "uninstall.vbs");
        string wscript = Path.Combine(Environment.SystemDirectory, "wscript.exe");

        try
        {
            Directory.CreateDirectory(startMenu);
            CreateShortcut(
                Path.Combine(startMenu, "TidyC.lnk"),
                exePath,
                installDir,
                "TidyC — Windows C 盘扫描与安全清理");
        }
        catch (Exception ex)
        {
            warnings.Add("开始菜单快捷方式创建失败：" + ex.Message);
        }

        try
        {
            CreateShortcut(
                Path.Combine(startMenu, "卸载 TidyC.lnk"),
                wscript,
                installDir,
                "卸载 TidyC",
                $"\"{vbs}\"");
        }
        catch (Exception ex)
        {
            warnings.Add("卸载快捷方式创建失败：" + ex.Message);
        }

        if (!desktopShortcut)
        {
            return warnings;
        }

        try
        {
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TidyC.lnk"),
                exePath,
                installDir,
                "TidyC — Windows C 盘扫描与安全清理");
        }
        catch (Exception ex)
        {
            warnings.Add("桌面快捷方式创建失败：" + ex.Message);
        }

        return warnings;
    }

    private static void CreateShortcut(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string description,
        string? arguments = null)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            throw new InvalidOperationException("系统未提供 WScript.Shell，无法创建快捷方式。");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(shortcutPath);
        link.TargetPath = targetPath;
        link.WorkingDirectory = workingDirectory;
        link.IconLocation = targetPath + ",0";
        link.Description = description;
        if (!string.IsNullOrEmpty(arguments))
        {
            link.Arguments = arguments;
        }

        link.Save();
    }

    private static void WriteUninstallRegistry(string installDir, string exePath)
    {
        string wscript = Path.Combine(Environment.SystemDirectory, "wscript.exe");
        string vbs = Path.Combine(installDir, "uninstall.vbs");

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TidyC",
            writable: true) ?? throw new InvalidOperationException("无法写入卸载登记表。");

        long sizeBytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
            {
                sizeBytes += new FileInfo(file).Length;
            }
        }
        catch (Exception)
        {
        }

        key.SetValue("DisplayName", "TidyC — C 盘扫描与安全清理");
        key.SetValue("DisplayVersion", AppVersion);
        key.SetValue("Publisher", "TidyC");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", exePath + ",0");
        key.SetValue("UninstallString", $"\"{wscript}\" \"{vbs}\"");
        key.SetValue("QuietUninstallString", $"\"{wscript}\" \"{vbs}\" --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Max(1, sizeBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

        using RegistryKey appKey = Registry.CurrentUser.CreateSubKey(@"Software\TidyC", writable: true)
                                   ?? throw new InvalidOperationException("无法写入 TidyC 注册表项。");
        appKey.SetValue("InstallPath", installDir);
        appKey.SetValue("Version", AppVersion);
    }

    private static void WriteUninstaller(string installDir)
    {
        const string ps1 = """"
param([switch]$Silent)

Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue

if (-not $Silent) {
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "确定要卸载 TidyC 吗？`n`n将删除程序文件与快捷方式；隔离区、扫描记录不会被删除。",
        "卸载 TidyC",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2)

    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { exit 0 }
}

Get-Process -Name TidyC -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
Remove-Item (Join-Path $startMenu 'TidyC.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $startMenu '卸载 TidyC.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'TidyC.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TidyC' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\TidyC' -Recurse -Force -ErrorAction SilentlyContinue

$dir = $PSScriptRoot
Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" `
    -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dir`"" `
    -WindowStyle Hidden

exit 0
"""";

        const string vbs = """"
Option Explicit
Dim shell, fso, dir, extra
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
extra = ""
If WScript.Arguments.Count > 0 Then
    If LCase(WScript.Arguments(0)) = "--silent" Then extra = " -Silent"
End If
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\uninstall.ps1""" & extra, 0, True
"""";

        File.WriteAllText(
            Path.Combine(installDir, "uninstall.ps1"),
            ps1,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        File.WriteAllText(
            Path.Combine(installDir, "uninstall.vbs"),
            vbs,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
