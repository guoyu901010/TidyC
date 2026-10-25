using System.Collections.Concurrent;
using System.Diagnostics;
using TidyC.Core.Sys;

namespace TidyC.Core.Scan;

/// <summary>
/// Non-elevated engine: a work-stealing directory queue across all cores. Every
/// worker enumerates one directory at a time (fast NTFS directory queries), and
/// the queue spreads subtrees evenly instead of pinning one thread per top-level
/// folder. Reparse points are never followed; denied directories are counted.
/// </summary>
public sealed class DirectoryTreeWalker : IScanEngine
{
    private readonly IFileSystemProbe _probe;

    public DirectoryTreeWalker(IFileSystemProbe? probe = null) => _probe = probe ?? new FileSystemProbe();

    public string EngineType => ScanEngines.DirectoryTreeWalker;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);

        var stopwatch = Stopwatch.StartNew();
        VolumeFacts volume = ElevationHelper.GetVolumeFacts(root);
        if (volume.ClusterSizeBytes == 0)
        {
            volume.ClusterSizeBytes = 4096;
        }

        var builder = new ScanResultBuilder(root, EngineType, volume, options);
        var counters = new WalkCounters();
        uint cluster = volume.ClusterSizeBytes;
        string driveRoot = builder.DriveRoot;

        progress?.Report(new ScanProgressSnapshot("枚举根目录", 0, 0, 0, 0, 0));

        DirectoryListing rootListing = _probe.List(builder.RootPath);
        if (rootListing.AccessDenied)
        {
            Interlocked.Increment(ref counters.Denied);
            builder.Root.AccessDeniedCount++;
        }

        var pending = new CountdownEvent(1);
        var queue = new ConcurrentQueue<(SpaceNode Node, int Depth)>();
        string rootCategory = TreeOps.CategoryOfPath(builder.RootPath, driveRoot);

        foreach (FileSystemEntryInfo entry in rootListing.Entries)
        {
            if (entry.IsReparsePoint)
            {
                Interlocked.Increment(ref counters.Reparse);
                builder.Root.ReparseSkippedCount++;
                AddReparseNode(builder, builder.Root, entry);
                continue;
            }

            if (entry.IsDirectory)
            {
                Interlocked.Increment(ref counters.Directories);
                var node = new SpaceNode
                {
                    Name = entry.Name,
                    FullPath = entry.FullPath,
                    Kind = NodeKind.Directory,
                    LastWriteUtc = entry.LastWriteUtc,
                };
                builder.Root.AddChild(node);

                if (options.MaxDepth > 1)
                {
                    pending.AddCount();
                    queue.Enqueue((node, 1));
                }
                else
                {
                    Interlocked.Increment(ref counters.Truncated);
                }

                continue;
            }

            AddFile(builder, builder.Root, entry, cluster, counters, rootCategory);
        }

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Thread? reporter = null;
        if (progress is not null)
        {
            // A dedicated thread (not the saturated thread pool) guarantees the
            // UI keeps receiving counters while workers are blocked on I/O.
            reporter = new Thread(() =>
            {
                while (!progressCts.IsCancellationRequested)
                {
                    progress.Report(new ScanProgressSnapshot(
                        "目录遍历",
                        0,
                        Interlocked.Read(ref counters.Files),
                        Interlocked.Read(ref counters.Directories),
                        Interlocked.Read(ref counters.Allocated),
                        stopwatch.Elapsed.TotalSeconds));

                    try
                    {
                        progressCts.Token.WaitHandle.WaitOne(250);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "TidyC scan progress",
            };
            reporter.Start();
        }

        int workerCount = Math.Max(2, options.MaxParallelism);
        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(
                () => Worker(queue, pending, builder, options, cluster, driveRoot, counters, ct),
                ct);
        }

        pending.Signal();
        try
        {
            pending.Wait(ct);
            Task.WaitAll(workers);
        }
        finally
        {
            progressCts.Cancel();
            reporter?.Join(1000);
        }

        builder.Files = counters.Files;
        builder.Directories = counters.Directories;
        builder.AttributedBytes = counters.Allocated;
        builder.AccessDenied = counters.Denied;
        builder.ReparsePointsSkipped = counters.Reparse;
        builder.Warnings.Add("目录遍历引擎不做 FRN 硬链接去重（统计口径为路径去重）");

        if (counters.Denied > 0)
        {
            builder.Warnings.Add($"{counters.Denied} 个目录拒绝访问（ACL 保护），其字节计入权限盲区");
        }

        if (counters.Truncated > 0)
        {
            builder.Warnings.Add($"{counters.Truncated} 个目录因深度限制未展开");
        }

        stopwatch.Stop();
        ScanResult result = builder.Build(stopwatch.Elapsed.TotalSeconds, counters.Files + counters.Directories);
        progress?.Report(new ScanProgressSnapshot(
            "完成",
            0,
            counters.Files,
            counters.Directories,
            counters.Allocated,
            stopwatch.Elapsed.TotalSeconds));
        return result;
    }

    private void Worker(
        ConcurrentQueue<(SpaceNode Node, int Depth)> queue,
        CountdownEvent pending,
        ScanResultBuilder builder,
        ScanOptions options,
        uint cluster,
        string driveRoot,
        WalkCounters counters,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (queue.TryDequeue(out (SpaceNode Node, int Depth) item))
            {
                try
                {
                    ProcessDirectory(item.Node, item.Depth, builder, options, cluster, driveRoot, counters, pending, queue, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref counters.Denied);
                    item.Node.AccessDeniedCount++;
                }
                finally
                {
                    pending.Signal();
                }

                continue;
            }

            if (pending.CurrentCount <= 0)
            {
                return;
            }

            // Work may still arrive from another worker; yield briefly.
            Thread.Sleep(1);
        }
    }

    private void ProcessDirectory(
        SpaceNode node,
        int depth,
        ScanResultBuilder builder,
        ScanOptions options,
        uint cluster,
        string driveRoot,
        WalkCounters counters,
        CountdownEvent pending,
        ConcurrentQueue<(SpaceNode Node, int Depth)> queue,
        CancellationToken ct)
    {
        DirectoryListing listing = _probe.List(node.FullPath);
        if (listing.AccessDenied)
        {
            Interlocked.Increment(ref counters.Denied);
            node.AccessDeniedCount++;
        }

        // Classify once per directory instead of once per file (millions of files
        // would otherwise pay a full path split each).
        string category = TreeOps.CategoryOfPath(node.FullPath, driveRoot);

        foreach (FileSystemEntryInfo entry in listing.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsReparsePoint)
            {
                Interlocked.Increment(ref counters.Reparse);
                node.ReparseSkippedCount++;
                AddReparseNode(builder, node, entry);
                continue;
            }

            if (entry.IsDirectory)
            {
                Interlocked.Increment(ref counters.Directories);
                var child = new SpaceNode
                {
                    Name = entry.Name,
                    FullPath = entry.FullPath,
                    Kind = NodeKind.Directory,
                    LastWriteUtc = entry.LastWriteUtc,
                };
                node.AddChild(child);

                if (depth + 1 < options.MaxDepth)
                {
                    pending.AddCount();
                    queue.Enqueue((child, depth + 1));
                }
                else
                {
                    Interlocked.Increment(ref counters.Truncated);
                }

                continue;
            }

            AddFile(builder, node, entry, cluster, counters, category);
        }
    }

    private static void AddFile(
        ScanResultBuilder builder,
        SpaceNode parent,
        FileSystemEntryInfo entry,
        uint cluster,
        WalkCounters counters,
        string category)
    {
        long allocated = ((entry.SizeBytes + cluster - 1) / cluster) * cluster;
        if (entry.SizeBytes <= 0)
        {
            allocated = 0;
        }

        parent.AddDirectFile(allocated, entry.SizeBytes, entry.LastWriteUtc);

        Interlocked.Increment(ref counters.Files);
        Interlocked.Add(ref counters.Allocated, allocated);
        builder.Spectrum.Add(category, allocated);

        if (builder.TopFiles.WouldAccept(allocated))
        {
            builder.TopFiles.Add(entry.FullPath, allocated, entry.SizeBytes, entry.LastWriteUtc);
        }
    }

    private static void AddReparseNode(ScanResultBuilder builder, SpaceNode parent, FileSystemEntryInfo entry)
    {
        if (!builder.Options.IncludeReparseNodes)
        {
            return;
        }

        parent.AddChild(new SpaceNode
        {
            Name = entry.Name,
            FullPath = entry.FullPath,
            Kind = NodeKind.ReparsePoint,
            LastWriteUtc = entry.LastWriteUtc,
        });
    }

    private sealed class WalkCounters
    {
        public long Files;
        public long Directories;
        public long Allocated;
        public long Denied;
        public long Reparse;
        public long Truncated;
    }
}
