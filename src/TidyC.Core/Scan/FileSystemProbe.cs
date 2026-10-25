using System.Runtime.InteropServices;
using TidyC.Core.Interop;
using TidyC.Core.Sys;

namespace TidyC.Core.Scan;

public sealed class FileSystemEntryInfo
{
    public string Name { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    public bool IsReparsePoint { get; init; }

    public long SizeBytes { get; init; }

    public DateTime LastWriteUtc { get; init; }
}

public sealed class DirectoryListing
{
    public List<FileSystemEntryInfo> Entries { get; } = new();

    public bool AccessDenied { get; init; }

    public int Win32Error { get; init; }
}

public sealed record PathMeasure(
    long AllocatedBytes,
    long LogicalBytes,
    long FileCount,
    long DirectoryCount,
    long ReparseSkipped,
    long AccessDenied,
    DateTime LastWriteUtc);

/// <summary>
/// Test seam around the filesystem. Production uses <see cref="FileSystemProbe"/>
/// which never follows reparse points; tests can substitute the same object with
/// a temp root and cluster size.
/// </summary>
public interface IFileSystemProbe
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    DirectoryListing List(string directory);

    PathMeasure Measure(string path, int maxDepth = int.MaxValue, CancellationToken ct = default);

    long RoundUpToCluster(long bytes);

    DateTime GetLastWriteUtc(string path);
}

public sealed class FileSystemProbe : IFileSystemProbe
{
    public FileSystemProbe(uint clusterSizeBytes = 4096)
    {
        ClusterSizeBytes = Math.Max(1u, clusterSizeBytes);
    }

    public uint ClusterSizeBytes { get; }

    public static FileSystemProbe Default { get; } = new();

    public bool FileExists(string path) => File.Exists(Extended(path));

    public bool DirectoryExists(string path) => Directory.Exists(Extended(path));

    public long RoundUpToCluster(long bytes)
    {
        if (bytes <= 0)
        {
            return 0;
        }

        long cluster = ClusterSizeBytes;
        return ((bytes + cluster - 1) / cluster) * cluster;
    }

    public DateTime GetLastWriteUtc(string path)
    {
        try
        {
            return File.Exists(Extended(path))
                ? File.GetLastWriteTimeUtc(Extended(path))
                : Directory.GetLastWriteTimeUtc(Extended(path));
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    public DirectoryListing List(string directory)
    {
        string path = PathKey.Normalize(directory);
        var listing = new DirectoryListing();

        try
        {
            var options = new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
                BufferSize = 64 * 1024,
            };

            var entries = new System.IO.Enumeration.FileSystemEnumerable<FileSystemEntryInfo>(
                path,
                (ref System.IO.Enumeration.FileSystemEntry entry) =>
                {
                    string name = entry.FileName.ToString();
                    return new FileSystemEntryInfo
                    {
                        Name = name,
                        FullPath = PathKey.Combine(path, name),
                        IsDirectory = entry.IsDirectory,
                        IsReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                        SizeBytes = entry.IsDirectory ? 0 : entry.Length,
                        LastWriteUtc = entry.LastWriteTimeUtc.UtcDateTime,
                    };
                },
                options);

            foreach (FileSystemEntryInfo entry in entries)
            {
                listing.Entries.Add(entry);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new DirectoryListing { AccessDenied = true, Win32Error = NativeMethods.ERROR_ACCESS_DENIED };
        }
        catch (DirectoryNotFoundException)
        {
            return new DirectoryListing { Win32Error = NativeMethods.ERROR_PATH_NOT_FOUND };
        }
        catch (IOException)
        {
            return new DirectoryListing { Win32Error = NativeMethods.ERROR_ACCESS_DENIED };
        }

        return listing;
    }

    public PathMeasure Measure(string path, int maxDepth = int.MaxValue, CancellationToken ct = default)
    {
        string extended = Extended(path);

        if (File.Exists(extended) && !IsDirectory(extended))
        {
            long length = new FileInfo(extended).Length;
            return new PathMeasure(RoundUpToCluster(length), length, 1, 0, 0, 0, GetLastWriteUtc(path));
        }

        if (!Directory.Exists(extended))
        {
            return new PathMeasure(0, 0, 0, 0, 0, 0, DateTime.MinValue);
        }

        long allocated = 0;
        long logical = 0;
        long files = 0;
        long directories = 0;
        long reparseSkipped = 0;
        long denied = 0;
        DateTime newest = DateTime.MinValue;
        int visited = 0;

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((path, 0));

        while (stack.Count > 0)
        {
            if ((++visited & 0x3F) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            (string current, int depth) = stack.Pop();
            DirectoryListing listing = List(current);
            if (listing.AccessDenied)
            {
                denied++;
            }

            foreach (FileSystemEntryInfo entry in listing.Entries)
            {
                if (entry.IsReparsePoint)
                {
                    reparseSkipped++;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    directories++;
                    if (depth + 1 < maxDepth)
                    {
                        stack.Push((entry.FullPath, depth + 1));
                    }

                    continue;
                }

                files++;
                logical += entry.SizeBytes;
                allocated += RoundUpToCluster(entry.SizeBytes);
                if (entry.LastWriteUtc > newest)
                {
                    newest = entry.LastWriteUtc;
                }
            }
        }

        return new PathMeasure(allocated, logical, files, directories, reparseSkipped, denied, newest);
    }

    private static bool IsDirectory(string path) => (File.GetAttributes(path) & FileAttributes.Directory) != 0;

    private static string Extended(string path) => NativeMethods.ToExtendedPath(PathKey.Normalize(path));

    private static FileSystemEntryInfo? ToEntry(string parent, ref NativeMethods.WIN32_FIND_DATA data)
    {
        string name = data.cFileName;
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            return null;
        }

        bool isDirectory = (data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;
        bool isReparse = (data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

        DateTime lastWrite = DateTime.MinValue;
        try
        {
            if (data.ftLastWriteTime > 0)
            {
                lastWrite = DateTime.FromFileTimeUtc(data.ftLastWriteTime);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return new FileSystemEntryInfo
        {
            Name = name,
            FullPath = PathKey.Combine(parent, name),
            IsDirectory = isDirectory,
            IsReparsePoint = isReparse,
            SizeBytes = isDirectory ? 0 : size,
            LastWriteUtc = lastWrite,
        };
    }
}
