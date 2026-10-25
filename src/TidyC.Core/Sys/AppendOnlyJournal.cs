using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TidyC.Core.Sys;

public sealed class JournalRecord
{
    public JournalRecord(long seq, string action, string txId, string hash, string prevHash, string raw)
    {
        Seq = seq;
        Action = action;
        TxId = txId;
        Hash = hash;
        PrevHash = prevHash;
        Raw = raw;
    }

    public long Seq { get; }

    public string Action { get; }

    public string TxId { get; }

    public string Hash { get; }

    public string PrevHash { get; }

    public string Raw { get; }

    public override string ToString() => Raw;
}

public sealed class JournalVerification
{
    public bool IsValid { get; init; }

    public long LineCount { get; init; }

    public long? BrokenSeq { get; init; }

    public string Reason { get; init; } = string.Empty;

    public override string ToString() =>
        IsValid ? $"链校验通过（{LineCount} 行）" : $"链校验失败（第 {BrokenSeq} 行）：{Reason}";
}

/// <summary>
/// Append-only, hash-chained audit log. Every move / restore / purge line is
/// written and flushed *before* the filesystem action it describes, so a crash
/// can never produce an action without a trace. Each record carries
/// <c>prev_hash</c> and its own SHA-256 over the canonical JSON, which makes
/// silent edits or line removal detectable by <see cref="VerifyChain"/>.
/// </summary>
public sealed class AppendOnlyJournal : IDisposable
{
    public const string GenesisHash = "GENESIS";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly bool _flushToDisk;
    private FileStream? _stream;
    private long _seq;
    private string _lastHash = GenesisHash;

    public AppendOnlyJournal(string directory, string fileName, bool flushToDisk = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        _directory = directory;
        _flushToDisk = flushToDisk;
        FilePath = Path.Combine(directory, fileName);
        Directory.CreateDirectory(directory);
        LoadTail();
    }

    public string FilePath { get; }

    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    public string LastHash
    {
        get
        {
            lock (_gate)
            {
                return _lastHash;
            }
        }
    }

    /// <summary>Appends one record and returns its chain hash.</summary>
    public string Append(string action, string txId, IReadOnlyDictionary<string, object?>? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        lock (_gate)
        {
            var node = new JsonObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O"),
                ["tx_id"] = txId ?? string.Empty,
                ["action"] = action,
            };

            if (data is not null)
            {
                foreach (KeyValuePair<string, object?> pair in data)
                {
                    node[pair.Key] = pair.Value is null
                        ? null
                        : JsonSerializer.SerializeToNode(pair.Value, JsonOptions);
                }
            }

            long seq = _seq + 1;
            string prevHash = _lastHash;
            node["seq"] = seq;
            node["prev_hash"] = prevHash;

            string canonical = node.ToJsonString(JsonOptions);
            string hash = Sha256Hex(canonical);
            node["hash"] = hash;

            string line = node.ToJsonString(JsonOptions);
            WriteLine(line);

            _seq = seq;
            _lastHash = hash;
            return hash;
        }
    }

    public IReadOnlyList<string> ReadAllLines()
    {
        if (!File.Exists(FilePath))
        {
            return Array.Empty<string>();
        }

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    public JournalVerification VerifyChain() => VerifyFile(FilePath);

    public static JournalVerification VerifyFile(string path)
    {
        if (!File.Exists(path))
        {
            return new JournalVerification { IsValid = true, LineCount = 0, Reason = "文件不存在（空日志）" };
        }

        long expectedSeq = 1;
        string expectedPrev = GenesisHash;
        long count = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            count++;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                return new JournalVerification
                {
                    IsValid = false,
                    LineCount = count,
                    BrokenSeq = expectedSeq,
                    Reason = "非法 JSON：" + ex.Message,
                };
            }

            if (node is not JsonObject obj)
            {
                return new JournalVerification
                {
                    IsValid = false, LineCount = count, BrokenSeq = expectedSeq, Reason = "记录不是 JSON 对象",
                };
            }

            long seq = obj["seq"]?.GetValue<long>() ?? -1;
            string prev = obj["prev_hash"]?.GetValue<string>() ?? string.Empty;
            string hash = obj["hash"]?.GetValue<string>() ?? string.Empty;

            if (seq != expectedSeq)
            {
                return new JournalVerification
                {
                    IsValid = false,
                    LineCount = count,
                    BrokenSeq = seq,
                    Reason = $"序号断档：期望 {expectedSeq}，实际 {seq}（疑似删除或重排）",
                };
            }

            if (!string.Equals(prev, expectedPrev, StringComparison.Ordinal))
            {
                return new JournalVerification
                {
                    IsValid = false,
                    LineCount = count,
                    BrokenSeq = seq,
                    Reason = "prev_hash 与前一条记录不衔接（疑似篡改）",
                };
            }

            obj.Remove("hash");
            string canonical = obj.ToJsonString(JsonOptions);
            string recomputed = Sha256Hex(canonical);
            if (!string.Equals(recomputed, hash, StringComparison.OrdinalIgnoreCase))
            {
                return new JournalVerification
                {
                    IsValid = false,
                    LineCount = count,
                    BrokenSeq = seq,
                    Reason = "内容哈希不匹配（疑似篡改）",
                };
            }

            expectedPrev = hash;
            expectedSeq++;
        }

        return new JournalVerification { IsValid = true, LineCount = count, Reason = "OK" };
    }

    public static string Sha256Hex(string content)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024);
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void WriteLine(string line)
    {
        _stream ??= new FileStream(
            FilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            _flushToDisk ? FileOptions.WriteThrough : FileOptions.None);

        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
        _stream.Write(payload, 0, payload.Length);
        _stream.Flush(_flushToDisk);
    }

    private void LoadTail()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        // Recover seq / last hash so a restarted process keeps the chain intact.
        foreach (string line in ReadAllLines())
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject obj)
                {
                    _seq = obj["seq"]?.GetValue<long>() ?? _seq;
                    _lastHash = obj["hash"]?.GetValue<string>() ?? _lastHash;
                }
            }
            catch (JsonException)
            {
                // A torn tail line is left untouched (append-only); the hash
                // chain starts again after it and VerifyChain reports it.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
