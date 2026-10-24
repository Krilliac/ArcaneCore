namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>What <see cref="MapFeedLineReader.ReadLineAsync"/> found.</summary>
internal enum LineStatus : byte
{
    /// <summary>A complete line (possibly empty).</summary>
    Line,

    /// <summary>The input ended.</summary>
    EndOfInput,

    /// <summary>A line longer than the limit began; nothing of it is returned and the reader must not be used again.</summary>
    TooLong,
}

/// <summary>The result of one read: a status and, for <see cref="LineStatus.Line"/>, the line's bytes.</summary>
internal readonly record struct LineRead(LineStatus Status, ReadOnlyMemory<byte> Line);

/// <summary>
/// Reads '\n'-terminated UTF-8 lines of at most <c>maxBytes</c> bytes (a trailing '\r' is dropped) from a stream into one fixed buffer:
/// no allocation per line, and a peer that never sends a newline costs at most <c>maxBytes + 1</c> bytes of memory before it is cut
/// off. The returned line is a view into the buffer and is valid until the next call. One reader per stream; one call at a time.
/// </summary>
internal sealed class MapFeedLineReader(Stream stream, int maxBytes)
{
    private const int ReadChunk = 1024;

    private readonly byte[] _buffer = new byte[maxBytes + 1 + ReadChunk];
    private int _start;
    private int _end;
    private int _searched;
    private readonly int _capacity = maxBytes;
    private int _limit = maxBytes;

    /// <summary>
    /// The longest line accepted now. Starts at the constructor's <c>maxBytes</c> (which sized the buffer) and may only be lowered, for a connection
    /// that learns it needs less (it bound the web role); it applies from the next line read. One reader, one caller: not thread-safe.
    /// </summary>
    public int MaxBytes
    {
        get => _limit;
        set => _limit = Math.Clamp(value, 1, _capacity);
    }

    public async ValueTask<LineRead> ReadLineAsync(CancellationToken token)
    {
        while (true)
        {
            int newline = -1;
            if (_searched < _end - _start)
            {
                int found = Array.IndexOf(_buffer, (byte)'\n', _start + _searched, _end - _start - _searched);
                if (found >= 0)
                {
                    newline = found;
                }
                else
                {
                    _searched = _end - _start;
                }
            }

            if (newline >= 0)
            {
                int length = newline - _start;
                int start = _start;
                _start = newline + 1;
                _searched = 0;
                if (length > 0 && _buffer[start + length - 1] == (byte)'\r')
                {
                    length--;
                }

                if (length > _limit)
                {
                    return new LineRead(LineStatus.TooLong, default);
                }

                return new LineRead(LineStatus.Line, new ReadOnlyMemory<byte>(_buffer, start, length));
            }

            int pending = _end - _start;
            if (pending > _limit + 1)
            {
                // One byte more than the limit may still be the '\r' of a line of exactly maxBytes.
                return new LineRead(LineStatus.TooLong, default);
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, pending);
                _start = 0;
                _end = pending;
            }

            int read = await stream.ReadAsync(_buffer.AsMemory(_end), token).ConfigureAwait(false);
            if (read == 0)
            {
                if (pending == 0)
                {
                    return new LineRead(LineStatus.EndOfInput, default);
                }

                // The peer closed in the middle of a line: serve what it sent, like a line that ended there.
                int length = pending;
                _start = _end;
                _searched = 0;
                if (_buffer[length - 1] == (byte)'\r')
                {
                    length--;
                }

                return new LineRead(LineStatus.Line, new ReadOnlyMemory<byte>(_buffer, 0, length));
            }

            _end += read;
        }
    }
}
