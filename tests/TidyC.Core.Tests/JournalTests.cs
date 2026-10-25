using TidyC.Core.Sys;
using TidyC.Core.Tests.TestSupport;

namespace TidyC.Core.Tests;

public sealed class JournalTests
{
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Append_WritesJsonlAndVerifies()
    {
        using var tree = new TempTree("journal-append");
        using var journal = new AppendOnlyJournal(tree.Root, "journal-test.jsonl");

        journal.Append("MOVE_START", "TX_1", new Dictionary<string, object?> { ["path"] = @"C:\a\b" });
        journal.Append("MOVE_COMMITTED", "TX_1", new Dictionary<string, object?> { ["bytes"] = 4096 });
        journal.Append("PURGE", "TX_2");

        Assert.Equal(3, journal.Count);
        Assert.Equal(3, journal.ReadAllLines().Count);
        JournalVerification verification = journal.VerifyChain();
        Assert.True(verification.IsValid, verification.ToString());
        Assert.Equal(3, verification.LineCount);
        Assert.Contains("MOVE_COMMITTED", ReadShared(journal.FilePath));
    }

    [Fact]
    public void VerifyChain_DetectsTamperedLine()
    {
        using var tree = new TempTree("journal-tamper");
        using (var journal = new AppendOnlyJournal(tree.Root, "journal-tamper.jsonl"))
        {
            journal.Append("A", "TX", new Dictionary<string, object?> { ["size"] = 1 });
            journal.Append("B", "TX", new Dictionary<string, object?> { ["size"] = 2 });
        }

        string path = Path.Combine(tree.Root, "journal-tamper.jsonl");
        string[] lines = File.ReadAllLines(path);
        lines[0] = lines[0].Replace("\"size\":1", "\"size\":999");
        File.WriteAllLines(path, lines);

        JournalVerification verification = AppendOnlyJournal.VerifyFile(path);

        Assert.False(verification.IsValid);
        Assert.Equal(1, verification.BrokenSeq);
        Assert.Contains("哈希", verification.Reason);
    }

    [Fact]
    public void Append_IsThreadSafe()
    {
        using var tree = new TempTree("journal-thread");
        using var journal = new AppendOnlyJournal(tree.Root, "journal-thread.jsonl");

        Parallel.For(0, 64, i => journal.Append("EVENT", "TX_" + i, new Dictionary<string, object?> { ["i"] = i }));

        Assert.Equal(64, journal.Count);
        JournalVerification verification = journal.VerifyChain();
        Assert.True(verification.IsValid, verification.ToString());
    }

    [Fact]
    public void Reopen_ContinuesHashChain()
    {
        using var tree = new TempTree("journal-reopen");
        string name = "journal-chain.jsonl";
        using (var journal = new AppendOnlyJournal(tree.Root, name))
        {
            journal.Append("FIRST", "TX");
        }

        using (var reopened = new AppendOnlyJournal(tree.Root, name))
        {
            reopened.Append("SECOND", "TX");
            Assert.Equal(2, reopened.Count);
            Assert.True(reopened.VerifyChain().IsValid);
        }

        JournalVerification verification = AppendOnlyJournal.VerifyFile(Path.Combine(tree.Root, name));
        Assert.True(verification.IsValid, verification.ToString());
    }

    [Fact]
    public void JournalFile_IsAppendOnly()
    {
        using var tree = new TempTree("journal-mode");
        using var journal = new AppendOnlyJournal(tree.Root, "journal-mode.jsonl");
        journal.Append("ONE", "TX");

        long lengthBefore = new FileInfo(journal.FilePath).Length;
        journal.Append("TWO", "TX");

        Assert.True(new FileInfo(journal.FilePath).Length > lengthBefore);
        Assert.StartsWith("{\"timestamp\"", ReadShared(journal.FilePath));
    }
}
