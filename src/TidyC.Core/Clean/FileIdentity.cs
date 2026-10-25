namespace TidyC.Core.Clean;

/// <summary>
/// Immutable fingerprint of a filesystem object captured while scanning. It is
/// the value that must still match when the engine later re-opens the path: a
/// swap to a different file (new FileId) or a junction is rejected.
/// </summary>
public readonly record struct FileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    long AllocatedBytes,
    long LogicalBytes,
    bool IsDirectory,
    bool IsReparsePoint)
{
    public string FileIdHex => $"0x{FileIdLow:X16}";

    public bool SameObject(in FileIdentity other) =>
        VolumeSerialNumber == other.VolumeSerialNumber &&
        FileIdLow == other.FileIdLow &&
        FileIdHigh == other.FileIdHigh;

    public bool SameObjectAndSize(in FileIdentity other) =>
        SameObject(other) && LogicalBytes == other.LogicalBytes;

    public override string ToString() =>
        $"{FileIdHex} (vol 0x{VolumeSerialNumber:X}, {LogicalBytes} B, dir={IsDirectory}, reparse={IsReparsePoint})";
}

public enum GuardStatus
{
    Ok = 0,
    NotFound = 1,
    AccessDenied = 2,
    SharingViolation = 3,
    ReparsePointRejected = 4,
    IdentityMismatch = 5,
    SizeMismatch = 6,
    ProtectedPathRejected = 7,
    InvalidPath = 8,
    MoveFailed = 9,
    DeleteFailed = 10,
    PostMoveVerificationFailed = 11,
    LockedByAnotherProcess = 12,
}

public sealed class GuardResult
{
    private GuardResult(GuardStatus status, string message, int win32Error)
    {
        Status = status;
        Message = message;
        Win32Error = win32Error;
    }

    public GuardStatus Status { get; }

    public string Message { get; }

    public int Win32Error { get; }

    public bool IsSuccess => Status == GuardStatus.Ok;

    public static GuardResult Ok(string message = "OK") => new(GuardStatus.Ok, message, 0);

    public static GuardResult Fail(GuardStatus status, string message, int win32Error = 0) =>
        new(status, message, win32Error);

    public override string ToString() => $"{Status}: {Message}";
}
