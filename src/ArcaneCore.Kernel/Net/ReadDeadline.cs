namespace ArcaneCore.Kernel.Net;

/// <summary>
/// One reusable deadline for the reads of a frame: armed when the first byte of a header has
/// arrived, disarmed when the frame is complete. A single linked <see cref="CancellationTokenSource"/>
/// is created per connection and re-armed per frame with <c>CancelAfter</c>, so a session that
/// reads thousands of frames allocates no timer or token source per frame (the previous pattern,
/// one linked source per read, did). Owned by the session's read task; not thread-safe.
/// </summary>
public sealed class ReadDeadline : IDisposable
{
    private CancellationTokenSource _source;
    private readonly CancellationToken _outer;
    private readonly TimeSpan _timeout;

    /// <param name="outer">The connection's own token; its cancellation is not a timeout.</param>
    /// <param name="timeout">The per-frame budget; zero or negative disables the deadline (the token is then <paramref name="outer"/>).</param>
    public ReadDeadline(CancellationToken outer, TimeSpan timeout)
    {
        _outer = outer;
        _timeout = timeout;
        _source = timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(outer)
            : new CancellationTokenSource(); // never cancelled; Token below hands out the outer token
    }

    /// <summary>True when the deadline is in use (a positive timeout was configured).</summary>
    public bool Enabled => _timeout > TimeSpan.Zero;

    /// <summary>The token to read with.</summary>
    public CancellationToken Token => Enabled ? _source.Token : _outer;

    /// <summary>The configured budget.</summary>
    public TimeSpan Timeout => _timeout;

    /// <summary>True after the deadline fired (and not merely because the outer token was cancelled).</summary>
    public bool Expired => Enabled && _source.IsCancellationRequested && !_outer.IsCancellationRequested;

    /// <summary>Start the clock for a frame. A no-op when disabled.</summary>
    public void Arm()
    {
        if (Enabled)
        {
            // A timer that fired after the previous frame's last read (before or after Disarm) left the source cancelled for good;
            // CancelAfter on a cancelled source does nothing, so this frame would start already expired.
            ReplaceIfTimerFired();
            _source.CancelAfter(_timeout);
        }
    }

    /// <summary>The frame is complete: stop the clock without cancelling. A no-op when disabled.</summary>
    public void Disarm()
    {
        if (Enabled)
        {
            _source.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
            ReplaceIfTimerFired();
        }
    }

    /// <summary>
    /// When the timer (not the outer token) cancelled the source, swap in a fresh linked source so the cancellation cannot carry into
    /// the next frame and <see cref="Expired"/> reads false for a frame that completed. Only the owning read task calls this, with no read
    /// pending on the old token (every read of the frame has completed), so nothing observes the disposed source. A pending
    /// read that captured the old token is unaffected by the swap.
    /// </summary>
    private void ReplaceIfTimerFired()
    {
        if (_source.IsCancellationRequested && !_outer.IsCancellationRequested)
        {
            CancellationTokenSource fresh = CancellationTokenSource.CreateLinkedTokenSource(_outer);
            _source.Dispose();
            _source = fresh;
        }
    }

    public void Dispose() => _source.Dispose();
}
