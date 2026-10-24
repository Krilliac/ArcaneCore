namespace ArcaneCore.Atlas.Insight;

/// <summary>Re-runs commands on live frames at a bounded rate. Stub.</summary>
internal sealed class WatchService
{
    /// <summary>Runs that finished.</summary>
    public long Runs => throw new NotImplementedException();

    /// <summary>Frames that arrived while a watch was not due.</summary>
    public long Throttled => throw new NotImplementedException();

    /// <summary>Frames that arrived while the previous run of a watch was still going.</summary>
    public long SkippedBusy => throw new NotImplementedException();

    /// <summary>Watches that exist.</summary>
    public int Count => throw new NotImplementedException();
}
