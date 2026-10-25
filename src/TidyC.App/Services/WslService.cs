using System.Diagnostics;
using System.IO;
using System.Text;
using TidyC.Core.Sys;

namespace TidyC.App.Services;

public sealed class WslDisk
{
    public string Name { get; init; } = string.Empty;

    public string VhdxPath { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public bool Compactable => SizeBytes > 0 && File.Exists(VhdxPath);
}

/// <summary>
/// WSL2 / Docker virtual disks are sparse and never shrink on their own. TidyC
/// only compacts them through Microsoft's own tools (Optimize-VHD, or diskpart
/// as a fallback), and only from an elevated instance.
/// </summary>
public sealed class WslService
{
    public IReadOnlyList<WslDisk> Discover()
    {
        var disks = new List<WslDisk>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        AddDisks(disks, Path.Combine(localAppData, "Packages"), "LocalState\\ext4.vhdx", "Store 版 WSL");
        AddDisks(disks, Path.Combine(localAppData, "wsl"), "ext4.vhdx", "WSL 发行版");
        AddDisks(disks, Path.Combine(localAppData, "Docker", "wsl"), "data\\ext4.vhdx", "Docker Desktop");

        return disks.OrderByDescending(d => d.SizeBytes).ToList();
    }

    public async Task<(bool Success, string Output)> CompactAsync(string vhdxPath, CancellationToken ct = default)
    {
        if (!File.Exists(vhdxPath))
        {
            return (false, "虚拟磁盘不存在：" + vhdxPath);
        }

        if (!ElevationHelper.IsElevated)
        {
            return (false, "压缩虚拟磁盘需要管理员权限，请先提权重启");
        }

        var log = new StringBuilder();
        ProcessResult shutdown = await Task.Run(() => ShellProcessRunner.Instance.Run("wsl.exe", "--shutdown", TimeSpan.FromSeconds(60)), ct);
        log.AppendLine($"wsl --shutdown (exit={shutdown.ExitCode})");

        string escaped = vhdxPath.Replace("'", "''", StringComparison.Ordinal);
        string powerShell = $"Optimize-VHD -Path '{escaped}' -Mode Full; exit $LASTEXITCODE";
        ProcessResult optimize = await Task.Run(
            () => ShellProcessRunner.Instance.Run(
                "powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{powerShell}\"",
                TimeSpan.FromMinutes(30)),
            ct);

        log.AppendLine("Optimize-VHD:");
        log.AppendLine(optimize.StandardOutput.Trim());
        log.AppendLine(optimize.StandardError.Trim());

        if (optimize.Succeeded)
        {
            return (true, log.ToString());
        }

        // Fallback for machines without the Hyper-V PowerShell module.
        string script = Path.Combine(Path.GetTempPath(), $"tidyc-diskpart-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(script, string.Join(
                Environment.NewLine,
                $"select vdisk file=\"{vhdxPath}\"",
                "attach vdisk readonly",
                "compact vdisk",
                "detach vdisk",
                "exit"));

            ProcessResult diskpart = await Task.Run(
                () => ShellProcessRunner.Instance.Run("diskpart.exe", $"/s \"{script}\"", TimeSpan.FromMinutes(30)),
                ct);

            log.AppendLine($"diskpart (exit={diskpart.ExitCode}):");
            log.AppendLine(diskpart.StandardOutput.Trim());
            log.AppendLine(diskpart.StandardError.Trim());

            return (diskpart.Succeeded, log.ToString());
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch (IOException)
            {
            }
        }
    }

    public bool RelaunchElevatedForCompact(string vhdxPath)
    {
        string executable = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            var info = new ProcessStartInfo(executable, $"--optimize-wsl \"{vhdxPath}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            return Process.Start(info) is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void AddDisks(List<WslDisk> disks, string root, string relativePattern, string label)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string candidate = Path.Combine(directory, relativePattern);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                long size = 0;
                try
                {
                    size = new FileInfo(candidate).Length;
                }
                catch (IOException)
                {
                }

                disks.Add(new WslDisk
                {
                    Name = label + " · " + Path.GetFileName(directory),
                    VhdxPath = candidate,
                    SizeBytes = size,
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }
}
