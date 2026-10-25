using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TidyC.Core.Interop;

/// <summary>
/// Raw Win32 surface used by the scan / clean engine. Everything in this file is
/// deliberately minimal: only the calls TidyC actually needs, with the exact
/// native layouts (BOOLEAN is one byte, BOOL is four) to avoid silent corruption
/// when reading back structures written by the kernel.
/// </summary>
internal static class NativeMethods
{
    // ---- Access rights -----------------------------------------------------
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint DELETE = 0x00010000;
    internal const uint FILE_READ_ATTRIBUTES = 0x00000080;
    internal const uint FILE_WRITE_ATTRIBUTES = 0x00000100;
    internal const uint SYNCHRONIZE = 0x00100000;

    // ---- Share modes -------------------------------------------------------
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;

    // ---- Creation dispositions --------------------------------------------
    internal const uint OPEN_EXISTING = 3;

    // ---- Flags -------------------------------------------------------------
    internal const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
    internal const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
    internal const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    // ---- MoveFileEx --------------------------------------------------------
    internal const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;
    internal const uint MOVEFILE_COPY_ALLOWED = 0x00000002;
    internal const uint MOVEFILE_WRITE_THROUGH = 0x00000008;

    // ---- FILE_DISPOSITION_INFO_EX -----------------------------------------
    internal const uint FILE_DISPOSITION_FLAG_DELETE = 0x00000001;
    internal const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x00000002;
    internal const uint FILE_DISPOSITION_FLAG_FORCE_IMAGE_SECTION_CHECK = 0x00000004;

    // ---- Errors ------------------------------------------------------------
    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_SHARING_VIOLATION = 32;
    internal const int ERROR_LOCK_VIOLATION = 33;
    internal const int ERROR_NOT_SUPPORTED = 50;
    internal const int ERROR_INVALID_PARAMETER = 87;
    internal const int ERROR_DISK_FULL = 112;
    internal const int ERROR_ALREADY_EXISTS = 183;

    internal const uint FSCTL_GET_NTFS_VOLUME_DATA =
        (9u << 16) | (0u << 14) | (25u << 2) | 0u; // 0x00090064

    internal const uint FIND_FIRST_EX_LARGE_FETCH = 0x00000002;

    internal enum FILE_INFO_BY_HANDLE_CLASS
    {
        FileBasicInfo = 0,
        FileStandardInfo = 1,
        FileNameInfo = 2,
        FileRenameInfo = 3,
        FileDispositionInfo = 4,
        FileAllocationInfo = 5,
        FileEndOfFileInfo = 6,
        FileStreamInfo = 7,
        FileCompressionInfo = 8,
        FileAttributeTagInfo = 9,
        FileIdBothDirectoryInfo = 10,
        FileIdBothDirectoryRestartInfo = 11,
        FileIoPriorityHintInfo = 12,
        FileRemoteProtocolInfo = 13,
        FileFullDirectoryInfo = 14,
        FileFullDirectoryRestartInfo = 15,
        FileStorageInfo = 16,
        FileAlignmentInfo = 17,
        FileIdInfo = 18,
        FileIdExtdDirectoryInfo = 19,
        FileIdExtdDirectoryRestartInfo = 20,
        FileDispositionInfoEx = 21,
        FileRenameInfoEx = 22,
    }

    internal enum FINDEX_INFO_LEVELS
    {
        FindExInfoStandard = 0,
        FindExInfoBasic = 1,
    }

    internal enum FINDEX_SEARCH_OPS
    {
        FindExSearchNameMatch = 0,
        FindExSearchLimitToDirectories = 1,
        FindExSearchLimitToDevices = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_DISPOSITION_INFO
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NTFS_VOLUME_DATA_BUFFER
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    // ---- kernel32 ----------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        FILE_INFO_BY_HANDLE_CLASS fileInformationClass,
        IntPtr lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        FILE_INFO_BY_HANDLE_CLASS fileInformationClass,
        IntPtr lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveFileExW(
        string lpExistingFileName,
        string lpNewFileName,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindFirstFileExW(
        string lpFileName,
        FINDEX_INFO_LEVELS fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData,
        FINDEX_SEARCH_OPS fSearchOp,
        IntPtr lpSearchFilter,
        uint dwAdditionalFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    // ---- shell32 -----------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    internal const uint SHERB_NOCONFIRMATION = 0x00000001;
    internal const uint SHERB_NOPROGRESSUI = 0x00000002;
    internal const uint SHERB_NOSOUND = 0x00000004;

    // ---- managed helpers ---------------------------------------------------

    internal static unsafe bool TryGetFileId(SafeFileHandle handle, out FILE_ID_INFO info)
    {
        FILE_ID_INFO local = default;
        bool ok = GetFileInformationByHandleEx(
            handle,
            FILE_INFO_BY_HANDLE_CLASS.FileIdInfo,
            (IntPtr)(&local),
            (uint)sizeof(FILE_ID_INFO));
        info = local;
        return ok;
    }

    internal static unsafe bool TryGetStandardInfo(SafeFileHandle handle, out FILE_STANDARD_INFO info)
    {
        FILE_STANDARD_INFO local = default;
        bool ok = GetFileInformationByHandleEx(
            handle,
            FILE_INFO_BY_HANDLE_CLASS.FileStandardInfo,
            (IntPtr)(&local),
            (uint)sizeof(FILE_STANDARD_INFO));
        info = local;
        return ok;
    }

    internal static unsafe bool TryGetAttributeTagInfo(SafeFileHandle handle, out FILE_ATTRIBUTE_TAG_INFO info)
    {
        FILE_ATTRIBUTE_TAG_INFO local = default;
        bool ok = GetFileInformationByHandleEx(
            handle,
            FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo,
            (IntPtr)(&local),
            (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO));
        info = local;
        return ok;
    }

    internal static unsafe bool SetDispositionDelete(SafeFileHandle handle)
    {
        var info = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS,
        };

        if (SetFileInformationByHandle(
                handle,
                FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx,
                (IntPtr)(&info),
                (uint)sizeof(FILE_DISPOSITION_INFO_EX)))
        {
            return true;
        }

        // Older / non-NTFS volumes: classic disposition (delete on last close).
        var classic = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
        return SetFileInformationByHandle(
            handle,
            FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo,
            (IntPtr)(&classic),
            (uint)sizeof(FILE_DISPOSITION_INFO));
    }

    internal static unsafe bool ClearReadOnly(SafeFileHandle handle, uint currentAttributes)
    {
        if ((currentAttributes & FILE_ATTRIBUTE_READONLY) == 0)
        {
            return true;
        }

        var info = new FILE_BASIC_INFO
        {
            FileAttributes = currentAttributes & ~FILE_ATTRIBUTE_READONLY,
        };

        return SetFileInformationByHandle(
            handle,
            FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo,
            (IntPtr)(&info),
            (uint)sizeof(FILE_BASIC_INFO));
    }

    internal static bool TryGetNtfsVolumeData(SafeFileHandle volume, out NTFS_VOLUME_DATA_BUFFER data)
    {
        int size = Marshal.SizeOf<NTFS_VOLUME_DATA_BUFFER>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = DeviceIoControl(
                volume,
                FSCTL_GET_NTFS_VOLUME_DATA,
                IntPtr.Zero,
                0,
                buffer,
                (uint)size,
                out _,
                IntPtr.Zero);
            data = ok ? Marshal.PtrToStructure<NTFS_VOLUME_DATA_BUFFER>(buffer) : default;
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Long-path safe form: prepends <c>\\?\</c> for paths that need it.</summary>
    internal static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path.Substring(2);
        }

        return @"\\?\" + path;
    }

    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path.Substring(8);
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path.Substring(4);
        }

        return path;
    }
}
