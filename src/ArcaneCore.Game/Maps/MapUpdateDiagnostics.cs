namespace ArcaneCore.Game.Maps;

/// <summary>Optional per-map slow-update evidence; allocated only on the enabled warning path.</summary>
internal sealed class MapUpdateDiagnostics
{
    private long _phaseStart;

    internal long SimulationMicros { get; private set; }
    internal long VisibilityMicros { get; private set; }
    internal long ValuesMicros { get; private set; }
    internal long FlushMicros { get; private set; }
    internal long CleanupMicros { get; private set; }
    internal int Players { get; set; }
    internal int MovedObjects { get; set; }
    internal int ChangedObjects { get; set; }
    internal int NewObjects { get; set; }

    /// <summary>Candidates the visibility phase evaluated (players' and moved objects' passes together).</summary>
    internal long VisibilityCandidates { get; set; }
    internal bool Completed { get; private set; }

    internal void Begin() => _phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
    internal void EndSimulation() { SimulationMicros = ElapsedMicros(); Begin(); }
    internal void EndVisibility() { VisibilityMicros = ElapsedMicros(); Begin(); }
    internal void EndValues() { ValuesMicros = ElapsedMicros(); Begin(); }
    internal void EndFlush() { FlushMicros = ElapsedMicros(); Begin(); }
    internal void Complete() { CleanupMicros = ElapsedMicros(); Completed = true; }

    private long ElapsedMicros()
        => (System.Diagnostics.Stopwatch.GetTimestamp() - _phaseStart) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
}
