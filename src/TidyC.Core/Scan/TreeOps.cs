using TidyC.Core.Sys;

namespace TidyC.Core.Scan;

/// <summary>Classification of one path into the six spectrum buckets.</summary>
public static class TreeOps
{
    internal static readonly Dictionary<string, int> CategoryIndex = new(StringComparer.Ordinal)
    {
        [StorageCategories.WindowsSystem] = 0,
        [StorageCategories.AppData] = 1,
        [StorageCategories.UserProfiles] = 2,
        [StorageCategories.ProgramFiles] = 3,
        [StorageCategories.ProtectedSystem] = 4,
        [StorageCategories.Quarantine] = 5,
        [StorageCategories.Other] = 6,
    };

    public static string CategoryOfPath(string path, string driveRoot = @"C:\")
    {
        string normalized = PathKey.Normalize(path);
        string root = PathKey.WithTrailingSeparator(driveRoot);
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return StorageCategories.Other;
        }

        string[] segments = normalized.Substring(root.Length)
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return StorageCategories.Other;
        }

        string first = segments[0];
        if (first.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("Windows.old", StringComparison.OrdinalIgnoreCase))
        {
            return StorageCategories.WindowsSystem;
        }

        if (first.Equals("TidyC_Quarantine", StringComparison.OrdinalIgnoreCase))
        {
            return StorageCategories.Quarantine;
        }

        if (first.Equals("Program Files", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase))
        {
            return StorageCategories.ProgramFiles;
        }

        if (first.Equals("Users", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length >= 3 && segments[2].Equals("AppData", StringComparison.OrdinalIgnoreCase))
            {
                return StorageCategories.AppData;
            }

            return StorageCategories.UserProfiles;
        }

        if (first.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("PerfLogs", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("Boot", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("EFI", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase))
        {
            return StorageCategories.ProtectedSystem;
        }

        return StorageCategories.Other;
    }

    public static VolumeReconciliation Reconcile(VolumeFacts volume, long attributedBytes, long cleanableBytes)
    {
        long used = Math.Max(0, volume.UsedBytes);
        long attributed = Math.Max(0, attributedBytes);
        return new VolumeReconciliation
        {
            TotalVolumeBytes = volume.TotalBytes > 0 ? volume.TotalBytes : 512L * 1024 * 1024 * 1024,
            NtfsPhysicalUsedBytes = used,
            EngineAttributedBytes = attributed,
            PermissionBlindspotBytes = Math.Max(0, used - attributed),
            WhitelistSafeCleanableBytes = Math.Max(0, cleanableBytes),
        };
    }

    public static List<FolderFact> TopDirectories(SpaceNode root, int count)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (count <= 0)
        {
            return new List<FolderFact>();
        }

        return root.Descendants()
            .Where(node => node.Kind is NodeKind.Directory or NodeKind.Volume)
            .OrderByDescending(node => node.AllocatedBytes)
            .Take(count)
            .Select(node => new FolderFact
            {
                Path = node.FullPath,
                AllocatedBytes = node.AllocatedBytes,
                FileCount = node.FileCount,
                DirectoryCount = node.DirectoryCount,
                Category = node.Category,
            })
            .ToList();
    }

    public static List<FileFact> TopFilesFromTree(SpaceNode root, int count)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (count <= 0)
        {
            return new List<FileFact>();
        }

        return root.Descendants()
            .Where(node => node.Kind == NodeKind.File)
            .OrderByDescending(node => node.AllocatedBytes)
            .Take(count)
            .Select(node => new FileFact
            {
                Path = node.FullPath,
                AllocatedBytes = node.AllocatedBytes,
                LogicalBytes = node.LogicalBytes,
                LastWriteUtc = node.LastWriteUtc,
                FileId = node.FileId,
                Category = node.Category,
            })
            .ToList();
    }

    /// <summary>Assigns the per-node category by path (used after tree construction).</summary>
    public static void ApplyCategories(SpaceNode root, string driveRoot)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (SpaceNode node in root.SelfAndDescendants())
        {
            node.Category = CategoryOfPath(node.FullPath, driveRoot);
        }
    }

    public static long SumFileBytes(SpaceNode root) =>
        root.SelfAndDescendants().Where(n => n.Kind == NodeKind.File).Sum(n => n.AllocatedBytes);
}

/// <summary>
/// Thread-safe spectrum accumulator. Files are classified in the scan hot loop;
/// storing one array keeps the per-file cost to a few interlocked adds.
/// </summary>
public sealed class SpectrumAccumulator
{
    private readonly long[] _bytes = new long[7];

    public void Add(string category, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        int index = CategoryIndexOf(category);
        Interlocked.Add(ref _bytes[index], bytes);
    }

    public void AddForPath(string path, long bytes, string driveRoot = @"C:\") =>
        Add(TreeOps.CategoryOfPath(path, driveRoot), bytes);

    public StorageSpectrum ToSpectrum() => new()
    {
        WindowsSystemBytes = Interlocked.Read(ref _bytes[0]),
        AppDataBytes = Interlocked.Read(ref _bytes[1]),
        UserProfilesBytes = Interlocked.Read(ref _bytes[2]),
        ProgramFilesBytes = Interlocked.Read(ref _bytes[3]),
        ProtectedSystemBytes = Interlocked.Read(ref _bytes[4]),
        QuarantineBytes = Interlocked.Read(ref _bytes[5]),
        OtherBytes = Interlocked.Read(ref _bytes[6]),
    };

    private static int CategoryIndexOf(string category) =>
        TreeOps.CategoryIndex.TryGetValue(category, out int index) ? index : 6;
}

/// <summary>Bounded, thread-safe top-N collector (min-eviction).</summary>
public sealed class TopNCollector
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly List<FileFact> _items;
    private long _threshold = long.MinValue;

    public TopNCollector(int capacity)
    {
        _capacity = Math.Max(0, capacity);
        _items = new List<FileFact>(Math.Min(capacity, 512));
    }

    public int Capacity => _capacity;

    /// <summary>
    /// Cheap rejection test used in the per-file hot loop: callers skip building
    /// the path and filing a record unless the size can actually enter the top N.
    /// </summary>
    public bool WouldAccept(long allocatedBytes) =>
        _capacity > 0 && allocatedBytes > 0 && allocatedBytes > Volatile.Read(ref _threshold);

    public void Add(string path, long allocatedBytes, long logicalBytes, DateTime lastWriteUtc, string fileId = "")
    {
        if (!WouldAccept(allocatedBytes))
        {
            return;
        }

        lock (_gate)
        {
            if (_items.Count < _capacity)
            {
                _items.Add(new FileFact
                {
                    Path = path,
                    AllocatedBytes = allocatedBytes,
                    LogicalBytes = logicalBytes,
                    LastWriteUtc = lastWriteUtc,
                    FileId = fileId,
                    Category = TreeOps.CategoryOfPath(path),
                });

                if (_items.Count == _capacity)
                {
                    Volatile.Write(ref _threshold, _items.Min(f => f.AllocatedBytes));
                }

                return;
            }

            int minIndex = 0;
            for (int i = 1; i < _items.Count; i++)
            {
                if (_items[i].AllocatedBytes < _items[minIndex].AllocatedBytes)
                {
                    minIndex = i;
                }
            }

            if (allocatedBytes <= _items[minIndex].AllocatedBytes)
            {
                return;
            }

            _items[minIndex] = new FileFact
            {
                Path = path,
                AllocatedBytes = allocatedBytes,
                LogicalBytes = logicalBytes,
                LastWriteUtc = lastWriteUtc,
                FileId = fileId,
                Category = TreeOps.CategoryOfPath(path),
            };

            Volatile.Write(ref _threshold, _items.Min(f => f.AllocatedBytes));
        }
    }

    public List<FileFact> Snapshot()
    {
        lock (_gate)
        {
            return _items.OrderByDescending(f => f.AllocatedBytes).ToList();
        }
    }
}
