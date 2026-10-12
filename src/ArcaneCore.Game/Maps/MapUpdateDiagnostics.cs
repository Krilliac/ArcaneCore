namespace ArcaneCore.Game.Maps;

/// <summary>Optional per-map slow-update evidence; allocated only on the enabled warning path (or by a benchmark observer).</summary>
internal sealed class MapUpdateDiagnostics
{
    private long _phaseStart;
    private long _subStart;

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

    // Sub-phases of the simulation: in-world packets (and transit/logout timers), unit heartbeats, each map updater.
    internal long PacketsMicros { get; private set; }
    internal long HeartbeatMicros { get; private set; }

    /// <summary>Part of <see cref="HeartbeatMicros"/> spent in player heartbeats (bots are players too); the rest is creatures, pets and other units.</summary>
    internal long HeartbeatPlayersMicros { get; private set; }

    /// <summary>Per-updater simulation time, in attach order (only filled when diagnostics are on).</summary>
    internal List<(IMapUpdater Updater, long Micros)> Updaters { get; } = [];

    // Sub-phases of the cleanup: the grid state machine (unloads), the terrain clean-up, the deferred actions.
    internal long GridMicros { get; private set; }
    internal long TerrainMicros { get; private set; }
    internal long DeferredMicros { get; private set; }

    /// <summary>Grid lifecycle work during this map update (deltas of the map's <see cref="Grid.GridContainer"/> counters).</summary>
    internal Grid.GridLifecycleCounters Grids { get; set; }

    /// <summary>Forget the previous update's values so one instance can serve every update of a map.</summary>
    internal void Reset()
    {
        SimulationMicros = VisibilityMicros = ValuesMicros = FlushMicros = CleanupMicros = 0;
        PacketsMicros = HeartbeatMicros = HeartbeatPlayersMicros = GridMicros = TerrainMicros = DeferredMicros = 0;
        Players = MovedObjects = ChangedObjects = NewObjects = 0;
        VisibilityCandidates = 0;
        Completed = false;
        Updaters.Clear();
        Grids = default;
    }

    internal void Begin()
    {
        _phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _subStart = _phaseStart;
    }

    /// <summary>Add one player's heartbeat time (Stopwatch ticks); called only while diagnostics are on.</summary>
    internal void AddPlayerHeartbeat(long stopwatchTicks)
        => HeartbeatPlayersMicros += stopwatchTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;

    internal void EndPackets() => PacketsMicros = Sub();
    internal void EndHeartbeat() => HeartbeatMicros = Sub();
    internal void EndUpdater(IMapUpdater updater) => Updaters.Add((updater, Sub()));
    internal void EndSimulation() { SimulationMicros = ElapsedMicros(); Begin(); }
    internal void EndVisibility() { VisibilityMicros = ElapsedMicros(); Begin(); }
    internal void EndValues() { ValuesMicros = ElapsedMicros(); Begin(); }
    internal void EndFlush() { FlushMicros = ElapsedMicros(); Begin(); }
    internal void EndGrid() => GridMicros = Sub();
    internal void EndTerrain() => TerrainMicros = Sub();
    internal void EndDeferred() => DeferredMicros = Sub();
    internal void Complete() { CleanupMicros = ElapsedMicros(); Completed = true; }

    /// <summary>One named share of a map update.</summary>
    internal readonly record struct Contributor(string Name, long Micros);

    /// <summary>
    /// The <paramref name="count"/> largest sub-phases of the update, longest first (slow path only: it allocates). Names say which
    /// subsystem the time belongs to: session packets (bots send theirs through the same queue), player heartbeats, creature AI and
    /// unit heartbeats (AI timers, auras and regeneration run in a unit's heartbeat), each map updater by its subsystem, visibility
    /// and relocation, values, flush, grid lifecycle, terrain and deferred work.
    /// </summary>
    internal Contributor[] TopContributors(int count)
    {
        var all = new List<Contributor>(Updaters.Count + 10)
        {
            new("session packets", PacketsMicros),
            new("player heartbeats", HeartbeatPlayersMicros),
            new("creature AI and unit heartbeats", Math.Max(0, HeartbeatMicros - HeartbeatPlayersMicros)),
            new("visibility and relocation", VisibilityMicros),
            new("values updates", ValuesMicros),
            new("flush", FlushMicros),
            new("grid lifecycle", GridMicros),
            new("terrain cleanup", TerrainMicros),
            new("deferred work", DeferredMicros),
        };
        foreach ((IMapUpdater updater, long micros) in Updaters)
        {
            all.Add(new Contributor(SubsystemOf(updater), micros));
        }

        // Several updaters can share a subsystem label; sum them so the ranking is by subsystem.
        var merged = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Contributor c in all)
        {
            merged[c.Name] = merged.GetValueOrDefault(c.Name) + c.Micros;
        }

        return [.. merged.Select(kv => new Contributor(kv.Key, kv.Value))
            .Where(c => c.Micros > 0)
            .OrderByDescending(c => c.Micros)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, count))];
    }

    /// <summary>The subsystem an updater belongs to, by type name (the type name itself when it has no friendlier label).</summary>
    internal static string SubsystemOf(IMapUpdater updater) => updater.GetType().Name switch
    {
        "CreatureMapSystem" => "creature AI (CreatureMapSystem)",
        "MapLocomotion" => "movement (MapLocomotion)",
        "MapCollision" => "collision and pathing (MapCollision)",
        "MapCombat" => "combat (MapCombat)",
        "PetMapSystem" or "MapUnitControl" => "pets (" + updater.GetType().Name + ")",
        "GameObjectMapSystem" => "gameobjects (GameObjectMapSystem)",
        "SpellObjectSystem" or "TrapSystem" or "TotemMapUpdater" => "spell objects (" + updater.GetType().Name + ")",
        "ReactiveUpdater" or "LiquidAuraInterruptUpdater" or "StealthDetectionUpdater" or "EnchantUpdater" => "spells and auras (" + updater.GetType().Name + ")",
        var name => name,
    };

    /// <summary>"name 812 ms, name 400 ms, name 90 ms" for <see cref="TopContributors"/>; "none" when nothing was timed.</summary>
    internal string FormatTop(int count)
    {
        Contributor[] top = TopContributors(count);
        return top.Length == 0
            ? "none"
            : string.Join(", ", top.Select(c => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{c.Name} {c.Micros / 1000.0:F1} ms")));
    }

    private long Sub()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long micros = (now - _subStart) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
        _subStart = now;
        return micros;
    }

    private long ElapsedMicros()
        => (System.Diagnostics.Stopwatch.GetTimestamp() - _phaseStart) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
}
