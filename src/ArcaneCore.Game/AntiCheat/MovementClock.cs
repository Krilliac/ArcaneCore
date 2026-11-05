namespace ArcaneCore.Game.AntiCheat;

/// <summary>
/// Translates the receive time a gateway stamped on a client packet into the worker's own monotonic millisecond clock, so the
/// movement checks judge the spacing the packets had when they reached the gateway instead of the spacing they had after the
/// gateway-worker link (batching, queueing, retransmits) reshuffled them.
/// <para>
/// The two clocks are independent 32-bit millisecond counters. The offset is estimated once, at the link handshake, from one
/// gateway reading and the worker's reading when it arrived (<see cref="Estimate"/>). That reading carries the link's one-way
/// delay at that moment; the delay is a constant shift of every translated time, and the movement checks only ever compare
/// receive times with each other, so a constant shift cancels. Only the variation after the handshake (the jitter) is removed
/// by the translation. All arithmetic wraps like the clocks do.
/// </para>
/// Immutable value; safe to read from any thread.
/// </summary>
public readonly struct MovementClock
{
    private readonly uint _offsetMs;

    private MovementClock(uint offsetMs)
    {
        _offsetMs = offsetMs;
    }

    /// <summary>The offset to add to a gateway reading to get worker time.</summary>
    public uint OffsetMs => _offsetMs;

    /// <summary>The clock for a gateway reading <paramref name="gatewayMs"/> that the worker saw arrive at <paramref name="workerMs"/>.</summary>
    public static MovementClock Estimate(uint gatewayMs, uint workerMs) => new(unchecked(workerMs - gatewayMs));

    /// <summary>
    /// The worker-clock time of a packet the gateway stamped <paramref name="gatewayMs"/>. A result ahead of
    /// <paramref name="workerNowMs"/> (the estimate overshot, or the gateway lied) is pulled back to it: a packet cannot have
    /// been received in the future.
    /// </summary>
    public uint ToWorld(uint gatewayMs, uint workerNowMs)
    {
        uint translated = unchecked(gatewayMs + _offsetMs);
        return unchecked((int)(translated - workerNowMs)) > 0 ? workerNowMs : translated;
    }
}
