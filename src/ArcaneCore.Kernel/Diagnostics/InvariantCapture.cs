namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The failures <see cref="Invariant"/> recorded in one logical call flow, from <see cref="Invariant.Capture"/>
/// until <see cref="Dispose"/>: the creating thread and every task it awaits or starts (the scope travels with
/// the <see cref="System.Threading.ExecutionContext"/>, like an <see cref="AsyncLocal{T}"/>). Failures raised
/// on other threads or in other tests are not seen, so a test can assert an exact count while xunit runs
/// unrelated test classes of the same assembly in parallel; <see cref="Invariant.FailureCount"/> and
/// <see cref="Invariant.Failures()"/> remain process-wide. Captures nest: an inner scope's failures are also
/// reported to the outer one.
/// <para>
/// Ownership and threads: created and disposed by one caller (<c>using</c>); <see cref="Failures"/> may be read
/// from any thread and returns a snapshot. Only the failure path of <see cref="Invariant"/> touches a capture,
/// the pass path never does.
/// </para>
/// </summary>
public sealed class InvariantCapture : IDisposable
{
    private readonly List<InvariantFailureEvent> _failures = [];
    private readonly InvariantCapture? _outer;
    private bool _disposed;

    internal InvariantCapture(InvariantCapture? outer) => _outer = outer;

    /// <summary>The failures recorded in this scope so far, oldest first (a snapshot).</summary>
    public IReadOnlyList<InvariantFailureEvent> Failures
    {
        get
        {
            lock (_failures)
            {
                return [.. _failures];
            }
        }
    }

    /// <summary>The outer scope this one was opened in, if any (restored on dispose).</summary>
    internal InvariantCapture? Outer => _outer;

    internal void Add(InvariantFailureEvent failure)
    {
        lock (_failures)
        {
            if (!_disposed)
            {
                _failures.Add(failure);
            }
        }

        _outer?.Add(failure);
    }

    /// <summary>End the scope: later failures in this flow go to the outer scope (or nowhere).</summary>
    public void Dispose()
    {
        lock (_failures)
        {
            _disposed = true;
        }

        Invariant.EndCapture(this);
    }
}

/// <summary>One recorded failure, as seen by an <see cref="InvariantCapture"/>.</summary>
/// <param name="Kind"><c>Assert</c> or <c>Check</c>.</param>
/// <param name="Member">The calling member.</param>
/// <param name="File">The calling file's name (no directory).</param>
/// <param name="Line">The calling line.</param>
/// <param name="Message">The formatted message.</param>
public sealed record InvariantFailureEvent(string Kind, string Member, string File, int Line, string Message);
