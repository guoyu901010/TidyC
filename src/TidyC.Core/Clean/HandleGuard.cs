using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TidyC.Core.Interop;
using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.Core.Clean;

/// <summary>Anti-TOCTOU gatekeeper: re-verifies FileId + size by handle before and after every destructive operation.</summary>
public static class HandleGuard
{
    public static GuardResult Capture(string path, out FileIdentity identity)
    {
        identity = default;
        if (!TryOpen(path, out SafeFileHandle? handle, out GuardResult? failure))
        {
            return failure!;
        }

        using (handle!)
        {
            GuardResult? identityFailure = ReadIdentity(handle!, out identity);
            if (identityFailure is not null)
            {
                return identityFailure;
            }

            if (identity.IsReparsePoint)
            {
                return GuardResult.Fail(
                    GuardStatus.ReparsePointRejected,
                    $"目标是指向其他位置的联接点/符号链接，拒绝操作：{path}");
            }

            return GuardResult.Ok();
        }
    }

    public static FileIdentity? TryCapture(string path) =>
        Capture(path, out FileIdentity identity).IsSuccess ? identity : null;

    public static GuardResult OpenVerified(
        string path,
        FileIdentity? expected,
        out SafeFileHandle? handle,
        out FileIdentity actual,
        bool enforceProtectedPaths = true)
    {
        actual = default;
        handle = null;

        if (!TryOpen(path, out handle, out GuardResult? failure))
        {
            return failure!;
        }

        GuardResult? identityFailure = ReadIdentity(handle!, out actual);
        if (identityFailure is not null)
        {
            handle!.Dispose();
            handle = null;
            return identityFailure;
        }

        if (actual.IsReparsePoint)
        {
            handle!.Dispose();
            handle = null;
            return GuardResult.Fail(
                GuardStatus.ReparsePointRejected,
                $"扫描后目标被替换为联接点/符号链接，已拒绝执行：{path}");
        }

        if (enforceProtectedPaths && RuleValidator.IsForbiddenCleanRoot(path))
        {
            handle!.Dispose();
            handle = null;
            return GuardResult.Fail(
                GuardStatus.ProtectedPathRejected,
                $"目标位于硬编码禁清区，已熔断：{path}");
        }

        if (expected is { } exp)
        {
            if (!actual.SameObject(exp))
            {
                handle!.Dispose();
                handle = null;
                return GuardResult.Fail(
                    GuardStatus.IdentityMismatch,
                    $"FileId 已变化（扫描时 {exp.FileIdHex}，当前 {actual.FileIdHex}），疑似文件被替换，拒绝操作：{path}");
            }

            if (actual.LogicalBytes != exp.LogicalBytes)
            {
                handle!.Dispose();
                handle = null;
                return GuardResult.Fail(
                    GuardStatus.SizeMismatch,
                    $"逻辑大小已变化（扫描时 {exp.LogicalBytes} B，当前 {actual.LogicalBytes} B），拒绝操作：{path}");
            }
        }

        return GuardResult.Ok();
    }

    public static GuardResult DeleteVerified(
        string path,
        FileIdentity? expected = null,
        bool enforceProtectedPaths = true)
    {
        // A read-only attribute blocks deletion; clearing it is a metadata-only
        // change and the delete itself still goes through the verified handle.
        try
        {
            string extended = NativeMethods.ToExtendedPath(PathKey.Normalize(path));
            if (File.Exists(extended))
            {
                FileAttributes attributes = File.GetAttributes(extended);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(extended, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception)
        {
            // Fall through: the handle-level clear below is the second chance.
        }

        GuardResult opened = OpenVerified(path, expected, out SafeFileHandle? handle, out _, enforceProtectedPaths);
        if (!opened.IsSuccess)
        {
            return opened;
        }

        using (handle!)
        {
            if (NativeMethods.TryGetAttributeTagInfo(handle!, out NativeMethods.FILE_ATTRIBUTE_TAG_INFO tag) &&
                (tag.FileAttributes & NativeMethods.FILE_ATTRIBUTE_READONLY) != 0)
            {
                NativeMethods.ClearReadOnly(handle!, tag.FileAttributes);
            }

            if (!NativeMethods.SetDispositionDelete(handle!))
            {
                int error = Marshal.GetLastWin32Error();
                return GuardResult.Fail(MapDeleteError(error), $"句柄级删除失败（Win32 {error}）：{path}", error);
            }
        }

        if (Directory.Exists(NativeMethods.ToExtendedPath(path)) || File.Exists(NativeMethods.ToExtendedPath(path)))
        {
            return GuardResult.Fail(GuardStatus.DeleteFailed, $"删除后目标仍存在：{path}");
        }

        return GuardResult.Ok();
    }

    /// <summary>
    /// Removes a reparse point (junction / symlink) itself. The handle is opened
    /// with OPEN_REPARSE_POINT, so only the link is deleted — never its target.
    /// </summary>
    public static GuardResult DeleteReparsePointVerified(string path)
    {
        if (!TryOpen(path, out SafeFileHandle? handle, out GuardResult? failure))
        {
            return failure!;
        }

        using (handle!)
        {
            GuardResult? identityFailure = ReadIdentity(handle!, out FileIdentity identity);
            if (identityFailure is not null)
            {
                return identityFailure;
            }

            if (!identity.IsReparsePoint)
            {
                return GuardResult.Fail(
                    GuardStatus.InvalidPath,
                    $"目标不是联接点/符号链接，拒绝按链接删除：{path}");
            }

            if (!NativeMethods.SetDispositionDelete(handle!))
            {
                int error = Marshal.GetLastWin32Error();
                return GuardResult.Fail(MapDeleteError(error), $"联接点删除失败（Win32 {error}）：{path}", error);
            }
        }

        if (Directory.Exists(NativeMethods.ToExtendedPath(path)) || File.Exists(NativeMethods.ToExtendedPath(path)))
        {
            return GuardResult.Fail(GuardStatus.DeleteFailed, $"联接点删除后仍然存在：{path}");
        }

        return GuardResult.Ok();
    }

    public static GuardResult DeleteTreeVerified(string path, bool enforceProtectedPaths = true)
    {
        string root = PathKey.Normalize(path);
        string extended = NativeMethods.ToExtendedPath(root);

        if (!Directory.Exists(extended))
        {
            return File.Exists(extended)
                ? DeleteVerified(root, null, enforceProtectedPaths)
                : GuardResult.Fail(GuardStatus.NotFound, $"目标不存在：{root}");
        }

        var probe = new FileSystemProbe();
        var errors = new List<string>();
        var directories = new List<(string Path, int Depth)>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            (string current, int depth) = stack.Pop();
            directories.Add((current, depth));

            DirectoryListing listing = probe.List(current);
            if (listing.AccessDenied)
            {
                errors.Add($"拒绝访问：{current}");
                continue;
            }

            foreach (FileSystemEntryInfo entry in listing.Entries)
            {
                if (entry.IsReparsePoint)
                {
                    GuardResult linkResult = DeleteReparsePointVerified(entry.FullPath);
                    if (!linkResult.IsSuccess)
                    {
                        errors.Add(linkResult.Message);
                    }
                }
                else if (entry.IsDirectory)
                {
                    stack.Push((entry.FullPath, depth + 1));
                }
                else
                {
                    GuardResult fileResult = DeleteVerified(entry.FullPath, null, enforceProtectedPaths);
                    if (!fileResult.IsSuccess)
                    {
                        errors.Add(fileResult.Message);
                    }
                }
            }
        }

        directories.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
        foreach ((string directory, _) in directories)
        {
            GuardResult result = DeleteVerified(directory, null, enforceProtectedPaths);
            if (!result.IsSuccess && result.Status != GuardStatus.NotFound)
            {
                errors.Add(result.Message);
            }
        }

        return errors.Count == 0
            ? GuardResult.Ok($"已物理抹除 {root}")
            : GuardResult.Fail(GuardStatus.DeleteFailed, string.Join("; ", errors.Take(5)));
    }

    public static GuardResult MoveVerified(
        string source,
        string destination,
        FileIdentity? expected = null,
        bool enforceProtectedPaths = true)
    {
        string normalizedSource = PathKey.Normalize(source);
        string normalizedDestination = PathKey.Normalize(destination);

        if (!OperatingSystem.IsWindows())
        {
            return GuardResult.Fail(GuardStatus.InvalidPath, "原子改名仅支持 Windows");
        }

        GuardResult opened = OpenVerified(
            normalizedSource,
            expected,
            out SafeFileHandle? handle,
            out FileIdentity identity,
            enforceProtectedPaths);

        if (!opened.IsSuccess)
        {
            return opened;
        }

        using (handle!)
        {
            string? sourceRoot = PathKey.RootOf(normalizedSource);
            string? destinationRoot = PathKey.RootOf(normalizedDestination);
            if (!string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                return GuardResult.Fail(
                    GuardStatus.InvalidPath,
                    "跨卷移动会退化为复制+删除，隔离区必须与源同卷");
            }

            if (!NativeMethods.MoveFileExW(
                    NativeMethods.ToExtendedPath(normalizedSource),
                    NativeMethods.ToExtendedPath(normalizedDestination),
                    NativeMethods.MOVEFILE_COPY_ALLOWED | NativeMethods.MOVEFILE_REPLACE_EXISTING))
            {
                int error = Marshal.GetLastWin32Error();
                return GuardResult.Fail(
                    MapMoveError(error),
                    $"同卷原子改名失败（Win32 {error}）：{normalizedSource}",
                    error);
            }

            GuardResult? postFailure = ReadIdentity(handle!, out FileIdentity after);
            if (postFailure is not null || !after.SameObject(identity))
            {
                TryRollback(normalizedDestination, normalizedSource);
                return GuardResult.Fail(
                    GuardStatus.PostMoveVerificationFailed,
                    "改名后句柄身份与预期不符，已回滚");
            }

            string? finalPath = GetFinalPath(handle!);
            if (finalPath is not null && !PathKey.AreEqual(finalPath, normalizedDestination))
            {
                TryRollback(normalizedDestination, normalizedSource);
                return GuardResult.Fail(
                    GuardStatus.PostMoveVerificationFailed,
                    $"改名后最终路径异常（{finalPath}），已回滚");
            }

            return GuardResult.Ok();
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(NativeMethods.ToExtendedPath(path)) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryRollback(string from, string to)
    {
        try
        {
            NativeMethods.MoveFileExW(
                NativeMethods.ToExtendedPath(from),
                NativeMethods.ToExtendedPath(to),
                NativeMethods.MOVEFILE_COPY_ALLOWED | NativeMethods.MOVEFILE_REPLACE_EXISTING);
        }
        catch (Exception)
        {
            // Best effort; the caller already refuses the operation.
        }
    }

    private static bool TryOpen(string path, out SafeFileHandle? handle, out GuardResult? failure)
    {
        handle = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(path) || PathKey.HasParentTraversal(path))
        {
            failure = GuardResult.Fail(GuardStatus.InvalidPath, $"非法路径：{path}");
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            failure = GuardResult.Fail(GuardStatus.InvalidPath, "句柄级安全操作仅支持 Windows");
            return false;
        }

        string normalized = PathKey.Normalize(path);
        const uint shareMode = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE;
        const uint openFlags = NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT;

        int lastError = 0;
        handle = NativeMethods.CreateFileW(
            NativeMethods.ToExtendedPath(normalized),
            NativeMethods.DELETE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.FILE_WRITE_ATTRIBUTES | NativeMethods.SYNCHRONIZE,
            shareMode,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            openFlags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            lastError = Marshal.GetLastWin32Error();
            handle.Dispose();
            handle = null;

            // Some ACLs grant DELETE but not WRITE_ATTRIBUTES: retry with the
            // minimal right set before reporting a hard denial.
            if (lastError == NativeMethods.ERROR_ACCESS_DENIED)
            {
                handle = NativeMethods.CreateFileW(
                    NativeMethods.ToExtendedPath(normalized),
                    NativeMethods.DELETE | NativeMethods.FILE_READ_ATTRIBUTES | NativeMethods.SYNCHRONIZE,
                    shareMode,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    openFlags,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    lastError = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    handle = null;
                }
            }
        }

        if (handle is null)
        {
            failure = lastError switch
            {
                NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND =>
                    GuardResult.Fail(GuardStatus.NotFound, $"目标不存在：{normalized}", lastError),
                NativeMethods.ERROR_ACCESS_DENIED =>
                    GuardResult.Fail(GuardStatus.AccessDenied, $"拒绝访问（权限不足）：{normalized}", lastError),
                NativeMethods.ERROR_SHARING_VIOLATION or NativeMethods.ERROR_LOCK_VIOLATION =>
                    GuardResult.Fail(GuardStatus.LockedByAnotherProcess, $"目标被其他进程独占锁定：{normalized}", lastError),
                _ => GuardResult.Fail(GuardStatus.InvalidPath, $"无法打开目标（Win32 {lastError}）：{normalized}", lastError),
            };
            return false;
        }

        return true;
    }

    private static GuardResult? ReadIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;

        if (!NativeMethods.TryGetFileId(handle, out NativeMethods.FILE_ID_INFO id))
        {
            int error = Marshal.GetLastWin32Error();
            return GuardResult.Fail(GuardStatus.InvalidPath, $"无法读取 FileId（Win32 {error}）", error);
        }

        if (!NativeMethods.TryGetStandardInfo(handle, out NativeMethods.FILE_STANDARD_INFO standard))
        {
            int error = Marshal.GetLastWin32Error();
            return GuardResult.Fail(GuardStatus.InvalidPath, $"无法读取文件标准信息（Win32 {error}）", error);
        }

        bool isReparse = NativeMethods.TryGetAttributeTagInfo(handle, out NativeMethods.FILE_ATTRIBUTE_TAG_INFO tag) &&
                         (tag.FileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

        identity = new FileIdentity(
            id.VolumeSerialNumber,
            id.FileIdLow,
            id.FileIdHigh,
            standard.AllocationSize,
            standard.EndOfFile,
            standard.Directory != 0,
            isReparse);

        return null;
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        uint written = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (written == 0 || written >= buffer.Capacity)
        {
            return null;
        }

        return NativeMethods.StripExtendedPrefix(buffer.ToString());
    }

    private static GuardStatus MapDeleteError(int error) => error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED => GuardStatus.AccessDenied,
        NativeMethods.ERROR_SHARING_VIOLATION or NativeMethods.ERROR_LOCK_VIOLATION => GuardStatus.LockedByAnotherProcess,
        NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND => GuardStatus.NotFound,
        _ => GuardStatus.DeleteFailed,
    };

    private static GuardStatus MapMoveError(int error) => error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED => GuardStatus.AccessDenied,
        NativeMethods.ERROR_SHARING_VIOLATION or NativeMethods.ERROR_LOCK_VIOLATION => GuardStatus.LockedByAnotherProcess,
        NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_PATH_NOT_FOUND => GuardStatus.NotFound,
        _ => GuardStatus.MoveFailed,
    };
}
