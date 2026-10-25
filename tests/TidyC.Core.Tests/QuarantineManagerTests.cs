using TidyC.Core.Clean;
using TidyC.Core.Sys;
using TidyC.Core.Tests.TestSupport;

namespace TidyC.Core.Tests;

public sealed class QuarantineManagerTests
{
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static QuarantineConfiguration Config(string root) => new()
    {
        Root = root,
        Retention = TimeSpan.FromDays(14),
        EnforceWhitelist = false,
        EnforceProtectedPaths = false,
        HashSizeLimitBytes = 8 * 1024 * 1024,
    };

    [Fact]
    public async Task Move_UsesAtomicRename_AndStaysFast()
    {
        using var tree = new TempTree("quar-move");
        string file = tree.File("cache.bin", 512 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "测试批次");

        Assert.Equal(1, batch.ItemCount);
        Assert.True(batch.TotalBytes >= 512 * 1024);
        Assert.False(File.Exists(file));
        Assert.True(batch.MoveDurationSeconds < 0.1, $"搬移耗时 {batch.MoveDurationSeconds}s 超过 0.1s");
        QuarantineRecord record = Assert.Single(batch.Records);
        Assert.True(File.Exists(record.QuarantinePath));
        Assert.StartsWith("0x", record.FileIndexHex);
        Assert.Equal(64, record.Sha256Hash.Length);
    }

    [Fact]
    public async Task Move_WritesJournalHashChain()
    {
        using var tree = new TempTree("quar-journal");
        string file = tree.File("logged.bin", 64 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        await manager.MoveToQuarantineAsync(new[] { file }, "日志测试");

        string journal = ReadShared(manager.MoveJournalPath);
        Assert.Contains("ATOMIC_MOVE_START", journal);
        Assert.Contains("ATOMIC_MOVE_COMMITTED", journal);
        JournalVerification verification = AppendOnlyJournal.VerifyFile(manager.MoveJournalPath);
        Assert.True(verification.IsValid, verification.ToString());
    }

    [Fact]
    public async Task Move_SkipsLockedFileWithoutThrowing()
    {
        using var tree = new TempTree("quar-lock");
        string file = tree.File("locked.bin", 32 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "锁定测试");

        Assert.Equal(0, batch.ItemCount);
        Assert.NotEmpty(batch.Skipped);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Move_RejectsProtectedSource()
    {
        using var tree = new TempTree("quar-protected");
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { @"C:\Windows" }, "禁清区测试");

        Assert.Equal(0, batch.ItemCount);
        Assert.Contains(batch.Skipped, s => s.Contains("禁清区", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_ReturnsFileToOriginalPath()
    {
        using var tree = new TempTree("quar-restore");
        string file = tree.File("restore.bin", 128 * 1024);
        byte[] original = File.ReadAllBytes(file);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "还原测试");
        QuarantineRestoreResult result = await manager.RestoreBatchAsync(batch.BatchId);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.RestoredCount);
        Assert.True(File.Exists(file));
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.True(Assert.Single(batch.Records).IsRestored);
        Assert.Equal("RESTORED", manager.LoadBatch(batch.BatchId)!.Status);
    }

    [Fact]
    public async Task Restore_RenamesConflictingLiveFileAside()
    {
        using var tree = new TempTree("quar-conflict");
        string file = tree.File("conflict.bin", 16 * 1024, fill: 0x11);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "冲突测试");
        File.WriteAllText(file, "NEWER LIVE FILE");

        QuarantineRestoreResult result = await manager.RestoreBatchAsync(batch.BatchId);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(16 * 1024, new FileInfo(file).Length);
        QuarantineRecord record = Assert.Single(batch.Records);
        Assert.NotNull(record.ConflictPath);
        Assert.Contains("_restore_conflict_", record.ConflictPath);
        Assert.Equal("NEWER LIVE FILE", File.ReadAllText(record.ConflictPath!));
    }

    [Fact]
    public async Task Restore_RejectsTamperedPayload()
    {
        using var tree = new TempTree("quar-tamper");
        string file = tree.File("tamper.bin", 32 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "篡改测试");
        QuarantineRecord record = Assert.Single(batch.Records);
        File.AppendAllText(record.QuarantinePath, "tampered");

        QuarantineRestoreResult result = await manager.RestoreBatchAsync(batch.BatchId);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.RestoredCount);
        Assert.False(File.Exists(file));
        Assert.Contains(result.Errors, e => e.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Purge_DeletesPayloadAndReportsFreedBytes()
    {
        using var tree = new TempTree("quar-purge");
        string file = tree.File("purge.bin", 256 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "抹除测试");
        QuarantineRecord record = Assert.Single(batch.Records);

        QuarantinePurgeResult result = await manager.PurgeBatchAsync(batch.BatchId);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.PurgedCount);
        Assert.True(result.FreedBytes >= 256 * 1024);
        Assert.False(File.Exists(record.QuarantinePath));
        Assert.True(record.Purged);
        JournalVerification verification = AppendOnlyJournal.VerifyFile(manager.PurgeJournalPath);
        Assert.True(verification.IsValid, verification.ToString());
    }

    [Fact]
    public async Task PurgeExpired_OnlyTouchesExpiredBatches()
    {
        using var tree = new TempTree("quar-expiry");
        string root = tree.Dir("quarantine");
        DateTime now = new(2025, 5, 20, 10, 0, 0, DateTimeKind.Utc);
        using var manager = new QuarantineManager(Config(root), () => now);

        string oldFile = tree.File("old.bin", 32 * 1024);
        QuarantineBatch oldBatch = await manager.MoveToQuarantineAsync(new[] { oldFile }, "过期批次");

        now = now.AddDays(15);
        string freshFile = tree.File("fresh.bin", 32 * 1024);
        QuarantineBatch freshBatch = await manager.MoveToQuarantineAsync(new[] { freshFile }, "新鲜批次");

        IReadOnlyList<QuarantinePurgeResult> purged = await manager.PurgeExpiredAsync();

        QuarantinePurgeResult single = Assert.Single(purged);
        Assert.Equal(oldBatch.BatchId, single.BatchId);
        Assert.Equal("PURGED", manager.LoadBatch(oldBatch.BatchId)!.Status);
        Assert.Equal("ACTIVE", manager.LoadBatch(freshBatch.BatchId)!.Status);
        Assert.True(File.Exists(Assert.Single(freshBatch.Records).QuarantinePath));
    }

    [Fact]
    public async Task Statistics_KeepStagedAndPurgedHonest()
    {
        using var tree = new TempTree("quar-stats");
        string file = tree.File("stats.bin", 128 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "统计测试");
        QuarantineStatistics staged = manager.GetStatistics();
        Assert.Equal(1, staged.ActiveBatchCount);
        Assert.True(staged.StagedBytes >= 128 * 1024);
        Assert.Equal(0, staged.PurgedBytes);

        await manager.PurgeBatchAsync(batch.BatchId);
        QuarantineStatistics purged = manager.GetStatistics();
        Assert.Equal(0, purged.StagedBytes);
        Assert.True(purged.PurgedBytes >= 128 * 1024);
        Assert.Equal(0, purged.ActiveItemCount);
    }

    [Fact]
    public async Task LoadBatches_ReloadsManifestFromDisk()
    {
        using var tree = new TempTree("quar-reload");
        string file = tree.File("reload.bin", 16 * 1024);
        string root = tree.Dir("quarantine");
        string batchId;
        using (var manager = new QuarantineManager(Config(root)))
        {
            QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "重载测试");
            batchId = batch.BatchId;
        }

        using var reopened = new QuarantineManager(Config(root));
        QuarantineBatch? loaded = reopened.LoadBatch(batchId);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.ItemCount);
        Assert.True(loaded.TotalBytes > 0);
        Assert.Single(reopened.LoadBatches());
    }

    [Fact]
    public async Task Move_DirectoryTree_RoundTrips()
    {
        using var tree = new TempTree("quar-dir");
        string directory = tree.Dir("cache-tree");
        tree.File(@"cache-tree\a\one.bin", 64 * 1024);
        tree.File(@"cache-tree\b\two.bin", 64 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { directory }, "目录批次");

        Assert.Equal(1, batch.ItemCount);
        Assert.False(Directory.Exists(directory));
        QuarantineRecord record = Assert.Single(batch.Records);
        Assert.True(record.IsDirectory);
        Assert.True(Directory.Exists(record.QuarantinePath));

        QuarantineRestoreResult restore = await manager.RestoreBatchAsync(batch.BatchId);

        Assert.True(restore.IsSuccess, string.Join("; ", restore.Errors));
        Assert.True(File.Exists(tree.At(@"cache-tree\a\one.bin")));
        Assert.True(File.Exists(tree.At(@"cache-tree\b\two.bin")));
    }

    [Fact]
    public async Task Restore_CannotRunTwice()
    {
        using var tree = new TempTree("quar-twice");
        string file = tree.File("twice.bin", 8 * 1024);
        string root = tree.Dir("quarantine");
        using var manager = new QuarantineManager(Config(root));

        QuarantineBatch batch = await manager.MoveToQuarantineAsync(new[] { file }, "二次还原");
        await manager.RestoreBatchAsync(batch.BatchId);

        QuarantineRestoreResult second = await manager.RestoreBatchAsync(batch.BatchId);

        Assert.Equal(0, second.RestoredCount);
        Assert.Equal(1, second.SkippedCount);
        Assert.True(File.Exists(file));
    }
}
