using System.Text;

namespace TidyC.Core.Tests.TestSupport;

/// <summary>
/// Builds syntactically valid NTFS FILE records (including the Update Sequence
/// Array) so the parser can be attacked with realistic adversarial input without
/// requiring an elevated volume handle.
/// </summary>
internal sealed class MftRecordBuilder
{
    public const int DefaultRecordSize = 1024;
    public const int SectorSize = 512;
    public const int FirstAttributeOffset = 0x38;

    private int _attributeId;

    public int RecordSize { get; set; } = DefaultRecordSize;

    public ushort SequenceNumber { get; set; } = 1;

    public bool IsDirectory { get; set; }

    public bool InUse { get; set; } = true;

    public ulong BaseRecord { get; set; }

    public bool IncludeStandardInformation { get; set; } = true;

    public bool IncludeFileName { get; set; } = true;

    public bool IncludeAttributeList { get; set; }

    public bool IncludeDataAttribute { get; set; } = true;

    public long DataAllocated { get; set; } = 4096;

    public long DataReal { get; set; } = 3000;

    public DateTime LastWriteUtc { get; set; } = new(2025, 5, 20, 9, 41, 22, DateTimeKind.Utc);

    public bool CorruptUsa { get; set; }

    public bool CorruptAttributeLength { get; set; }

    public List<NameLink> Links { get; } = new();

    public readonly record struct NameLink(string Name, ulong ParentFrn, byte Namespace = 1);

    public MftRecordBuilder AddLink(string name, ulong parentFrn, byte nameSpace = 1)
    {
        Links.Add(new NameLink(name, parentFrn, nameSpace));
        return this;
    }

    public byte[] Build()
    {
        var buffer = new byte[RecordSize];

        var attributes = new List<byte[]>();
        if (IncludeStandardInformation)
        {
            attributes.Add(BuildResident(0x10, BuildStandardInformationValue()));
        }

        if (IncludeFileName)
        {
            foreach (NameLink link in Links)
            {
                attributes.Add(BuildResident(0x30, BuildFileNameValue(link)));
            }
        }

        if (IncludeAttributeList)
        {
            attributes.Add(BuildResident(0x20, Array.Empty<byte>()));
        }

        if (IncludeDataAttribute)
        {
            attributes.Add(IsDirectory
                ? BuildNonResident(0xA0, DataAllocated, 0)
                : BuildNonResident(0x80, DataAllocated, DataReal));
        }

        int cursor = FirstAttributeOffset;
        foreach (byte[] attribute in attributes)
        {
            Array.Copy(attribute, 0, buffer, cursor, attribute.Length);
            cursor += attribute.Length;
        }

        WriteU32(buffer, cursor, 0xFFFFFFFF);
        WriteU32(buffer, cursor + 4, 0);
        cursor += 8;

        // ---- FILE record header ----
        Encoding.ASCII.GetBytes("FILE", 0, 4, buffer, 0);
        int usaCount = (RecordSize / SectorSize) + 1;
        WriteU16(buffer, 4, 0x30);
        WriteU16(buffer, 6, (ushort)usaCount);
        WriteU16(buffer, 16, SequenceNumber);
        WriteU16(buffer, 18, (ushort)Math.Max(1, Links.Count));
        WriteU16(buffer, 20, FirstAttributeOffset);
        WriteU16(buffer, 22, (ushort)((InUse ? 0x0001 : 0x0000) | (IsDirectory ? 0x0002 : 0x0000)));
        WriteU32(buffer, 24, (uint)cursor);
        WriteU32(buffer, 28, (uint)RecordSize);
        WriteU64(buffer, 32, BaseRecord);
        WriteU16(buffer, 40, (ushort)(attributes.Count + 1));
        WriteU32(buffer, 44, 0);

        // ---- Update Sequence Array ----
        const ushort usn = 0x0001;
        WriteU16(buffer, 0x30, usn);
        for (int i = 1; i < usaCount; i++)
        {
            int tail = (i * SectorSize) - 2;
            ushort original = ReadU16(buffer, tail);
            WriteU16(buffer, 0x30 + (i * 2), original);
            WriteU16(buffer, tail, CorruptUsa && i == 1 ? (ushort)0xDEAD : usn);
        }

        return buffer;
    }

    private byte[] BuildStandardInformationValue()
    {
        var value = new byte[72];
        long fileTime = LastWriteUtc.ToFileTimeUtc();
        WriteU64(value, 0, (ulong)fileTime);
        WriteU64(value, 8, (ulong)fileTime);
        WriteU64(value, 16, (ulong)fileTime);
        WriteU64(value, 24, (ulong)fileTime);
        uint attributes = 0x20 | (IsDirectory ? 0x10u : 0u);
        WriteU32(value, 32, attributes);
        return value;
    }

    private byte[] BuildFileNameValue(NameLink link)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(link.Name);
        var value = new byte[66 + nameBytes.Length];
        WriteU64(value, 0, link.ParentFrn);
        WriteU64(value, 40, (ulong)Math.Max(0, DataAllocated));
        WriteU64(value, 48, (ulong)Math.Max(0, DataReal));
        WriteU32(value, 56, 0x20 | (IsDirectory ? 0x10u : 0u));
        WriteU32(value, 60, 0);
        value[64] = (byte)link.Name.Length;
        value[65] = link.Namespace;
        Array.Copy(nameBytes, 0, value, 66, nameBytes.Length);
        return value;
    }

    private byte[] BuildResident(uint type, byte[] value)
    {
        int length = Align8(24 + value.Length);
        var attribute = new byte[length];
        WriteU32(attribute, 0, type);
        WriteU32(attribute, 4, CorruptAttributeLength ? 0x7FFFFFF0u : (uint)length);
        attribute[8] = 0;
        attribute[9] = 0;
        WriteU16(attribute, 10, 0);
        WriteU16(attribute, 12, 0);
        WriteU16(attribute, 14, (ushort)++_attributeId);
        WriteU32(attribute, 16, (uint)value.Length);
        WriteU16(attribute, 20, 24);
        attribute[22] = 0;
        Array.Copy(value, 0, attribute, 24, value.Length);
        return attribute;
    }

    private byte[] BuildNonResident(uint type, long allocated, long real)
    {
        const int length = 72;
        var attribute = new byte[length];
        WriteU32(attribute, 0, type);
        WriteU32(attribute, 4, length);
        attribute[8] = 1;
        attribute[9] = 0;
        WriteU16(attribute, 10, 0);
        WriteU16(attribute, 12, 0);
        WriteU16(attribute, 14, (ushort)++_attributeId);
        WriteU64(attribute, 16, 0); // lowest VCN
        WriteU64(attribute, 24, 0); // highest VCN
        WriteU16(attribute, 32, 64); // data run offset
        WriteU16(attribute, 34, 0); // compression unit
        WriteU64(attribute, 40, (ulong)Math.Max(0, allocated));
        WriteU64(attribute, 48, (ulong)Math.Max(0, real));
        WriteU64(attribute, 56, (ulong)Math.Max(0, real));
        return attribute;
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteU64(byte[] buffer, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * i));
        }
    }

    private static ushort ReadU16(byte[] buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
}
