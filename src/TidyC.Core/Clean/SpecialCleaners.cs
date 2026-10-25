using TidyC.Core.Interop;
using TidyC.Core.Sys;

namespace TidyC.Core.Clean;

public sealed class SpecialCleanerResult
{
    public string Action { get; init; } = string.Empty;

    public bool Success { get; init; }

    public int ExitCode { get; init; }

    public long FreedBytes { get; init; }

    public string Output { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool RequiresElevation { get; init; }

    public override string ToString() =>
        $"{Action}: {(Success ? "OK" : "FAIL")} exit={ExitCode} {Message}".Trim();
}

/// <summary>
/// System data that must never be deleted by hand: hiberfil.sys, the component
/// store, restore points and the recycle bin. Each one goes through its official
/// channel (powercfg / DISM / vssadmin / Shell) and is refused without elevation.
/// </summary>
public sealed class SpecialCleaners
{
    private readonly IProcessRunner _runner;

    public SpecialCleaners(IProcessRunner? runner = null) =>
        _runner = runner ?? ShellProcessRunner.Instance;

    public static bool IsElevated => ElevationHelper.IsElevated;

    public SpecialCleanerResult DisableHibernation()
    {
        if (!IsElevated)
        {
            return RequiresAdmin("关闭休眠（powercfg /h off）");
        }

        ProcessResult result = _runner.Run("powercfg.exe", "/h off", TimeSpan.FromMinutes(2));
        long freed = 0;
        try
        {
            string hiberfil = Path.Combine(
                Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
                "hiberfil.sys");
            if (File.Exists(hiberfil))
            {
                freed = new FileInfo(hiberfil).Length;
            }
        }
        catch (IOException)
        {
        }

        return new SpecialCleanerResult
        {
            Action = "关闭休眠并释放 hiberfil.sys",
            Success = result.Succeeded,
            ExitCode = result.ExitCode,
            FreedBytes = result.Succeeded ? freed : 0,
            Output = Trim(result),
            Message = result.Succeeded
                ? "powercfg 已关闭休眠，hiberfil.sys 由系统收回（未强删文件）"
                : "powercfg 执行失败，hiberfil.sys 保持原状",
        };
    }

    public SpecialCleanerResult CleanComponentStore(bool resetBase = false)
    {
        if (!IsElevated)
        {
            return RequiresAdmin("WinSxS 组件清理（DISM）");
        }

        string arguments = "/Online /Cleanup-Image /StartComponentCleanup" + (resetBase ? " /ResetBase" : string.Empty);
        ProcessResult result = _runner.Run("dism.exe", arguments, TimeSpan.FromHours(2));

        return new SpecialCleanerResult
        {
            Action = resetBase ? "DISM 组件清理（ResetBase）" : "DISM 组件清理",
            Success = result.Succeeded,
            ExitCode = result.ExitCode,
            Output = Trim(result),
            Message = result.Succeeded
                ? "DISM 已完成组件清理，释放量以 dism 输出为准"
                : "DISM 未完成（详见输出）",
        };
    }

    public SpecialCleanerResult DeleteOldRestorePoints(bool all = false)
    {
        if (!IsElevated)
        {
            return RequiresAdmin("还原点清理（vssadmin）");
        }

        string drive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\');
        string arguments = all
            ? $"delete shadows /for={drive} /all /quiet"
            : $"delete shadows /for={drive} /oldest /quiet";

        ProcessResult result = _runner.Run("vssadmin.exe", arguments, TimeSpan.FromMinutes(5));

        return new SpecialCleanerResult
        {
            Action = all ? "删除全部还原点" : "删除最旧还原点",
            Success = result.Succeeded,
            ExitCode = result.ExitCode,
            Output = Trim(result),
            Message = result.Succeeded
                ? "vssadmin 已按请求删除卷影副本"
                : "vssadmin 执行失败（可能是系统保护未开启）",
        };
    }

    public SpecialCleanerResult EmptyRecycleBin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SpecialCleanerResult { Action = "清空回收站", Message = "仅支持 Windows" };
        }

        long before = QueryRecycleBinBytes();
        try
        {
            int hr = NativeMethods.SHEmptyRecycleBinW(
                IntPtr.Zero,
                null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);

            bool ok = hr == 0;
            return new SpecialCleanerResult
            {
                Action = "清空回收站（Shell API）",
                Success = ok,
                ExitCode = hr,
                FreedBytes = ok ? before : 0,
                Message = ok ? "回收站已通过 Shell API 清空" : $"Shell API 返回 0x{hr:X8}",
            };
        }
        catch (Exception ex)
        {
            return new SpecialCleanerResult
            {
                Action = "清空回收站（Shell API）",
                Success = false,
                Message = ex.Message,
            };
        }
    }

    public long QueryRecycleBinBytes() => ElevationHelper.GetRecycleBinBytes();

    private static SpecialCleanerResult RequiresAdmin(string action) => new()
    {
        Action = action,
        Success = false,
        RequiresElevation = true,
        Message = "该操作需要管理员权限，请通过 UAC 提权后重试",
    };

    private static string Trim(ProcessResult result)
    {
        string text = (result.StandardOutput + result.StandardError).Trim();
        return text.Length <= 2000 ? text : text.Substring(0, 2000) + "…";
    }
}
