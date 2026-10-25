using System.Security.Principal;
using TidyC.Core.Interop;
using TidyC.Core.Scan;

namespace TidyC.Core.Sys;

/// <summary>
/// Least privilege helper: the app ships as <c>asInvoker</c> and only asks for
/// UAC when the MFT reader or a privileged cleaner is actually needed.
/// </summary>
public static class ElevationHelper
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsElevated
    {
        get
        {
            if (!IsWindows)
            {
                return false;
            }

            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static VolumeFacts GetVolumeFacts(string root)
    {
        string normalizedRoot = PathKey.Normalize(root);
        var facts = new VolumeFacts { Root = normalizedRoot };
        if (!IsWindows)
        {
            return facts;
        }

        try
        {
            var nameBuffer = new System.Text.StringBuilder(261);
            var fsBuffer = new System.Text.StringBuilder(261);
            if (NativeMethods.GetVolumeInformationW(
                    PathKey.WithTrailingSeparator(normalizedRoot),
                    nameBuffer,
                    (uint)nameBuffer.Capacity,
                    out uint serial,
                    out uint maxComponent,
                    out uint fsFlags,
                    fsBuffer,
                    (uint)fsBuffer.Capacity))
            {
                facts.SerialNumber = serial;
                facts.FileSystem = fsBuffer.ToString();
                facts.IsNtfs = facts.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
                _ = maxComponent;
                _ = fsFlags;
            }
        }
        catch (Exception)
        {
            // Reporting only: never fail a scan because metadata is unavailable.
        }

        try
        {
            if (NativeMethods.GetDiskFreeSpaceExW(
                    PathKey.WithTrailingSeparator(normalizedRoot),
                    out ulong freeToCaller,
                    out ulong total,
                    out ulong totalFree))
            {
                facts.TotalBytes = (long)Math.Min(total, long.MaxValue);
                facts.FreeBytes = (long)Math.Min(Math.Min(freeToCaller, totalFree), long.MaxValue);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            string volumePath = @"\\.\" + normalizedRoot.TrimEnd('\\');
            using var volume = NativeMethods.CreateFileW(
                volumePath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (!volume.IsInvalid && NativeMethods.TryGetNtfsVolumeData(volume, out NativeMethods.NTFS_VOLUME_DATA_BUFFER data))
            {
                facts.NtfsVolumeDataAvailable = true;
                facts.ClusterSizeBytes = data.BytesPerCluster;
                facts.SerialNumber = unchecked((uint)data.VolumeSerialNumber);
                if (facts.TotalBytes == 0)
                {
                    facts.TotalBytes = data.TotalClusters * data.BytesPerCluster;
                    facts.FreeBytes = data.FreeClusters * data.BytesPerCluster;
                }
            }
        }
        catch (Exception)
        {
        }

        if (facts.ClusterSizeBytes == 0)
        {
            facts.ClusterSizeBytes = 4096;
        }

        return facts;
    }

    public static bool IsNtfsVolume(string root) => GetVolumeFacts(root).IsNtfs;

    /// <summary>
    /// Enables a token privilege (e.g. SeBackupPrivilege) for the current
    /// process. Opening the NTFS $MFT metafile needs backup semantics even for
    /// an elevated administrator, because its ACL only grants SYSTEM read.
    /// </summary>
    public static bool TryEnablePrivilege(string privilegeName)
    {
        if (!IsWindows)
        {
            return false;
        }

        IntPtr token = IntPtr.Zero;
        try
        {
            if (!Interop.NativeMethods.OpenProcessToken(
                    Interop.NativeMethods.GetCurrentProcess(),
                    Interop.NativeMethods.TOKEN_ADJUST_PRIVILEGES | Interop.NativeMethods.TOKEN_QUERY,
                    out token))
            {
                return false;
            }

            if (!Interop.NativeMethods.LookupPrivilegeValueW(null, privilegeName, out Interop.NativeMethods.LUID luid))
            {
                return false;
            }

            var privileges = new Interop.NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = Interop.NativeMethods.SE_PRIVILEGE_ENABLED,
            };

            if (!Interop.NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                Interop.NativeMethods.CloseHandle(token);
            }
        }
    }

    /// <summary>Relaunch outcome, including why the UAC request failed.</summary>
    public sealed record ElevationAttempt(bool Started, string ExecutablePath, int ErrorCode, string Message);

    /// <summary>
    /// Relaunches the application elevated through the shell's runas verb. Works
    /// both for the published single-file exe and for a framework-dependent
    /// `dotnet TidyC.dll` launch (where ProcessPath is dotnet.exe), and reports a
    /// precise reason instead of silently doing nothing when UAC is unavailable.
    /// </summary>
    public static ElevationAttempt RelaunchElevated(string arguments = "")
    {
        if (!IsWindows)
        {
            return new ElevationAttempt(false, string.Empty, 0, "仅支持 Windows");
        }

        if (IsElevated)
        {
            return new ElevationAttempt(false, string.Empty, 0, "当前已是管理员权限");
        }

        (string executable, string prefix) = ResolveElevationTarget();
        if (string.IsNullOrEmpty(executable))
        {
            return new ElevationAttempt(false, string.Empty, 0, "无法定位 TidyC 可执行文件");
        }

        string fullArguments = string.IsNullOrWhiteSpace(prefix) ? arguments : prefix + " " + arguments;

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(executable, fullArguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            };

            System.Diagnostics.Process? process = System.Diagnostics.Process.Start(info);
            return process is null
                ? new ElevationAttempt(false, executable, 0, "系统未能创建提权进程")
                : new ElevationAttempt(true, executable, 0, "已请求 UAC 确认");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            string message = ex.NativeErrorCode switch
            {
                1223 => "用户取消了 UAC 提权提示",
                5 => "系统拒绝了提权请求（当前会话可能禁止 UAC 弹窗）",
                _ => $"Win32 错误 {ex.NativeErrorCode}：{ex.Message}",
            };
            return new ElevationAttempt(false, executable, ex.NativeErrorCode, message);
        }
        catch (Exception ex)
        {
            return new ElevationAttempt(false, executable, 0, ex.Message);
        }
    }

    public static bool TryRelaunchElevated(string arguments = "", string? executablePath = null) =>
        RelaunchElevated(arguments).Started;

    private static (string Executable, string Prefix) ResolveElevationTarget()
    {
        string? processPath = Environment.ProcessPath;
        string processName = processPath is null ? string.Empty : Path.GetFileNameWithoutExtension(processPath);

        if (processPath is not null && processName.Equals("TidyC", StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, string.Empty);
        }

        // Framework-dependent start: elevate the dotnet host with the app dll.
        // CommandLineArgs[0] is the dll path in that case (Assembly.Location is
        // empty inside a single-file bundle, hence not used here).
        string? entryArgument = Environment.GetCommandLineArgs().FirstOrDefault();
        if (processPath is not null &&
            processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            entryArgument is not null &&
            entryArgument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(entryArgument))
        {
            return (processPath, $"\"{entryArgument}\"");
        }

        string appHost = Path.Combine(AppContext.BaseDirectory, "TidyC.exe");
        if (File.Exists(appHost))
        {
            return (appHost, string.Empty);
        }

        return (processPath ?? string.Empty, string.Empty);
    }

    public static long GetRecycleBinBytes()
    {
        if (!IsWindows)
        {
            return 0;
        }

        try
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            return NativeMethods.SHQueryRecycleBinW(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
