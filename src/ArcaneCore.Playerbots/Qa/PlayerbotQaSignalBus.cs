using System.Threading.Channels;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// The hand-over from the logging call (any thread, usually the world thread) to the QA loop: a bounded queue that drops the oldest signal
/// when full and counts it, so a flood of warnings can never grow memory or block a log call. Writers never wait and never throw.
/// Thread-safe; any number of readers (drained under the channel's own synchronisation).
/// </summary>
internal sealed class PlayerbotQaSignalBus
{
    private readonly Channel<QaSignal> _channel;
    private long _dropped;
    private long _received;

    public PlayerbotQaSignalBus(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _channel = Channel.CreateBounded<QaSignal>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Signals thrown away because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Signals offered to the queue.</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Offer a signal; false only when the queue is closed (never blocks).</summary>
    public bool TryWrite(QaSignal signal)
    {
        Interlocked.Increment(ref _received);
        return _channel.Writer.TryWrite(signal);
    }

    /// <summary>Move up to <paramref name="max"/> queued signals into <paramref name="into"/>; the number moved.</summary>
    public int Drain(List<QaSignal> into, int max)
    {
        int moved = 0;
        while (moved < max && _channel.Reader.TryRead(out QaSignal? signal))
        {
            into.Add(signal);
            moved++;
        }

        return moved;
    }
}
