// Ported from Krilliac/Sonder-Inference src/sampling/include/sonder/sampling/stop.hpp and src/sampling/src/stop.cpp @1c4c152 (Apache-2.0)

using System.Text;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

/// <summary>Outcome of feeding one piece: whether a stop completed and which one. The safe text is <see cref="StopSequenceMatcher.Emitted"/>.</summary>
internal readonly record struct StopCheck(bool Stopped, int StopIndex);

/// <summary>
/// Incremental, text-level stop-sequence detector (llama.cpp reverse prompts / server <c>stop</c>). Matching is byte-wise on UTF-8, so
/// a piece may end in the middle of a character and a stop may span any number of tokens. Any suffix that could still begin a stop is
/// held back, so a client never sees a partial stop. On a match, everything before the stop is emitted and the stop itself is dropped.
/// When several stops complete, the one starting earliest wins (ties: the longest).
/// </summary>
/// <remarks>
/// Buffers are reused: after warm-up <see cref="Feed(ReadOnlySpan{byte})"/> does not allocate. <see cref="Emitted"/> is valid until the
/// next call. The string overloads are conveniences that allocate.
/// </remarks>
internal sealed class StopSequenceMatcher
{
    private readonly byte[][] _stops;
    private readonly string[] _stopText;
    private byte[] _pending = new byte[64];
    private int _pendingLength;
    private byte[] _emit = new byte[64];
    private int _emitLength;

    public StopSequenceMatcher(IEnumerable<string> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        // Empty stops would match everywhere; config validation rejects them and the matcher ignores them defensively.
        _stopText = [.. stops.Where(static s => !string.IsNullOrEmpty(s))];
        _stops = new byte[_stopText.Length][];
        for (int i = 0; i < _stopText.Length; i++)
        {
            _stops[i] = Encoding.UTF8.GetBytes(_stopText[i]);
        }
    }

    public StopSequenceMatcher(IEnumerable<byte[]> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        _stops = [.. stops.Where(static s => s is { Length: > 0 }).Select(static s => (byte[])s.Clone())];
        _stopText = new string[_stops.Length];
        for (int i = 0; i < _stops.Length; i++)
        {
            _stopText[i] = Encoding.UTF8.GetString(_stops[i]);
        }
    }

    public IReadOnlyList<string> Stops => _stopText;

    public bool Stopped { get; private set; }

    /// <summary>Bytes now safe to stream, from the last <see cref="Feed(ReadOnlySpan{byte})"/> or <see cref="Flush"/>.</summary>
    public ReadOnlySpan<byte> Emitted => _emit.AsSpan(0, _emitLength);

    /// <summary>Bytes currently held back as a possible stop prefix.</summary>
    public ReadOnlySpan<byte> Pending => _pending.AsSpan(0, _pendingLength);

    public StopCheck Feed(ReadOnlySpan<byte> piece)
    {
        _emitLength = 0;
        if (Stopped)
        {
            return new StopCheck(true, 0);
        }

        EnsureCapacity(ref _pending, _pendingLength + piece.Length);
        piece.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += piece.Length;
        ReadOnlySpan<byte> pending = _pending.AsSpan(0, _pendingLength);

        // 1) Earliest complete match (ties: the longest stop).
        int bestPos = -1;
        int bestIdx = 0;
        for (int i = 0; i < _stops.Length; i++)
        {
            int pos = pending.IndexOf(_stops[i]);
            if (pos < 0)
            {
                continue;
            }

            if (bestPos < 0 || pos < bestPos || (pos == bestPos && _stops[i].Length > _stops[bestIdx].Length))
            {
                bestPos = pos;
                bestIdx = i;
            }
        }

        if (bestPos >= 0)
        {
            SetEmit(pending[..bestPos]);
            _pendingLength = 0;
            Stopped = true;
            return new StopCheck(true, bestIdx);
        }

        // 2) Hold back the longest suffix that is a proper prefix of some stop.
        int hold = 0;
        foreach (byte[] s in _stops)
        {
            int maxLen = Math.Min(s.Length - 1, pending.Length);
            for (int len = maxLen; len > hold; len--)
            {
                if (pending[^len..].SequenceEqual(s.AsSpan(0, len)))
                {
                    hold = len;
                    break;
                }
            }
        }

        int emitLen = pending.Length - hold;
        SetEmit(pending[..emitLen]);
        pending[emitLen..].CopyTo(_pending);
        _pendingLength = hold;
        return new StopCheck(false, 0);
    }

    /// <summary>Feed a piece of text. Allocates the returned string.</summary>
    public (StopCheck Check, string Emit) Feed(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        StopCheck check = Feed(Encoding.UTF8.GetBytes(piece));
        return (check, Encoding.UTF8.GetString(Emitted));
    }

    /// <summary>Release held-back text (generation ended without a stop). Also exposed as <see cref="Emitted"/>.</summary>
    public ReadOnlySpan<byte> Flush()
    {
        SetEmit(_pending.AsSpan(0, _pendingLength));
        _pendingLength = 0;
        return Emitted;
    }

    /// <summary>Release held-back text as a string. Allocates.</summary>
    public string FlushText() => Encoding.UTF8.GetString(Flush());

    public void Reset()
    {
        _pendingLength = 0;
        _emitLength = 0;
        Stopped = false;
    }

    private void SetEmit(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(ref _emit, bytes.Length);
        bytes.CopyTo(_emit);
        _emitLength = bytes.Length;
    }

    private static void EnsureCapacity(ref byte[] buffer, int needed)
    {
        if (buffer.Length < needed)
        {
            Array.Resize(ref buffer, Math.Max(needed, buffer.Length * 2));
        }
    }
}
