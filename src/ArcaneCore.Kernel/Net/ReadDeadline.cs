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
    private readonly CancellationTokenSource _source;
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
            _source.CancelAfter(_timeout);
        }
    }

    /// <summary>The frame is complete: stop the clock without cancelling. A no-op when disabled.</summary>
    public void Disarm()
    {
        if (Enabled)
        {
            _source.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose() => _source.Dispose();
}
