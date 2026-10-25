using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TidyC.Core.Interop;
using TidyC.Core.Sys;

namespace TidyC.Core.Scan;

public sealed class ScanOptions
{
    public int MaxDepth { get; init; } = int.MaxValue;

    public int TopFileCount { get; init; } = 200;

    public int TopFolderCount { get; init; } = 100;

    public int MaxParallelism { get; init; } = Math.Max(2, Environment.ProcessorCount);

    public bool PreferMft { get; init; } = true;

    public bool IncludeReparseNodes { get; init; } = true;
}

public interface IScanEngine
{
    string EngineType { get; }

    Task<ScanResult> ScanAsync(
        string root,
        ScanOptions options,
        CancellationToken ct = default,
        IProgress<ScanProgressSnapshot>? progress = null);
}

/// <summary>Raised when the raw $MFT reader cannot run (no admin, non-NTFS, locked volume).</summary>
public sealed class MftUnavailableException : IOException
{
    public MftUnavailableException(string message, int win32Error = 0)
        : base(message)
    {
        Win32Error = win32Error;
    }

    public int Win32Error { get; }
}

/// <summary>
/// Administrator-mode engine: sequential read of the raw $MFT after
/// <c>FSCTL_GET_NTFS_VOLUME_DATA</c>. One pass over the metadata stream yields
/// every file, its parent FRN, timestamps and real allocated size — seconds for
/// a whole volume instead of minutes of directory walking.
/// </summary>
public sealed class MftScanner : IScanEngine
{
    public const ulong RootDirectoryFileReference = 5;

    public string EngineType => ScanEngines.MftStreamReader;

    /// <summary>Diagnostics for the most recent scan (used by --mft-probe).</summary>
    public static string LastSource { get; private set; } = string.Empty;

    public static uint LastRecordSize { get; private set; }

    public static long LastRejectedRecords { get; private set; }

    public static double LastReadSeconds { get; private set; }

    public static double LastBuildSeconds { get; private set; }

    public static int LastMftOpenError { get; private set; }

    public static bool LastBackupPrivilege { get; private set; }

    public static IReadOnlyDictionary<MftRecordStatus, long> LastRejectedByStatus { get; private set; } =
        new Dictionary<MftRecordStatus, long>();

    public static bool IsAvailable =>
        OperatingSystem.IsWindows() &&
        ElevationHelper.IsElevated;

    /// <summary>Cheap availability probe that also verifies NTFS.</summary>
    public static bool IsAvailableFor(string root) =>
        IsAvailable &&
        OperatingSystem.IsWindows() &&
        ElevationHelper.GetVolumeFacts(root).IsNtfs;

    public Task<ScanResult> ScanAsync(
        string root,
        ScanOptions options,
        CancellationToken ct = default,
        IProgress<ScanProgressSnapshot>? progress = null) =>
        Task.Run(() => Scan(root, options, ct, progress), ct);

    public ScanResult Scan(
        string root,
        ScanOptions options,
        CancellationToken ct = default,
        IProgress<ScanProgressSnapshot>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new ScanProgressSnapshot("打开 NTFS 卷句柄", 0, 0, 0, 0, 0));

        (Stream stream, uint recordSize, long byteLength, VolumeFacts volume) = OpenMftStream(root);
        var builder = new ScanResultBuilder(root, EngineType, volume, options);
        long recordsRead = 0;

        try
        {
            var entries = new List<MftFileEntry>(capacity: 262_144);
            progress?.Report(new ScanProgressSnapshot("顺序直读 $MFT", 0, 0, 0, 0, 0));
            long rejected = 0;
            var readStopwatch = Stopwatch.StartNew();
            var rejectedByStatus = new Dictionary<MftRecordStatus, long>();
            recordsRead = ReadRecordsParallel(
                stream,
                byteLength,
                recordSize,
                volume.ClusterSizeBytes,
                Math.Clamp(Environment.ProcessorCount, 2, 8),
                entries,
                ct,
                read => progress?.Report(new ScanProgressSnapshot(
                    "并行解析 $MFT",
                    read,
                    0,
                    0,
                    0,
                    stopwatch.Elapsed.TotalSeconds)),
                status =>
                {
                    Interlocked.Increment(ref rejected);
                    lock (rejectedByStatus)
                    {
                        rejectedByStatus[status] = rejectedByStatus.GetValueOrDefault(status) + 1;
                    }
                });
            readStopwatch.Stop();
            LastReadSeconds = readStopwatch.Elapsed.TotalSeconds;
            LastRejectedRecords = rejected;
            LastRejectedByStatus = rejectedByStatus;

            progress?.Report(new ScanProgressSnapshot(
                "聚合目录树与硬链接",
                recordsRead,
                0,
                0,
                0,
                stopwatch.Elapsed.TotalSeconds));
            var buildStopwatch = Stopwatch.StartNew();
            MftScanBuilder.Feed(entries, builder);
            buildStopwatch.Stop();
            LastBuildSeconds = buildStopwatch.Elapsed.TotalSeconds;
        }
        finally
        {
            stream.Dispose();
        }

        stopwatch.Stop();
        progress?.Report(new ScanProgressSnapshot(
            "完成",
            recordsRead,
            builder.Files,
            builder.Directories,
            builder.AttributedBytes,
            stopwatch.Elapsed.TotalSeconds));
        return builder.Build(stopwatch.Elapsed.TotalSeconds, recordsRead);
    }

    /// <summary>
    /// Parallel chunk pipeline: one reader streams 4 MB chunks into a small
    /// bounded pool while several workers parse them in place. Records are
    /// independent, so this cuts parse time by the core count without holding
    /// the whole metadata stream in memory.
    /// </summary>
    public static long ReadRecordsParallel(
        Stream stream,
        long byteLength,
        uint recordSize,
        uint clusterSize,
        int workerCount,
        List<MftFileEntry> entries,
        CancellationToken ct,
        Action<long>? onProgress = null,
        Action<MftRecordStatus>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);
        if (recordSize < MftRecordParser.RecordHeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(recordSize), "文件记录尺寸异常");
        }

        long totalRecords = byteLength / recordSize;
        if (totalRecords <= 0)
        {
            return 0;
        }

        int recordsPerChunk = Math.Max(1, (int)(4 * 1024 * 1024 / recordSize));
        int chunkBytes = recordsPerChunk * (int)recordSize;
        int workers = Math.Clamp(workerCount, 1, 16);
        long recordsRead = 0;

        using var chunks = new BlockingCollection<MftChunk>(workers + 2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task producer = Task.Run(
            () =>
            {
                try
                {
                    long index = 0;
                    while (index < totalRecords)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        int count = (int)Math.Min(recordsPerChunk, totalRecords - index);
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkBytes);
                        int expected = count * (int)recordSize;
                        int filled = 0;

                        while (filled < expected)
                        {
                            int read = stream.Read(buffer, filled, expected - filled);
                            if (read <= 0)
                            {
                                break;
                            }

                            filled += read;
                        }

                        if (filled < (int)recordSize)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            break;
                        }

                        int usable = filled / (int)recordSize;
                        chunks.Add(new MftChunk(buffer, usable, index, (int)recordSize), cts.Token);
                        index += usable;
                        long done = Interlocked.Add(ref recordsRead, usable);
                        onProgress?.Invoke(done);

                        if (usable < count)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    chunks.CompleteAdding();
                }
            },
            CancellationToken.None);

        var consumers = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            consumers[w] = Task.Run(
                () =>
                {
                    var local = new List<MftFileEntry>(8192);
                    try
                    {
                        foreach (MftChunk chunk in chunks.GetConsumingEnumerable(cts.Token))
                        {
                            ct.ThrowIfCancellationRequested();
                            for (int i = 0; i < chunk.Count; i++)
                            {
                                Span<byte> record = chunk.Buffer.AsSpan(i * chunk.RecordSize, chunk.RecordSize);
                                MftRecordStatus status = MftRecordParser.TryParseInPlace(
                                    record,
                                    chunk.FirstIndex + i,
                                    clusterSize,
                                    out MftFileEntry? entry,
                                    out _);

                                if (status == MftRecordStatus.Success && entry is not null)
                                {
                                    local.Add(entry);
                                }
                                else
                                {
                                    onRejected?.Invoke(status);
                                }
                            }

                            ArrayPool<byte>.Shared.Return(chunk.Buffer);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        lock (entries)
                        {
                            entries.AddRange(local);
                        }
                    }
                },
                CancellationToken.None);
        }

        try
        {
            Task.WaitAll(consumers);
            producer.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
        }

        ct.ThrowIfCancellationRequested();
        return Interlocked.Read(ref recordsRead);
    }

    private sealed record MftChunk(byte[] Buffer, int Count, long FirstIndex, int RecordSize);

    /// <summary>
    /// Reads <paramref name="byteLength"/> bytes of $MFT in large chunks and
    /// hands every successfully validated record to <paramref name="onEntry"/>.
    /// Corrupt / deleted / alias-only records are skipped and counted, never
    /// thrown: one bad record must not abort a volume scan.
    /// </summary>
    public static long ReadRecords(
        Stream stream,
        long byteLength,
        uint recordSize,
        uint clusterSize,
        Action<MftFileEntry> onEntry,
        CancellationToken ct = default,
        Action<long>? onProgress = null,
        Action<MftRecordStatus>? onRejected = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onEntry);
        if (recordSize < MftRecordParser.RecordHeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(recordSize), "文件记录尺寸异常");
        }

        long totalRecords = byteLength / recordSize;
        if (totalRecords <= 0)
        {
            return 0;
        }

        int recordsPerChunk = Math.Max(1, (int)(4 * 1024 * 1024 / recordSize));
        int chunkBytes = recordsPerChunk * (int)recordSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkBytes);

        long recordsRead = 0;
        try
        {
            while (recordsRead < totalRecords)
            {
                ct.ThrowIfCancellationRequested();

                int count = (int)Math.Min(recordsPerChunk, totalRecords - recordsRead);
                int expected = count * (int)recordSize;
                int filled = 0;
                while (filled < expected)
                {
                    int read = stream.Read(buffer, filled, expected - filled);
                    if (read <= 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled < (int)recordSize)
                {
                    break;
                }

                int usable = filled / (int)recordSize;
                for (int i = 0; i < usable; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    long index = recordsRead + i;
                    Span<byte> record = buffer.AsSpan(i * (int)recordSize, (int)recordSize);
                    MftRecordStatus status = MftRecordParser.TryParseInPlace(
                        record,
                        index,
                        clusterSize,
                        out MftFileEntry? entry,
                        out _);

                    if (status == MftRecordStatus.Success && entry is not null)
                    {
                        onEntry(entry);
                    }
                    else
                    {
                        onRejected?.Invoke(status);
                    }
                }

                recordsRead += usable;
                onProgress?.Invoke(recordsRead);

                if (usable < count)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return recordsRead;
    }

    /// <summary>
    /// Reads MFT record 0 from the raw volume and walks its $DATA run list,
    /// producing one extent per fragment so the whole MFT can be streamed even
    /// though it is not contiguous on disk.
    /// </summary>
    private static bool TryReadMftExtents(
        SafeFileHandle volumeHandle,
        NativeMethods.NTFS_VOLUME_DATA_BUFFER data,
        uint recordSize,
        out List<MftExtent> extents,
        out long totalBytes)
    {
        extents = new List<MftExtent>();
        totalBytes = 0;

        if (data.MftStartLcn <= 0 || data.BytesPerCluster == 0 || recordSize < MftRecordParser.RecordHeaderSize)
        {
            return false;
        }

        long startByte = data.MftStartLcn * (long)data.BytesPerCluster;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)recordSize);
        try
        {
            int read = RandomAccess.Read(volumeHandle, buffer.AsSpan(0, (int)recordSize), startByte);
            if (read < recordSize)
            {
                return false;
            }

            if (!MftRecordParser.TryGetDataRuns(buffer.AsSpan(0, (int)recordSize), out IReadOnlyList<(long StartLcn, long ClusterCount)> runs))
            {
                return false;
            }

            long volumeBytes = data.TotalClusters * (long)data.BytesPerCluster;
            foreach ((long startLcn, long clusterCount) in runs)
            {
                long offset = startLcn * (long)data.BytesPerCluster;
                long length = clusterCount * (long)data.BytesPerCluster;
                if (startLcn <= 0 || clusterCount <= 0 || offset + length > volumeBytes)
                {
                    return false;
                }

                extents.Add(new MftExtent(offset, length));
                totalBytes += length;
            }

            return extents.Count > 0 && totalBytes > 0;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (Stream Stream, uint RecordSize, long ByteLength, VolumeFacts Volume) OpenMftStream(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new MftUnavailableException("MFT 直读仅支持 Windows / NTFS");
        }

        string normalized = PathKey.Normalize(root);
        VolumeFacts volume = ElevationHelper.GetVolumeFacts(normalized);
        if (!volume.IsNtfs)
        {
            throw new MftUnavailableException($"卷 {normalized} 不是 NTFS，无法直读 $MFT");
        }

        // $MFT's ACL only grants SYSTEM read access; backup semantics plus the
        // backup privilege is what lets an elevated administrator open it.
        LastBackupPrivilege = ElevationHelper.TryEnablePrivilege("SeBackupPrivilege");
        ElevationHelper.TryEnablePrivilege("SeManageVolumePrivilege");

        string volumePath = @"\\.\" + normalized.TrimEnd('\\');
        SafeFileHandle volumeHandle = NativeMethods.CreateFileW(
            volumePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            volumeHandle.Dispose();
            throw new MftUnavailableException(
                error == NativeMethods.ERROR_ACCESS_DENIED
                    ? "需要管理员权限才能直读 $MFT（请以 UAC 提权重启）"
                    : $"无法打开卷句柄（Win32 错误 {error}）",
                error);
        }

        try
        {
            if (!NativeMethods.TryGetNtfsVolumeData(volumeHandle, out NativeMethods.NTFS_VOLUME_DATA_BUFFER data))
            {
                throw new MftUnavailableException("FSCTL_GET_NTFS_VOLUME_DATA 失败，卷不是 NTFS", Marshal.GetLastWin32Error());
            }

            uint recordSize = data.BytesPerFileRecordSegment;
            if (recordSize < MftRecordParser.RecordHeaderSize)
            {
                recordSize = 1024;
            }

            long byteLength = data.MftValidDataLength > 0
                ? data.MftValidDataLength
                : data.TotalClusters * data.BytesPerCluster;

            string drive = normalized.TrimEnd('\\');
            int lastError = 0;
            string[] mftPaths =
            {
                @"\\.\" + drive + @"\$MFT",
                @"\\?\" + drive + @"\$MFT",
            };

            foreach (string mftPath in mftPaths)
            {
                SafeFileHandle mftHandle = NativeMethods.CreateFileW(
                    mftPath,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN | NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
                    IntPtr.Zero);

                if (!mftHandle.IsInvalid)
                {
                    volumeHandle.Dispose();
                    volume.ClusterSizeBytes = data.BytesPerCluster;
                    LastSource = "$MFT 文件";
                    LastRecordSize = recordSize;
                    var stream = new FileStream(mftHandle, FileAccess.Read, bufferSize: 1 << 20, isAsync: false);
                    return (stream, recordSize, byteLength, volume);
                }

                lastError = Marshal.GetLastWin32Error();
                LastMftOpenError = lastError;
                mftHandle.Dispose();
            }

            // Fallback 1: follow the $MFT data runs from record 0 and stream every
            // extent. A multi-gigabyte MFT is almost always fragmented, so a
            // single contiguous read would only see the first extent.
            if (TryReadMftExtents(volumeHandle, data, recordSize, out List<MftExtent> extents, out long extentBytes))
            {
                long length = Math.Min(byteLength, extentBytes);
                var extentStream = new MftExtentStream(volumeHandle, extents, length);
                volume.ClusterSizeBytes = data.BytesPerCluster;
                LastSource = $"裸卷 data runs（{extents.Count} 段）";
                LastRecordSize = recordSize;
                return (extentStream, recordSize, length, volume);
            }

            // Fallback 2: contiguous read at MftStartLcn (works for small,
            // unfragmented MFTs).
            long offset = data.MftStartLcn * (long)data.BytesPerCluster;
            long volumeBytes = data.TotalClusters * (long)data.BytesPerCluster;
            if (data.MftStartLcn > 0 && byteLength > 0 && offset + byteLength <= volumeBytes)
            {
                var raw = new FileStream(volumeHandle, FileAccess.Read, bufferSize: 1 << 20, isAsync: false);
                raw.Position = offset;
                volume.ClusterSizeBytes = data.BytesPerCluster;
                LastSource = $"裸卷连续段 (LCN {data.MftStartLcn})";
                LastRecordSize = recordSize;
                return (raw, recordSize, byteLength, volume);
            }

            throw new MftUnavailableException(
                $"无法打开 $MFT（Win32 {lastError}），且裸卷回退不可用",
                lastError);
        }
        catch
        {
            volumeHandle.Dispose();
            throw;
        }
    }
}

/// <summary>Turns parsed $MFT records into a directory-only space tree plus reconciliations.</summary>
public static class MftScanBuilder
{
    /// <summary>
    /// NTFS metadata files. Their sparse streams (notably <c>$BadClus</c>, which
    /// reports ~100 GB allocated) are not user data and must never enter the
    /// attributed totals or cleanup lists.
    /// </summary>
    private static readonly HashSet<string> NtfsMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot",
        "$BadClus", "$Secure", "$UpCase", "$Extend", "$Quota", "$ObjId", "$Reparse",
        "$UsnJrnl", "$Deleted", "$TxfLog",
    };

    public static void Feed(IReadOnlyList<MftFileEntry> entries, ScanResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(builder);

        var directoryEntries = new Dictionary<ulong, MftFileEntry>(capacity: 65_536);
        var directoryNodes = new Dictionary<ulong, SpaceNode>(capacity: 65_536);
        long metadataBytes = 0;
        int metadataRecords = 0;

        // Pass 1: index directories and materialise one node per directory.
        foreach (MftFileEntry entry in entries)
        {
            if (!entry.IsDirectory || entry.HasReparsePoint || entry.PrimaryLink is null)
            {
                continue;
            }

            if (NtfsMetadataNames.Contains(entry.Name))
            {
                metadataBytes += entry.AllocatedBytes;
                metadataRecords++;
                continue;
            }

            directoryEntries[entry.FileReferenceNumber] = entry;

            if (entry.FileReferenceNumber == MftScanner.RootDirectoryFileReference)
            {
                SpaceNode root = builder.Root;
                root.Name = string.IsNullOrEmpty(entry.Name) ? "$MFT_ROOT" : entry.Name;
                root.LastWriteUtc = entry.LastWriteUtc;
                root.DirectAllocatedBytes = entry.AllocatedBytes;
                directoryNodes[entry.FileReferenceNumber] = root;
                continue;
            }

            directoryNodes[entry.FileReferenceNumber] = new SpaceNode
            {
                Name = string.IsNullOrEmpty(entry.Name) ? "<unnamed>" : entry.Name,
                Kind = NodeKind.Directory,
                FileId = entry.FileIdHex,
                LastWriteUtc = entry.LastWriteUtc,
                DirectAllocatedBytes = entry.AllocatedBytes,
            };
        }

        SpaceNode rootNode = builder.Root;
        if (!directoryNodes.ContainsKey(MftScanner.RootDirectoryFileReference))
        {
            directoryNodes[MftScanner.RootDirectoryFileReference] = rootNode;
            rootNode.Kind = NodeKind.Volume;
        }

        // Pass 2: link every directory to its parent; orphans hang off the root
        // so no allocated byte disappears from the reconciliation.
        int orphans = 0;
        foreach (KeyValuePair<ulong, SpaceNode> pair in directoryNodes)
        {
            if (pair.Key == MftScanner.RootDirectoryFileReference)
            {
                continue;
            }

            MftFileEntry entry = directoryEntries[pair.Key];
            if (directoryNodes.TryGetValue(entry.PrimaryLink!.ParentFileReferenceNumber, out SpaceNode? parent) &&
                !ReferenceEquals(parent, pair.Value))
            {
                parent.AddChild(pair.Value);
            }
            else
            {
                orphans++;
                rootNode.AddChild(pair.Value);
            }
        }

        // Pass 3: resolve full paths breadth-first, guarded against cycles left
        // behind by a corrupted parent chain.
        var pending = new Stack<SpaceNode>();
        var visited = new HashSet<SpaceNode>(ReferenceEqualityComparer.Instance) { rootNode };
        rootNode.FullPath = builder.RootPath;
        pending.Push(rootNode);
        while (pending.Count > 0)
        {
            SpaceNode node = pending.Pop();
            foreach (SpaceNode child in node.Children)
            {
                if (!visited.Add(child))
                {
                    continue;
                }

                if (child.FullPath.Length == 0)
                {
                    child.FullPath = PathKey.Combine(node.FullPath, child.Name);
                }

                pending.Push(child);
            }
        }

        // Pass 4: files, hard links and reparse points.
        var seenFileFrns = new HashSet<ulong>();
        var categoryByParent = new Dictionary<SpaceNode, string>(ReferenceEqualityComparer.Instance);
        foreach (MftFileEntry entry in entries)
        {
            if (entry.IsDirectory || entry.PrimaryLink is null)
            {
                continue;
            }

            if (NtfsMetadataNames.Contains(entry.Name))
            {
                metadataBytes += entry.AllocatedBytes;
                metadataRecords++;
                continue;
            }

            SpaceNode? parent = directoryNodes.TryGetValue(entry.PrimaryLink.ParentFileReferenceNumber, out SpaceNode? node)
                ? node
                : rootNode;

            if (entry.HasReparsePoint)
            {
                builder.ReparsePointsSkipped++;
                if (builder.Options.IncludeReparseNodes)
                {
                    parent.AddChild(new SpaceNode
                    {
                        Name = entry.Name,
                        FullPath = PathKey.Combine(parent.FullPath, entry.Name),
                        Kind = NodeKind.ReparsePoint,
                        LastWriteUtc = entry.LastWriteUtc,
                    });
                }

                continue;
            }

            if (!seenFileFrns.Add(entry.FileReferenceNumber))
            {
                continue;
            }

            long allocated = entry.AllocatedBytes;
            parent.AddDirectFile(allocated, entry.LogicalBytes, entry.LastWriteUtc);
            builder.Files++;
            builder.AttributedBytes += allocated;

            // Category is a property of the parent directory, so it is computed
            // once per parent instead of once per file.
            if (!categoryByParent.TryGetValue(parent, out string? category))
            {
                category = TreeOps.CategoryOfPath(parent.FullPath, builder.DriveRoot);
                categoryByParent[parent] = category;
            }

            builder.Spectrum.Add(category, allocated);

            int extraLinks = Math.Max(0, entry.HardLinkCount - 1);
            if (extraLinks > 0)
            {
                builder.HardLinkDeduplicated += extraLinks;
                builder.HardLinkSavedBytes += allocated * extraLinks;
            }

            if (builder.TopFiles.WouldAccept(allocated))
            {
                builder.TopFiles.Add(
                    PathKey.Combine(parent.FullPath, entry.Name),
                    allocated,
                    entry.LogicalBytes,
                    entry.LastWriteUtc,
                    entry.FileIdHex);
            }
        }

        builder.Directories = Math.Max(0, directoryNodes.Count - 1);
        if (metadataRecords > 0)
        {
            builder.Warnings.Add(
                $"已排除 {metadataRecords:N0} 条 NTFS 元数据记录（$MFT / $BadClus / $Extend 等，共 {ByteSize.Format(metadataBytes)}），不计入用户归因");
        }

        if (orphans > 0)
        {
            builder.Warnings.Add($"{orphans} 条目录记录的父 FRN 已失效，已挂接到根节点以保证字节对账");
        }
    }
}
