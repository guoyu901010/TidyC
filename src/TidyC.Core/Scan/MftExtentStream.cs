using Microsoft.Win32.SafeHandles;

namespace TidyC.Core.Scan;

/// <summary>
/// Sequential reader over the non-contiguous extents of an NTFS metafile. The
/// raw volume handle is read positionally (<see cref="RandomAccess"/>), so the
/// $MFT can be streamed as one logical file even when its data runs are
/// fragmented — which is the normal case for a multi-gigabyte MFT.
/// </summary>
internal sealed class MftExtentStream : Stream
{
    private readonly SafeFileHandle _volume;
    private readonly IReadOnlyList<MftExtent> _extents;
    private readonly long _length;
    private long _position;

    public MftExtentStream(SafeFileHandle volume, IReadOnlyList<MftExtent> extents, long length)
    {
        _volume = volume;
        _extents = extents;
        _length = Math.Max(0, length);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _position >= _length)
        {
            return 0;
        }

        int requested = (int)Math.Min(buffer.Length, _length - _position);
        int total = 0;

        while (total < requested)
        {
            if (!TryMap(_position + total, out MftExtent extent, out long offsetInExtent))
            {
                break;
            }

            long available = extent.Length - offsetInExtent;
            int chunk = (int)Math.Min(requested - total, available);
            if (chunk <= 0)
            {
                break;
            }

            int read = RandomAccess.Read(_volume, buffer.Slice(total, chunk), extent.StartByte + offsetInExtent);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (read < chunk)
            {
                break;
            }
        }

        _position += total;
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position,
        };
        _position = Math.Clamp(_position, 0, _length);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private bool TryMap(long logicalOffset, out MftExtent extent, out long offsetInExtent)
    {
        long cursor = 0;
        foreach (MftExtent candidate in _extents)
        {
            if (logicalOffset < cursor + candidate.Length)
            {
                extent = candidate;
                offsetInExtent = logicalOffset - cursor;
                return true;
            }

            cursor += candidate.Length;
        }

        extent = default;
        offsetInExtent = 0;
        return false;
    }
}

internal readonly record struct MftExtent(long StartByte, long Length);
