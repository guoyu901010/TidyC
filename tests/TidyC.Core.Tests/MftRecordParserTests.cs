using TidyC.Core.Scan;
using TidyC.Core.Tests.TestSupport;

namespace TidyC.Core.Tests;

public sealed class MftRecordParserTests
{
    private const ulong RootFrn = 5;

    [Fact]
    public void ParsesNamedFile()
    {
        byte[] record = new MftRecordBuilder()
            .AddLink("hello.txt", RootFrn)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 42, 4096, out MftFileEntry? entry, out string detail);

        Assert.Equal(MftRecordStatus.Success, status);
        Assert.NotNull(entry);
        Assert.Equal("hello.txt", entry!.Name);
        Assert.Equal(RootFrn, entry.ParentFileReferenceNumber);
        Assert.False(entry.IsDirectory);
        Assert.Equal(4096, entry.AllocatedBytes);
        Assert.Equal(3000, entry.LogicalBytes);
        Assert.Equal(1, entry.HardLinkCount);
        Assert.StartsWith("0x", entry.FileIdHex);
        Assert.Equal(1, entry.SequenceNumber);
    }

    [Fact]
    public void ParsesDirectoryFlag()
    {
        byte[] record = new MftRecordBuilder
        {
            IsDirectory = true,
            DataAllocated = 8192,
        }.AddLink("ProgramData", RootFrn).Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 7, 4096, out MftFileEntry? entry, out _);

        Assert.Equal(MftRecordStatus.Success, status);
        Assert.True(entry!.IsDirectory);
        Assert.Equal(8192, entry.AllocatedBytes);
    }

    [Fact]
    public void AppliesUsaFixupAcrossSectorBoundary()
    {
        string longName = new string('a', 250) + ".txt";
        byte[] record = new MftRecordBuilder()
            .AddLink(longName, RootFrn)
            .Build();

        byte[] copy = (byte[])record.Clone();
        // Prove the raw buffer really does hold USN placeholders at the sector tail.
        Assert.Equal(1, (int)copy[510]);
        Assert.Equal(0, (int)copy[511]);

        MftRecordStatus status = MftRecordParser.TryParse(record, 3, 4096, out MftFileEntry? entry, out string detail);

        Assert.True(status == MftRecordStatus.Success, detail);
        Assert.Equal(longName, entry!.Name);
    }

    [Fact]
    public void RejectsBadSignature()
    {
        byte[] record = new byte[1024];
        "BAAD"u8.CopyTo(record);

        MftRecordStatus status = MftRecordParser.TryParse(record, 1, 4096, out _, out string detail);

        Assert.Equal(MftRecordStatus.NotFileSignature, status);
        Assert.Contains("魔数", detail);
    }

    [Fact]
    public void RejectsUsaMismatch()
    {
        byte[] record = new MftRecordBuilder { CorruptUsa = true }
            .AddLink("broken.txt", RootFrn)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 1, 4096, out MftFileEntry? entry, out string detail);

        Assert.Equal(MftRecordStatus.UsaMismatch, status);
        Assert.Null(entry);
        Assert.Contains("USA", detail);
    }

    [Fact]
    public void RejectsDeletedRecord()
    {
        byte[] record = new MftRecordBuilder { InUse = false }
            .AddLink("deleted.txt", RootFrn)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 9, 4096, out _, out _);

        Assert.Equal(MftRecordStatus.NotInUse, status);
    }

    [Fact]
    public void RejectsExtensionRecord()
    {
        byte[] record = new MftRecordBuilder { BaseRecord = 0x1234 }
            .AddLink("extended.txt", RootFrn)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 10, 4096, out _, out _);

        Assert.Equal(MftRecordStatus.ExtensionRecord, status);
    }

    [Fact]
    public void RejectsAttributeListRecord()
    {
        byte[] record = new MftRecordBuilder { IncludeAttributeList = true }
            .AddLink("fragmented.txt", RootFrn)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 11, 4096, out _, out _);

        Assert.Equal(MftRecordStatus.AttributeListRecord, status);
    }

    [Fact]
    public void RejectsDosShortNameOnly()
    {
        byte[] record = new MftRecordBuilder()
            .AddLink("FILE~1.TXT", RootFrn, nameSpace: 2)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 12, 4096, out _, out string detail);

        Assert.Equal(MftRecordStatus.DosShortNameOnly, status);
        Assert.Contains("8.3", detail);
    }

    [Fact]
    public void PrefersLongNameOverDosAlias()
    {
        byte[] record = new MftRecordBuilder()
            .AddLink("verylongfilename.txt", RootFrn, nameSpace: 1)
            .AddLink("VERYLO~1.TXT", RootFrn, nameSpace: 2)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 13, 4096, out MftFileEntry? entry, out _);

        Assert.Equal(MftRecordStatus.Success, status);
        Assert.Equal("verylongfilename.txt", entry!.Name);
        Assert.Equal(1, entry.HardLinkCount);
    }

    [Fact]
    public void ParsesTwoHardLinkParents()
    {
        byte[] record = new MftRecordBuilder()
            .AddLink("shared.dat", 5)
            .AddLink("alias.dat", 42)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 14, 4096, out MftFileEntry? entry, out _);

        Assert.Equal(MftRecordStatus.Success, status);
        Assert.Equal(2, entry!.HardLinkCount);
        Assert.Contains(entry.Links, l => l.ParentFileReferenceNumber == 42 && l.Name == "alias.dat");
        Assert.Contains(entry.Links, l => l.ParentFileReferenceNumber == 5 && l.Name == "shared.dat");
    }

    [Fact]
    public void RejectsTruncatedRecord()
    {
        byte[] record = new MftRecordBuilder().AddLink("tiny.txt", RootFrn).Build()[..32];

        MftRecordStatus status = MftRecordParser.TryParse(record, 15, 4096, out _, out _);

        Assert.Equal(MftRecordStatus.RecordTooSmall, status);
    }

    [Fact]
    public void RejectsRecordWithoutFileName()
    {
        byte[] record = new MftRecordBuilder { IncludeFileName = false }.Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 16, 4096, out _, out _);

        Assert.Equal(MftRecordStatus.NoFileName, status);
    }

    [Fact]
    public void RejectsCorruptAttributeLength()
    {
        byte[] record = new MftRecordBuilder { CorruptAttributeLength = true }
            .AddLink("corrupt.txt", RootFrn)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 17, 4096, out _, out _);

        Assert.Equal(MftRecordStatus.AttributeCorrupt, status);
    }

    [Fact]
    public void ParsesNonResidentAllocatedSize()
    {
        byte[] record = new MftRecordBuilder
        {
            DataAllocated = 8192,
            DataReal = 5000,
        }.AddLink("video.mp4", RootFrn).Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 18, 4096, out MftFileEntry? entry, out _);

        Assert.Equal(MftRecordStatus.Success, status);
        Assert.Equal(8192, entry!.AllocatedBytes);
        Assert.Equal(5000, entry.LogicalBytes);
    }

    [Fact]
    public void ParsesLastWriteTimestamp()
    {
        var expected = new DateTime(2025, 5, 20, 9, 41, 22, DateTimeKind.Utc);
        byte[] record = new MftRecordBuilder { LastWriteUtc = expected }
            .AddLink("stamped.txt", RootFrn)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 19, 4096, out MftFileEntry? entry, out _);

        Assert.Equal(MftRecordStatus.Success, status);
        Assert.Equal(expected, entry!.LastWriteUtc);
    }

    [Fact]
    public void AcceptsWindowsAndDosCombinedNamespace()
    {
        byte[] record = new MftRecordBuilder()
            .AddLink("combined.txt", RootFrn, nameSpace: 3)
            .Build();

        MftRecordStatus status = MftRecordParser.TryParse(record, 20, 4096, out MftFileEntry? entry, out _);

        Assert.Equal(MftRecordStatus.Success, status);
        Assert.Equal("combined.txt", entry!.Name);
    }
}
