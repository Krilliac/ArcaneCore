namespace ArcaneCore.Atlas.Live;

/// <summary>What <see cref="NdjsonReader.ReadAsync"/> found next.</summary>
internal enum NdjsonKind
{
    /// <summary>A complete, non-empty line (newline and any trailing carriage return removed).</summary>
    Line,

    /// <summary>A line longer than the limit; its bytes were discarded up to and including its newline.</summary>
    Oversize,

    /// <summary>The stream ended in the middle of a line (a partial line is never parsed).</summary>
    Truncated,

    /// <summary>The stream ended cleanly.</summary>
    End,
}

/// <summary>One result of <see cref="NdjsonReader.ReadAsync"/>.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Line">The line's bytes for <see cref="NdjsonKind.Line"/>; valid until the next read.</param>
/// <param name="Length">The line's length in bytes (for oversize and truncated lines, the bytes discarded).</param>
internal readonly record struct NdjsonItem(NdjsonKind Kind, ReadOnlyMemory<byte> Line, long Length);

/// <summary>
/// Splits a byte stream into newline-delimited lines without ever holding more than <c>maxLineBytes</c> of one line. A longer line is
/// skipped (its bytes are read and dropped, never buffered) and reported as <see cref="NdjsonKind.Oversize"/>, after which reading resumes at
/// the next line: one runaway frame costs one frame, not the connection, and a peer that never sends a newline cannot grow memory.
/// Blank lines are skipped. One reader per stream; not thread-safe.
/// </summary>
internal sealed class NdjsonReader(Stream stream, int maxLineBytes, int bufferSize = NdjsonReader.DefaultBufferSize)
{
    public const int DefaultBufferSize = 64 * 1024;

    // A carry buffer that grew for one large line is dropped at the next read rather than kept for the life of the connection.
    private const int CarryKeepBytes = 1024 * 1024;

    private readonly byte[] _buffer = new byte[bufferSize];
    private byte[] _carry = [];
    private int _carryLength;
    private int _start;
    private int _end;
    private bool _discarding;
    private long _discarded;

    /// <summary>Total bytes read from the stream so far.</summary>
    public long BytesRead { get; private set; }

    /// <summary>Bytes this reader holds right now: its read buffer plus the carry of a line still being assembled. Bounded by the buffer size and the line limit.</summary>
    public int RetainedBytes => _buffer.Length + _carry.Length;

    /// <summary>Reads the next line. The returned memory is valid until the next call.</summary>
    public async ValueTask<NdjsonItem> ReadAsync(CancellationToken cancellationToken)
    {
        if (_carry.Length > CarryKeepBytes && _carryLength == 0)
        {
            _carry = [];
        }

        while (true)
        {
            if (_start < _end)
            {
                int nl = new ReadOnlySpan<byte>(_buffer, _start, _end - _start).IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    int segmentStart = _start;
                    _start += nl + 1;
                    if (_discarding)
                    {
                        long total = _discarded + nl;
                        _discarding = false;
                        _discarded = 0;
                        return new NdjsonItem(NdjsonKind.Oversize, default, total);
                    }

                    long length = (long)_carryLength + nl;
                    if (length > maxLineBytes)
                    {
                        _carryLength = 0;
                        return new NdjsonItem(NdjsonKind.Oversize, default, length);
                    }

                    ReadOnlyMemory<byte> line;
                    if (_carryLength == 0)
                    {
                        line = _buffer.AsMemory(segmentStart, nl);
                    }
                    else
                    {
                        Carry(segmentStart, nl);
                        line = _carry.AsMemory(0, _carryLength);
                        _carryLength = 0;
                    }

                    if (line.Length > 0 && line.Span[^1] == (byte)'\r')
                    {
                        line = line[..^1];
                    }

                    if (line.IsEmpty)
                    {
                        continue;
                    }

                    return new NdjsonItem(NdjsonKind.Line, line, line.Length);
                }

                int pending = _end - _start;
                if (_discarding)
                {
                    _discarded += pending;
                }
                else if ((long)_carryLength + pending > maxLineBytes)
                {
                    _discarding = true;
                    _discarded = (long)_carryLength + pending;
                    _carryLength = 0;
                }
                else
                {
                    Carry(_start, pending);
                }
            }

            _start = 0;
            _end = 0;
            int read = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (_discarding)
                {
                    long total = _discarded;
                    _discarding = false;
                    _discarded = 0;
                    return new NdjsonItem(NdjsonKind.Truncated, default, total);
                }

                if (_carryLength > 0)
                {
                    long total = _carryLength;
                    _carryLength = 0;
                    return new NdjsonItem(NdjsonKind.Truncated, default, total);
                }

                return new NdjsonItem(NdjsonKind.End, default, 0);
            }

            BytesRead += read;
            _end = read;
        }
    }

    private void Carry(int offset, int count)
    {
        int needed = _carryLength + count;
        if (needed > _carry.Length)
        {
            byte[] bigger = new byte[Math.Max(needed, Math.Min(Math.Max(_carry.Length * 2, 4096), maxLineBytes))];
            Buffer.BlockCopy(_carry, 0, bigger, 0, _carryLength);
            _carry = bigger;
        }

        Buffer.BlockCopy(_buffer, offset, _carry, _carryLength, count);
        _carryLength = needed;
    }
}
