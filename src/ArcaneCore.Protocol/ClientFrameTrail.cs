using System.Globalization;

namespace ArcaneCore.Protocol;

/// <summary>
/// What a connection's read loop saw before a rejected header, for the bad-header diagnostics: frames and bytes received since the
/// header cipher engaged and the last frame that parsed. Owned by one read loop (single reader): plain fields, no synchronisation.
/// </summary>
public sealed class ClientFrameTrail
{
    private bool _hasLast;
    private WorldOpcode _lastOpcode;
    private long _lastAt;

    /// <summary>Frames parsed while the header cipher was engaged.</summary>
    public long Frames { get; private set; }

    /// <summary>Header and payload bytes of those frames.</summary>
    public long Bytes { get; private set; }

    /// <summary>Note one frame whose header parsed. <paramref name="now"/> is the caller's monotonic millisecond clock.</summary>
    public void Record(WorldOpcode opcode, int frameBytes, long now, bool cipherEngaged)
    {
        if (cipherEngaged)
        {
            Frames++;
            Bytes += frameBytes;
        }

        _hasLast = true;
        _lastOpcode = opcode;
        _lastAt = now;
    }

    /// <summary>The last parsed opcode and how long ago it arrived, or "none" before the first frame.</summary>
    public string LastGood(long now)
        => _hasLast ? string.Create(CultureInfo.InvariantCulture, $"{WorldOpcodeNames.GetName(_lastOpcode)} {now - _lastAt} ms ago") : "none";
}
