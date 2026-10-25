using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace TidyC.Core.Scan;

public enum MftRecordStatus
{
    Success = 0,
    RecordTooSmall = 1,
    NotFileSignature = 2,
    UsaMismatch = 3,
    NotInUse = 4,
    ExtensionRecord = 5,
    AttributeListRecord = 6,
    NoFileName = 7,
    AttributeCorrupt = 8,
    DosShortNameOnly = 9,
}

public sealed class MftNameLink
{
    public ulong ParentFileReferenceNumber { get; init; }

    public string Name { get; init; } = string.Empty;

    public byte Namespace { get; init; }

    public long AllocatedSize { get; init; }

    public long RealSize { get; init; }

    public uint FileAttributes { get; init; }

    public bool HasReparsePoint { get; init; }
}

public sealed class MftFileEntry
{
    public ulong FileReferenceNumber { get; set; }

    public long RecordIndex { get; set; }

    public ushort SequenceNumber { get; set; }

    public bool IsDirectory { get; set; }

    public long AllocatedBytes { get; set; }

    public long LogicalBytes { get; set; }

    public DateTime LastWriteUtc { get; set; } = DateTime.MinValue;

    public uint FileAttributes { get; set; }

    public bool HasReparsePoint { get; set; }

    public List<MftNameLink> Links { get; } = new();

    /// <summary>True when every name is an 8.3 DOS alias; such a record is not usable.</summary>
    public bool IsDosOnly { get; set; }

    public int HardLinkCount => Links.Count;

    public MftNameLink? PrimaryLink { get; set; }

    public string Name => PrimaryLink?.Name ?? string.Empty;

    public ulong ParentFileReferenceNumber => PrimaryLink?.ParentFileReferenceNumber ?? 0;

    public string FileIdHex => $"0x{FileReferenceNumber:X16}";

    public override string ToString() => $"{Name} ({AllocatedBytes} B, dir={IsDirectory}, links={HardLinkCount})";
}

/// <summary>
/// Pure $MFT record parser. It owns zero I/O: the scanner hands it a buffer and
/// it returns either a validated entry or an explicit rejection reason, so every
/// adversarial case (bad magic, broken USA, deleted record, DOS alias, extension
/// record, attribute list) is unit-testable without touching a real volume.
/// </summary>
public static class MftRecordParser
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrIndexAllocation = 0xA0;
    public const uint AttrEnd = 0xFFFFFFFF;
    public const int RecordHeaderSize = 48;
    public const int AttributeHeaderSize = 24;

    private static readonly byte[] FileMagic = "FILE"u8.ToArray();

    public static MftRecordStatus TryParse(
        ReadOnlySpan<byte> record,
        long recordIndex,
        uint clusterSize,
        out MftFileEntry? entry,
        out string detail)
    {
        entry = null;
        detail = string.Empty;

        if (record.Length < RecordHeaderSize)
        {
            detail = $"记录长度 {record.Length} 小于文件记录头 {RecordHeaderSize}";
            return MftRecordStatus.RecordTooSmall;
        }

        if (!record.Slice(0, 4).SequenceEqual(FileMagic))
        {
            detail = $"非 FILE 魔数: 0x{BinaryPrimitives.ReadUInt32LittleEndian(record):X8}";
            return MftRecordStatus.NotFileSignature;
        }

        // ---- Update Sequence Array: verify every sector tail before use -----
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
        if (usaCount < 1 || usaOffset < RecordHeaderSize || usaOffset + (usaCount * 2) > record.Length)
        {
            detail = $"USA 越界: offset={usaOffset}, count={usaCount}";
            return MftRecordStatus.UsaMismatch;
        }

        int sectorSize = usaCount > 1 ? record.Length / (usaCount - 1) : 512;
        if (sectorSize < 16)
        {
            detail = $"USA 扇区推导异常: {sectorSize}";
            return MftRecordStatus.UsaMismatch;
        }

        ushort updateSequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset, 2));
        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * sectorSize) - 2;
            if (tail < 0 || tail + 2 > record.Length)
            {
                detail = $"USA 第 {i} 个扇区尾部越界";
                return MftRecordStatus.UsaMismatch;
            }

            ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(tail, 2));
            if (actual != updateSequenceNumber)
            {
                detail = $"USA 校验失败: 扇区 {i} 尾部 0x{actual:X4} != USN 0x{updateSequenceNumber:X4}";
                return MftRecordStatus.UsaMismatch;
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(record.Length);
        try
        {
            record.CopyTo(buffer);
            return TryParseInPlace(buffer.AsSpan(0, record.Length), recordIndex, clusterSize, out entry, out detail);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Same parser, but the USA fix-up is applied to the caller's buffer in
    /// place. The MFT scanner reads records directly into a reusable chunk, so
    /// this removes a rent/copy/return per record (millions of records).
    /// </summary>
    public static MftRecordStatus TryParseInPlace(
        Span<byte> record,
        long recordIndex,
        uint clusterSize,
        out MftFileEntry? entry,
        out string detail)
    {
        entry = null;
        detail = string.Empty;

        if (record.Length < RecordHeaderSize)
        {
            detail = $"记录长度 {record.Length} 小于文件记录头 {RecordHeaderSize}";
            return MftRecordStatus.RecordTooSmall;
        }

        if (!record.Slice(0, 4).SequenceEqual(FileMagic))
        {
            detail = $"非 FILE 魔数: 0x{BinaryPrimitives.ReadUInt32LittleEndian(record):X8}";
            return MftRecordStatus.NotFileSignature;
        }

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
        if (usaCount < 1 || usaOffset < RecordHeaderSize || usaOffset + (usaCount * 2) > record.Length)
        {
            detail = $"USA 越界: offset={usaOffset}, count={usaCount}";
            return MftRecordStatus.UsaMismatch;
        }

        int sectorSize = usaCount > 1 ? record.Length / (usaCount - 1) : 512;
        if (sectorSize < 16)
        {
            detail = $"USA 扇区推导异常: {sectorSize}";
            return MftRecordStatus.UsaMismatch;
        }

        ushort updateSequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset, 2));
        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * sectorSize) - 2;
            if (tail < 0 || tail + 2 > record.Length)
            {
                detail = $"USA 第 {i} 个扇区尾部越界";
                return MftRecordStatus.UsaMismatch;
            }

            ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(tail, 2));
            if (actual != updateSequenceNumber)
            {
                detail = $"USA 校验失败: 扇区 {i} 尾部 0x{actual:X4} != USN 0x{updateSequenceNumber:X4}";
                return MftRecordStatus.UsaMismatch;
            }
        }

        // Restore the real bytes over the USN placeholders, in place.
        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * sectorSize) - 2;
            int savedAt = usaOffset + (i * 2);
            record[tail] = record[savedAt];
            record[tail + 1] = record[savedAt + 1];
        }

        return ParseFixedUp(record, recordIndex, clusterSize, out entry, out detail);
    }

    private static MftRecordStatus ParseFixedUp(
        ReadOnlySpan<byte> record,
        long recordIndex,
        uint clusterSize,
        out MftFileEntry? entry,
        out string detail)
    {
        entry = null;
        detail = string.Empty;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(22, 2));
        if ((flags & 0x0001) == 0)
        {
            detail = "记录未在使用（已删除）";
            return MftRecordStatus.NotInUse;
        }

        bool isDirectory = (flags & 0x0002) != 0;
        ushort sequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(16, 2));

        ulong baseRecord = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(32, 8));
        if (baseRecord != 0)
        {
            detail = $"属性扩展记录（base=0x{baseRecord:X16}），拒收";
            return MftRecordStatus.ExtensionRecord;
        }

        int firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));
        if (firstAttributeOffset < RecordHeaderSize || firstAttributeOffset >= record.Length)
        {
            detail = $"首个属性偏移非法: {firstAttributeOffset}";
            return MftRecordStatus.AttributeCorrupt;
        }

        var links = new List<MftNameLink>(2);
        long allocated = 0;
        long logical = 0;
        long indexAllocated = 0;
        DateTime lastWrite = DateTime.MinValue;
        uint fileAttributes = 0;
        bool sawDosAlias = false;
        bool sawEndMarker = false;

        int offset = firstAttributeOffset;
        while (offset + 8 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset, 4));
            if (type == AttrEnd)
            {
                sawEndMarker = true;
                break;
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset + 4, 4));
            long attributeEnd = (long)offset + length;
            if (length < AttributeHeaderSize || attributeEnd > record.Length)
            {
                detail = $"属性 0x{type:X} 长度非法: {length}";
                return MftRecordStatus.AttributeCorrupt;
            }

            if (type == AttrAttributeList)
            {
                detail = "存在 $ATTRIBUTE_LIST（属性分片），记录不完整，拒收";
                return MftRecordStatus.AttributeListRecord;
            }

            bool nonResident = record[offset + 8] != 0;
            ReadOnlySpan<byte> attribute = record.Slice(offset, (int)length);

            if (type == AttrStandardInformation && !nonResident)
            {
                int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(20, 2));
                int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute.Slice(16, 4));
                if (valueOffset + valueLength <= attribute.Length && valueLength >= 36)
                {
                    ReadOnlySpan<byte> value = attribute.Slice(valueOffset, valueLength);
                    long modified = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(8, 8));
                    if (modified > 0)
                    {
                        try
                        {
                            lastWrite = DateTime.FromFileTimeUtc(modified);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            lastWrite = DateTime.MinValue;
                        }
                    }

                    fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(32, 4));
                }
            }
            else if (type == AttrFileName && !nonResident)
            {
                int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(20, 2));
                int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute.Slice(16, 4));
                if (valueOffset + valueLength > attribute.Length || valueLength < 66)
                {
                    detail = "$FILE_NAME 属性长度非法";
                    return MftRecordStatus.AttributeCorrupt;
                }

                ReadOnlySpan<byte> value = attribute.Slice(valueOffset, valueLength);
                byte nameLength = value[64];
                byte nameSpace = value[65];
                if (nameSpace > 3 || 66 + (nameLength * 2) > value.Length)
                {
                    detail = $"$FILE_NAME 命名空间/长度非法: ns={nameSpace}, len={nameLength}";
                    return MftRecordStatus.AttributeCorrupt;
                }

                string name = Encoding.Unicode.GetString(value.Slice(66, nameLength * 2));
                if (name is "." or "..")
                {
                    offset += (int)length;
                    continue;
                }

                if (nameSpace == 2)
                {
                    // DOS 8.3 alias only: never a user-visible file name.
                    sawDosAlias = true;
                    offset += (int)length;
                    continue;
                }

                ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0, 8)) & 0x0000FFFFFFFFFFFFUL;
                long linkAllocated = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(40, 8));
                long linkReal = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(48, 8));
                uint linkAttributes = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(56, 4));
                uint reparseTag = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(60, 4));

                if (!links.Any(l => l.ParentFileReferenceNumber == parent &&
                                    string.Equals(l.Name, name, StringComparison.Ordinal)))
                {
                    links.Add(new MftNameLink
                    {
                        ParentFileReferenceNumber = parent,
                        Name = name,
                        Namespace = nameSpace,
                        AllocatedSize = linkAllocated,
                        RealSize = linkReal,
                        FileAttributes = linkAttributes,
                        HasReparsePoint = (linkAttributes & 0x00000400) != 0 || reparseTag != 0,
                    });
                }
            }
            else if (type is AttrData or AttrIndexAllocation)
            {
                if (nonResident && length >= 64)
                {
                    long attrAllocated = BinaryPrimitives.ReadInt64LittleEndian(attribute.Slice(40, 8));
                    long attrReal = BinaryPrimitives.ReadInt64LittleEndian(attribute.Slice(48, 8));
                    if (type == AttrData)
                    {
                        allocated += Math.Max(0, attrAllocated);
                        logical += Math.Max(0, attrReal);
                    }
                    else
                    {
                        indexAllocated += Math.Max(0, attrAllocated);
                    }
                }
                else if (!nonResident)
                {
                    int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute.Slice(16, 4));
                    if (type == AttrData)
                    {
                        logical += valueLength;
                        allocated += RoundUp(valueLength, clusterSize);
                    }
                }
            }

            offset += (int)length;
        }

        if (!sawEndMarker)
        {
            detail = "属性链未以 0xFFFFFFFF 结束，记录可能被截断";
            return MftRecordStatus.AttributeCorrupt;
        }

        if (links.Count == 0)
        {
            detail = sawDosAlias ? "仅存在 DOS 8.3 短文件名别名，拒收" : "记录不含 $FILE_NAME 属性";
            return sawDosAlias ? MftRecordStatus.DosShortNameOnly : MftRecordStatus.NoFileName;
        }

        // Prefer a Windows-visible name; POSIX-only names are not rendered by Explorer.
        MftNameLink primary = links
            .OrderBy(l => l.Namespace == 0 ? 1 : 0)
            .ThenByDescending(l => l.Name.Length)
            .First();

        if (isDirectory)
        {
            allocated += indexAllocated;
        }
        else if (allocated == 0 && logical > 0)
        {
            allocated = RoundUp(logical, clusterSize);
        }

        var fileEntry = new MftFileEntry
        {
            FileReferenceNumber = ((ulong)sequenceNumber << 48) | ((ulong)recordIndex & 0x0000FFFFFFFFFFFFUL),
            RecordIndex = recordIndex,
            SequenceNumber = sequenceNumber,
            IsDirectory = isDirectory,
            AllocatedBytes = allocated,
            LogicalBytes = logical,
            LastWriteUtc = lastWrite,
            FileAttributes = fileAttributes != 0 ? fileAttributes : primary.FileAttributes,
            HasReparsePoint = primary.HasReparsePoint || (fileAttributes & 0x00000400) != 0,
            IsDosOnly = false,
            PrimaryLink = primary,
        };

        fileEntry.Links.AddRange(links);
        entry = fileEntry;
        return MftRecordStatus.Success;
    }

    private static long RoundUp(long value, uint clusterSize)
    {
        if (value <= 0)
        {
            return 0;
        }

        long cluster = Math.Max(1u, clusterSize);
        return ((value + cluster - 1) / cluster) * cluster;
    }

    /// <summary>
    /// Extracts the non-resident data runs of the unnamed $DATA attribute. Used
    /// to follow a fragmented $MFT (record 0) across its extents without opening
    /// the metafile by name (which its ACL denies even to administrators).
    /// </summary>
    public static bool TryGetDataRuns(
        ReadOnlySpan<byte> record,
        out IReadOnlyList<(long StartLcn, long ClusterCount)> runs)
    {
        runs = Array.Empty<(long, long)>();

        if (record.Length < RecordHeaderSize || !record.Slice(0, 4).SequenceEqual(FileMagic))
        {
            return false;
        }

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
        if (usaCount < 1 || usaOffset < RecordHeaderSize || usaOffset + (usaCount * 2) > record.Length)
        {
            return false;
        }

        int sectorSize = usaCount > 1 ? record.Length / (usaCount - 1) : 512;
        if (sectorSize < 16)
        {
            return false;
        }

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset, 2));
        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * sectorSize) - 2;
            if (tail < 0 || tail + 2 > record.Length ||
                BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(tail, 2)) != usn)
            {
                return false;
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(record.Length);
        try
        {
            record.CopyTo(buffer);
            for (int i = 1; i < usaCount; i++)
            {
                int tail = (i * sectorSize) - 2;
                int savedAt = usaOffset + (i * 2);
                buffer[tail] = buffer[savedAt];
                buffer[tail + 1] = buffer[savedAt + 1];
            }

            ReadOnlySpan<byte> fixedRecord = buffer.AsSpan(0, record.Length);
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(20, 2));

            while (offset + 8 <= fixedRecord.Length)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(fixedRecord.Slice(offset, 4));
                if (type == AttrEnd)
                {
                    break;
                }

                uint length = BinaryPrimitives.ReadUInt32LittleEndian(fixedRecord.Slice(offset + 4, 4));
                if (length < AttributeHeaderSize || offset + (long)length > fixedRecord.Length)
                {
                    break;
                }

                bool nonResident = fixedRecord[offset + 8] != 0;
                int nameLength = fixedRecord[offset + 9];
                if (type == AttrData && nonResident && nameLength == 0 && length >= 64)
                {
                    int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(offset + 32, 2));
                    if (runOffset >= 64 && runOffset < length)
                    {
                        var parsed = DecodeRuns(fixedRecord.Slice(offset, (int)length), runOffset);
                        if (parsed.Count > 0)
                        {
                            runs = parsed;
                            return true;
                        }
                    }
                }

                offset += (int)length;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<(long StartLcn, long ClusterCount)> DecodeRuns(ReadOnlySpan<byte> attribute, int runOffset)
    {
        var runs = new List<(long, long)>();
        long currentLcn = 0;
        int offset = runOffset;

        while (offset < attribute.Length)
        {
            byte header = attribute[offset];
            if (header == 0)
            {
                break;
            }

            offset++;
            int lengthBytes = header & 0x0F;
            int offsetBytes = (header >> 4) & 0x0F;
            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || offset + lengthBytes + offsetBytes > attribute.Length)
            {
                return new List<(long, long)>();
            }

            long clusterCount = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                clusterCount |= (long)attribute[offset + i] << (8 * i);
            }

            offset += lengthBytes;

            long delta = 0;
            for (int i = 0; i < offsetBytes; i++)
            {
                delta |= (long)attribute[offset + i] << (8 * i);
            }

            if (offsetBytes is > 0 and < 8)
            {
                long signBit = 1L << ((8 * offsetBytes) - 1);
                if ((delta & signBit) != 0)
                {
                    delta |= ~((1L << (8 * offsetBytes)) - 1);
                }
            }

            offset += offsetBytes;
            currentLcn += delta;

            if (clusterCount > 0 && currentLcn > 0)
            {
                runs.Add((currentLcn, clusterCount));
            }
        }

        return runs;
    }
}
