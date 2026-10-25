using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TidyC.Core.Interop;
using TidyC.Core.Rules;
using TidyC.Core.Scan;
using TidyC.Core.Sys;

namespace TidyC.Core.Clean;

/// <summary>
/// Reversible cleaning. Nothing is deleted on clean: items are atomically renamed
/// into a same-volume batch folder in under a second, journaled before and after,
/// and only purged when the user asks or the 14 day retention expires.
/// </summary>
public sealed class QuarantineManager : IDisposable
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppendOnlyJournal _moveJournal;
    private readonly AppendOnlyJournal _restoreJournal;
    private readonly AppendOnlyJournal _purgeJournal;
    private readonly Func<DateTime> _clock;
    private readonly IFileSystemProbe _probe;
    private readonly object _batchGate = new();

    public QuarantineManager(
        QuarantineConfiguration? configuration = null,
        Func<DateTime>? clock = null,
        IFileSystemProbe? probe = null)
    {
        Configuration = configuration ?? new QuarantineConfiguration();
        Root = PathKey.Normalize(Configuration.Root);
        _clock = clock ?? (() => DateTime.UtcNow);
        _probe = probe ?? new FileSystemProbe();

        string logDirectory = Path.Combine(Root, "logs");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(logDirectory);

        _moveJournal = new AppendOnlyJournal(logDirectory, "journal-batch-move.jsonl");
        _restoreJournal = new AppendOnlyJournal(logDirectory, "journal-batch-restore.jsonl");
        _purgeJournal = new AppendOnlyJournal(logDirectory, "journal-batch-purge.jsonl");
    }

    public string Root { get; }

    public QuarantineConfiguration Configuration { get; }

    public string JournalDirectory => Path.Combine(Root, "logs");

    public string MoveJournalPath => _moveJournal.FilePath;

    public string RestoreJournalPath => _restoreJournal.FilePath;

    public string PurgeJournalPath => _purgeJournal.FilePath;

    // ------------------------------------------------------------------ move

    public Task<QuarantineBatch> MoveToQuarantineAsync(
        IEnumerable<string> sourcePaths,
        string description,
        CancellationToken ct = default) =>
        MoveToQuarantineAsync(
            (sourcePaths ?? throw new ArgumentNullException(nameof(sourcePaths)))
                .Select(p => new QuarantineRequest { Path = p }),
            description,
            ct);

    public Task<QuarantineBatch> MoveToQuarantineAsync(
        IEnumerable<QuarantineRequest> requests,
        string description,
        CancellationToken ct = default) =>
        Task.Run(() => MoveToQuarantine(requests, description, ct), ct);

    public QuarantineBatch MoveToQuarantine(
        IEnumerable<QuarantineRequest> requests,
        string description,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var stopwatch = Stopwatch.StartNew();
        DateTime now = _clock();
        var batch = new QuarantineBatch
        {
            BatchId = CreateBatchId(now),
            CreatedAt = now,
            ExpiresAt = now + Configuration.Retention,
            Description = description ?? string.Empty,
        };

        string batchFolder = BatchFolder(batch.BatchId);
        string payloadFolder = Path.Combine(batchFolder, "payload");
        Directory.CreateDirectory(payloadFolder);

        string txId = "TX_MOVE_" + batch.BatchId;
        var items = requests.ToList();
        _moveJournal.Append("ATOMIC_MOVE_BATCH_START", txId, new Dictionary<string, object?>
        {
            ["batch_id"] = batch.BatchId,
            ["requested_items"] = items.Count,
            ["retention_days"] = Configuration.Retention.TotalDays,
            ["description"] = batch.Description,
        });

        int index = 0;
        foreach (QuarantineRequest request in items)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            string source = PathKey.Normalize(request.Path);

            void Skip(string reason)
            {
                batch.Skipped.Add($"{source}: {reason}");
                _moveJournal.Append("ATOMIC_MOVE_SKIPPED", txId, new Dictionary<string, object?>
                {
                    ["source"] = source,
                    ["reason"] = reason,
                });
            }

            if (!Exists(source))
            {
                Skip("目标不存在");
                continue;
            }

            if (Configuration.EnforceProtectedPaths && RuleValidator.IsForbiddenCleanRoot(source))
            {
                Skip("位于硬编码禁清区（C:\\ / Windows / Program Files / 个人目录）");
                continue;
            }

            if (Configuration.EnforceWhitelist && !RuleValidator.IsInsideWhitelist(source))
            {
                Skip("不在白名单规则覆盖范围内（默认拒绝）");
                continue;
            }

            GuardResult capture = HandleGuard.Capture(source, out FileIdentity identity);
            if (!capture.IsSuccess)
            {
                Skip(capture.Message);
                continue;
            }

            if (request.ExpectedIdentity is { } expected && !identity.SameObject(expected))
            {
                Skip($"FileId 与扫描指纹不符（{expected.FileIdHex} != {identity.FileIdHex}），疑似 TOCTOU 替换");
                continue;
            }

            PathMeasure measure = _probe.Measure(source);
            long size = identity.IsDirectory ? measure.AllocatedBytes : identity.AllocatedBytes;
            if (size <= 0)
            {
                size = measure.AllocatedBytes;
            }

            string hash = string.Empty;
            if (!identity.IsDirectory && measure.LogicalBytes > 0 && measure.LogicalBytes <= Configuration.HashSizeLimitBytes)
            {
                try
                {
                    hash = AppendOnlyJournal.Sha256File(source);
                }
                catch (IOException)
                {
                    hash = string.Empty;
                }
            }

            string destination = UniquePayloadPath(payloadFolder, index, PathKey.Leaf(source));

            _moveJournal.Append("ATOMIC_MOVE_START", txId, new Dictionary<string, object?>
            {
                ["batch_id"] = batch.BatchId,
                ["source"] = source,
                ["quarantine_path"] = destination,
                ["file_id"] = identity.FileIdHex,
                ["size_bytes"] = size,
                ["is_directory"] = identity.IsDirectory,
                ["sha256"] = hash,
            });

            GuardResult move = HandleGuard.MoveVerified(
                source,
                destination,
                identity,
                enforceProtectedPaths: Configuration.EnforceProtectedPaths);
            if (!move.IsSuccess)
            {
                _moveJournal.Append("ATOMIC_MOVE_FAILED", txId, new Dictionary<string, object?>
                {
                    ["source"] = source,
                    ["status"] = move.Status.ToString(),
                    ["win32_error"] = move.Win32Error,
                    ["message"] = move.Message,
                });
                Skip(move.Message);
                continue;
            }

            batch.Records.Add(new QuarantineRecord
            {
                OriginalPath = source,
                QuarantinePath = destination,
                FileIndexHex = identity.FileIdHex,
                SizeBytes = size,
                Sha256Hash = hash,
                IsDirectory = identity.IsDirectory,
                MovedAt = _clock(),
            });

            batch.ItemCount = batch.Records.Count;
            batch.TotalBytes += size;

            _moveJournal.Append("ATOMIC_MOVE_COMMITTED", txId, new Dictionary<string, object?>
            {
                ["batch_id"] = batch.BatchId,
                ["file_id"] = identity.FileIdHex,
                ["source"] = source,
                ["quarantine_path"] = destination,
                ["size_bytes"] = size,
                ["status"] = "SUCCESS",
            });

            PersistBatch(batch, batchFolder);
        }

        stopwatch.Stop();
        batch.MoveDurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 4);
        batch.Status = batch.ItemCount > 0 ? "ACTIVE" : "EMPTY";
        PersistBatch(batch, batchFolder);

        _moveJournal.Append("ATOMIC_MOVE_BATCH_COMMITTED", txId, new Dictionary<string, object?>
        {
            ["batch_id"] = batch.BatchId,
            ["item_count"] = batch.ItemCount,
            ["total_bytes"] = batch.TotalBytes,
            ["skipped"] = batch.Skipped.Count,
            ["duration_seconds"] = batch.MoveDurationSeconds,
            ["status"] = batch.Status,
        });

        return batch;
    }

    // --------------------------------------------------------------- restore

    public Task<QuarantineRestoreResult> RestoreBatchAsync(string batchId, CancellationToken ct = default) =>
        Task.Run(() => RestoreBatch(batchId, ct), ct);

    public QuarantineRestoreResult RestoreBatch(string batchId, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new QuarantineRestoreResult { BatchId = batchId };
        QuarantineBatch? batch = LoadBatch(batchId);
        if (batch is null)
        {
            result.FailedCount = 1;
            result.Errors.Add($"批次不存在：{batchId}");
            return result;
        }

        string txId = "TX_RESTORE_" + SafeBatchToken(batchId);
        _restoreJournal.Append("ATOMIC_RESTORE_START", txId, new Dictionary<string, object?>
        {
            ["batch_id"] = batch.BatchId,
            ["item_count"] = batch.ItemCount,
            ["total_bytes"] = batch.TotalBytes,
        });

        foreach (QuarantineRecord record in batch.Records)
        {
            ct.ThrowIfCancellationRequested();

            void Fail(string reason)
            {
                record.LastError = reason;
                result.FailedCount++;
                result.Errors.Add($"{record.OriginalPath}: {reason}");
                _restoreJournal.Append("ATOMIC_RESTORE_FAILED", txId, new Dictionary<string, object?>
                {
                    ["file_id"] = record.FileIndexHex,
                    ["target_path"] = record.OriginalPath,
                    ["quarantine_path"] = record.QuarantinePath,
                    ["reason"] = reason,
                    ["status"] = "FAILED",
                });
            }

            if (record.IsRestored || record.Purged)
            {
                result.SkippedCount++;
                continue;
            }

            if (!PathKey.IsStrictlyUnder(record.QuarantinePath, Root))
            {
                Fail("隔离副本路径越界（清单可能被篡改），拒绝还原");
                continue;
            }

            if (!Exists(record.QuarantinePath))
            {
                Fail("隔离副本已不存在");
                continue;
            }

            if (Configuration.EnforceProtectedPaths && RuleValidator.IsForbiddenCleanRoot(record.OriginalPath))
            {
                Fail("原路径落入禁清区，拒绝还原");
                continue;
            }

            if (!string.IsNullOrEmpty(record.Sha256Hash))
            {
                string current;
                try
                {
                    current = AppendOnlyJournal.Sha256File(record.QuarantinePath);
                }
                catch (IOException ex)
                {
                    Fail("无法校验哈希：" + ex.Message);
                    continue;
                }

                if (!string.Equals(current, record.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    Fail("SHA-256 不匹配，隔离副本疑似被篡改，拒绝还原");
                    continue;
                }
            }

            FileIdentity? payloadIdentity = HandleGuard.TryCapture(record.QuarantinePath);
            if (payloadIdentity is null)
            {
                Fail("无法读取隔离副本的 FileId 指纹");
                continue;
            }

            if (!string.IsNullOrEmpty(record.FileIndexHex) &&
                !string.Equals(payloadIdentity.Value.FileIdHex, record.FileIndexHex, StringComparison.OrdinalIgnoreCase))
            {
                Fail($"FileId 指纹不符（记录 {record.FileIndexHex}，实际 {payloadIdentity.Value.FileIdHex}），隔离副本疑似被替换");
                continue;
            }

            // A newer live file may already occupy the original path: move it
            // aside with a timestamp suffix instead of overwriting user data.
            if (Exists(record.OriginalPath))
            {
                string conflictPath = BuildConflictPath(record.OriginalPath, _clock());
                GuardResult conflictMove = HandleGuard.MoveVerified(
                    record.OriginalPath,
                    conflictPath,
                    expected: null,
                    enforceProtectedPaths: false);

                if (!conflictMove.IsSuccess)
                {
                    Fail("同名冲突无法让位：" + conflictMove.Message);
                    continue;
                }

                record.ConflictPath = conflictPath;
                result.ConflictCount++;
                _restoreJournal.Append("RESTORE_CONFLICT_RENAMED", txId, new Dictionary<string, object?>
                {
                    ["original_path"] = record.OriginalPath,
                    ["conflict_path"] = conflictPath,
                });
            }

            try
            {
                string? parent = Path.GetDirectoryName(record.OriginalPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(NativeMethods.ToExtendedPath(parent)))
                {
                    Directory.CreateDirectory(parent);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail("无法创建原目录：" + ex.Message);
                continue;
            }

            GuardResult restore = HandleGuard.MoveVerified(
                record.QuarantinePath,
                record.OriginalPath,
                payloadIdentity,
                enforceProtectedPaths: false);

            if (!restore.IsSuccess)
            {
                Fail("原子还原失败：" + restore.Message);
                continue;
            }

            record.IsRestored = true;
            record.RestoredAt = _clock();
            record.LastError = null;
            result.RestoredCount++;

            _restoreJournal.Append("ATOMIC_RESTORE_COMMITTED", txId, new Dictionary<string, object?>
            {
                ["file_id"] = record.FileIndexHex,
                ["quarantine_path"] = record.QuarantinePath,
                ["target_path"] = record.OriginalPath,
                ["status"] = "SUCCESS",
                ["duration_us"] = (long)stopwatch.Elapsed.TotalMilliseconds * 1000,
                ["integrity_check"] = string.IsNullOrEmpty(record.Sha256Hash) ? "SIZE_ONLY" : "SHA256_MATCHED",
                ["rollback_guard"] = "ACTIVE",
            });
        }

        if (result.FailedCount == 0)
        {
            batch.Status = "RESTORED";
        }
        else if (result.RestoredCount > 0)
        {
            batch.Status = "PARTIAL";
        }

        PersistBatch(batch, BatchFolder(batch.BatchId));
        stopwatch.Stop();
        result.DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 4);

        _restoreJournal.Append("ATOMIC_RESTORE_BATCH_COMMITTED", txId, new Dictionary<string, object?>
        {
            ["batch_id"] = batch.BatchId,
            ["restored"] = result.RestoredCount,
            ["failed"] = result.FailedCount,
            ["conflicts"] = result.ConflictCount,
            ["integrity_check"] = "SHA256_MATCHED",
            ["status"] = result.IsSuccess ? "SUCCESS" : "PARTIAL",
        });

        return result;
    }

    // ----------------------------------------------------------------- purge

    public Task<QuarantinePurgeResult> PurgeBatchAsync(string batchId, CancellationToken ct = default) =>
        Task.Run(() => PurgeBatch(batchId, ct), ct);

    public QuarantinePurgeResult PurgeBatch(string batchId, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new QuarantinePurgeResult { BatchId = batchId };
        QuarantineBatch? batch = LoadBatch(batchId);
        if (batch is null)
        {
            result.Errors.Add($"批次不存在：{batchId}");
            return result;
        }

        string txId = "TX_PURGE_" + SafeBatchToken(batchId);
        _purgeJournal.Append("PHYSICAL_PURGE_START", txId, new Dictionary<string, object?>
        {
            ["batch_id"] = batch.BatchId,
            ["item_count"] = batch.ItemCount,
        });

        foreach (QuarantineRecord record in batch.Records)
        {
            ct.ThrowIfCancellationRequested();
            if (record.Purged || record.IsRestored)
            {
                continue;
            }

            if (!PathKey.IsStrictlyUnder(record.QuarantinePath, Root))
            {
                result.Errors.Add($"{record.OriginalPath}: 隔离路径越界，拒绝抹除");
                continue;
            }

            if (!Exists(record.QuarantinePath))
            {
                record.Purged = true;
                record.PurgedAt = _clock();
                result.PurgedCount++;
                continue;
            }

            GuardResult delete = record.IsDirectory
                ? HandleGuard.DeleteTreeVerified(record.QuarantinePath, enforceProtectedPaths: false)
                : HandleGuard.DeleteVerified(record.QuarantinePath, null, enforceProtectedPaths: false);

            if (!delete.IsSuccess)
            {
                result.Errors.Add($"{record.OriginalPath}: {delete.Message}");
                _purgeJournal.Append("PHYSICAL_PURGE_FAILED", txId, new Dictionary<string, object?>
                {
                    ["quarantine_path"] = record.QuarantinePath,
                    ["message"] = delete.Message,
                });
                continue;
            }

            record.Purged = true;
            record.PurgedAt = _clock();
            result.PurgedCount++;
            result.FreedBytes += record.SizeBytes;

            _purgeJournal.Append("PHYSICAL_PURGE_COMMITTED", txId, new Dictionary<string, object?>
            {
                ["quarantine_path"] = record.QuarantinePath,
                ["size_bytes"] = record.SizeBytes,
                ["status"] = "SUCCESS",
            });
        }

        batch.Status = "PURGED";
        batch.PurgedAt = _clock();
        PersistBatch(batch, BatchFolder(batch.BatchId));

        stopwatch.Stop();
        result.DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 4);

        _purgeJournal.Append("PHYSICAL_PURGE_BATCH_COMMITTED", txId, new Dictionary<string, object?>
        {
            ["batch_id"] = batch.BatchId,
            ["purged_count"] = result.PurgedCount,
            ["freed_bytes"] = result.FreedBytes,
            ["status"] = result.IsSuccess ? "SUCCESS" : "PARTIAL",
        });

        return result;
    }

    /// <summary>Expired batches are purged only when this is called explicitly — never silently.</summary>
    public async Task<IReadOnlyList<QuarantinePurgeResult>> PurgeExpiredAsync(CancellationToken ct = default)
    {
        DateTime now = _clock();
        var results = new List<QuarantinePurgeResult>();
        foreach (QuarantineBatch batch in LoadBatches())
        {
            ct.ThrowIfCancellationRequested();
            bool purgeable = batch.Status is "ACTIVE" or "PARTIAL" &&
                             batch.IsExpired(now) &&
                             batch.Records.Any(r => !r.IsRestored && !r.Purged);
            if (!purgeable)
            {
                continue;
            }

            results.Add(await PurgeBatchAsync(batch.BatchId, ct).ConfigureAwait(false));
        }

        return results;
    }

    // ------------------------------------------------------------------ read

    public QuarantineBatch? LoadBatch(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return null;
        }

        string path = Path.Combine(BatchFolder(batchId), "batch.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<QuarantineBatch>(json, ManifestJson);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<QuarantineBatch> LoadBatches()
    {
        if (!Directory.Exists(Root))
        {
            return Array.Empty<QuarantineBatch>();
        }

        var batches = new List<QuarantineBatch>();
        foreach (string directory in Directory.EnumerateDirectories(Root, "BATCH_*"))
        {
            string manifest = Path.Combine(directory, "batch.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(manifest);
                QuarantineBatch? batch = JsonSerializer.Deserialize<QuarantineBatch>(json, ManifestJson);
                if (batch is not null)
                {
                    batches.Add(batch);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A damaged manifest must never take down the quarantine view.
            }
        }

        return batches.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public QuarantineStatistics GetStatistics()
    {
        DateTime now = _clock();
        var statistics = new QuarantineStatistics { Root = Root };

        foreach (QuarantineBatch batch in LoadBatches())
        {
            statistics.TotalBatchCount++;

            bool active = batch.Status is "ACTIVE" or "PARTIAL";
            if (active)
            {
                statistics.ActiveBatchCount++;
                if (statistics.NextExpiryUtc is null || batch.ExpiresAt < statistics.NextExpiryUtc)
                {
                    statistics.NextExpiryUtc = batch.ExpiresAt;
                }

                if (batch.IsExpired(now))
                {
                    statistics.ExpiredBatchCount++;
                }
            }

            foreach (QuarantineRecord record in batch.Records)
            {
                if (record.Purged)
                {
                    statistics.PurgedBytes += record.SizeBytes;
                    continue;
                }

                if (record.IsRestored)
                {
                    continue;
                }

                if (Exists(record.QuarantinePath))
                {
                    statistics.StagedBytes += record.SizeBytes;
                    statistics.ActiveItemCount++;
                }
            }
        }

        return statistics;
    }

    public void Dispose()
    {
        _moveJournal.Dispose();
        _restoreJournal.Dispose();
        _purgeJournal.Dispose();
    }

    // ----------------------------------------------------------- internals

    private string BatchFolder(string batchId) => Path.Combine(Root, batchId);

    private void PersistBatch(QuarantineBatch batch, string batchFolder)
    {
        lock (_batchGate)
        {
            Directory.CreateDirectory(batchFolder);
            string target = Path.Combine(batchFolder, "batch.json");
            string temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(batch, ManifestJson));
            File.Move(temp, target, overwrite: true);
        }
    }

    private string CreateBatchId(DateTime now)
    {
        string baseId = $"BATCH_{now:yyyyMMdd_HHmmss}";
        string candidate = baseId;
        int suffix = 1;
        while (Directory.Exists(Path.Combine(Root, candidate)))
        {
            candidate = $"{baseId}_{suffix:D2}";
            suffix++;
        }

        return candidate;
    }

    private string UniquePayloadPath(string payloadFolder, int index, string leafName)
    {
        string safeLeaf = SanitizeLeaf(leafName);
        string candidate = Path.Combine(payloadFolder, $"{index:D4}_{safeLeaf}");
        int suffix = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(payloadFolder, $"{index:D4}_{suffix}_{safeLeaf}");
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeLeaf(string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return "item";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(leaf.Length);
        foreach (char c in leaf)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        string sanitized = builder.ToString().Trim().TrimEnd('.');
        if (sanitized.Length > 80)
        {
            sanitized = sanitized.Substring(0, 80);
        }

        return sanitized.Length == 0 ? "item" : sanitized;
    }

    private static string BuildConflictPath(string originalPath, DateTime now)
    {
        string directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(originalPath);
        string extension = Path.GetExtension(originalPath);
        string stamp = now.ToString("yyyyMMdd_HHmmss");
        string candidate = Path.Combine(directory, $"{name}_restore_conflict_{stamp}{extension}");
        int suffix = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name}_restore_conflict_{stamp}_{suffix}{extension}");
            suffix++;
        }

        return candidate;
    }

    private static string SafeBatchToken(string batchId)
    {
        var builder = new System.Text.StringBuilder(batchId.Length);
        foreach (char c in batchId)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.ToString();
    }

    private static bool Exists(string path)
    {
        string extended = NativeMethods.ToExtendedPath(path);
        return File.Exists(extended) || Directory.Exists(extended);
    }
}
