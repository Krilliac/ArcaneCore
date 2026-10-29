namespace ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;

/// <summary>
/// Several sinks behind one: every call reaches every sink (a failing sink never starves the others), and the call reports success only when all
/// of them delivered. A supervised child uses it to beat on stdout for the cluster host supervisor beside the sink the operator configured
/// (<see cref="HeartbeatPlan.Resolve"/>). Called on the watchdog thread only, like the sinks it holds; it owns them and disposes them.
/// </summary>
public sealed class CompositeHeartbeatSink(IReadOnlyList<IHeartbeatSink> sinks) : IHeartbeatSink
{
    private readonly IHeartbeatSink[] _sinks = [.. sinks];
    private string? _lastError;

    public string Description => string.Join(" + ", _sinks.Select(s => s.Description));

    public string? LastError => _lastError;

    public bool Ready(string status) => All(s => s.Ready(status));

    public bool Beat() => All(s => s.Beat());

    public bool Stopping(string status) => All(s => s.Stopping(status));

    public void Dispose()
    {
        foreach (IHeartbeatSink sink in _sinks)
        {
            sink.Dispose();
        }
    }

    private bool All(Func<IHeartbeatSink, bool> call)
    {
        bool delivered = true;
        string? error = null;
        foreach (IHeartbeatSink sink in _sinks)
        {
            if (!call(sink))
            {
                delivered = false;
                error ??= sink.LastError ?? (sink.Description + " failed");
            }
        }

        _lastError = error;
        return delivered;
    }
}
