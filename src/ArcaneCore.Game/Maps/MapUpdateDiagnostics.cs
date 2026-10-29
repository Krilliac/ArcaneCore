using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArcaneCore.Game.Maps.Collision;

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

    /// <summary>Marking the cells around players and active objects (<see cref="Map.UpdatesActiveCellsOnly"/>), between the heartbeat and the first updater.</summary>
    internal long ActiveCellsMicros { get; private set; }

    /// <summary>Part of <see cref="HeartbeatMicros"/> spent in player heartbeats (bots are players too); the rest is creatures, pets and other units.</summary>
    internal long HeartbeatPlayersMicros => _heartbeatPlayersTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;

    private long _heartbeatPlayersTicks;

    /// <summary>Per-updater simulation time, in attach order (only filled when diagnostics are on).</summary>
    internal List<(IMapUpdater Updater, long Micros)> Updaters { get; } = [];

    // Sub-phases of the cleanup: the grid state machine (unloads), the deferred actions. The terrain clean-up is the world's, once per
    // tick (TerrainManager.Update), and counts in the tick's maps phase, not in any one map's update.
    internal long GridMicros { get; private set; }
    internal long DeferredMicros { get; private set; }

    /// <summary>Grid lifecycle work during this map update (deltas of the map's <see cref="Grid.GridContainer"/> counters).</summary>
    internal Grid.GridLifecycleCounters Grids { get; set; }

    private PathSearchTotals _tilesBefore;

    /// <summary>
    /// Navigation tile installations the world thread made during this map update: on-demand path-query reads and grid tile loads
    /// (deltas of <see cref="PathSearchCounters"/>; set by <see cref="Complete"/>).
    /// </summary>
    internal PathSearchTotals Tiles { get; private set; }

    /// <summary>The slowest of those installations (default when none).</summary>
    internal TileLoadDetail SlowestTile { get; private set; }

    /// <summary>Forget the previous update's values so one instance can serve every update of a map.</summary>
    internal void Reset()
    {
        SimulationMicros = VisibilityMicros = ValuesMicros = FlushMicros = CleanupMicros = 0;
        PacketsMicros = HeartbeatMicros = ActiveCellsMicros = GridMicros = DeferredMicros = 0;
        _heartbeatPlayersTicks = 0;
        Players = MovedObjects = ChangedObjects = NewObjects = 0;
        VisibilityCandidates = 0;
        Completed = false;
        Updaters.Clear();
        Grids = default;
        Tiles = default;
        SlowestTile = default;
    }

    /// <summary>Remember the world thread's tile counters at the start of the update (a few loads of thread-static fields).</summary>
    internal void BeginTiles()
    {
        _tilesBefore = PathSearchCounters.Current;
        PathSearchCounters.ResetSlowestSyncTile();
    }

    internal void Begin()
    {
        _phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _subStart = _phaseStart;
    }

    /// <summary>Add one player's heartbeat time (Stopwatch ticks, summed and converted to microseconds once so no per-player truncation is lost); called only while diagnostics are on.</summary>
    internal void AddPlayerHeartbeat(long stopwatchTicks) => _heartbeatPlayersTicks += stopwatchTicks;

    internal void EndPackets() => PacketsMicros = Sub();
    internal void EndHeartbeat() => HeartbeatMicros = Sub();
    internal void EndActiveCells() => ActiveCellsMicros = Sub();
    internal void EndUpdater(IMapUpdater updater) => Updaters.Add((updater, Sub()));
    internal void EndSimulation() { SimulationMicros = ElapsedMicros(); Begin(); }
    internal void EndVisibility() { VisibilityMicros = ElapsedMicros(); Begin(); }
    internal void EndValues() { ValuesMicros = ElapsedMicros(); Begin(); }
    internal void EndFlush() { FlushMicros = ElapsedMicros(); Begin(); }
    internal void EndGrid() => GridMicros = Sub();
    internal void EndDeferred() => DeferredMicros = Sub();
    internal void Complete()
    {
        CleanupMicros = ElapsedMicros();
        Tiles = PathSearchCounters.Current - _tilesBefore;
        SlowestTile = PathSearchCounters.SlowestSyncTile;
        Completed = true;
    }

    /// <summary>A simulation sub-phase at least this long (µs) is named as dominant in <see cref="Describe"/>.</summary>
    internal const long DominantMicros = 500_000;

    /// <summary>How many of the slowest updaters <see cref="Describe"/> lists.</summary>
    internal const int TopUpdaters = 3;

    /// <summary>
    /// The sub-phase breakdown of this update for the slow-map-update line: simulation split into packets, heartbeats, active cells and
    /// the slowest updaters (the slowest with its own breakdown when it gives one, <see cref="IMapUpdateBreakdown"/>), the cleanup split,
    /// grid lifecycle work and the world thread's navigation tile installations. Allocates; call only when the line is logged.
    /// </summary>
    internal string Describe()
    {
        var sb = new StringBuilder(256);
        sb.Append("simulation: packets ").Append(Ms(PacketsMicros))
            .Append(", heartbeat ").Append(Ms(HeartbeatMicros))
            .Append(", active cells ").Append(Ms(ActiveCellsMicros));

        (IMapUpdater Updater, long Micros)[] ranked = [.. Updaters.OrderByDescending(u => u.Micros).Take(TopUpdaters)];
        sb.Append(", updaters ");
        if (ranked.Length == 0)
        {
            sb.Append("none");
        }

        for (int i = 0; i < ranked.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ranked[i].Updater.GetType().Name).Append(' ').Append(Ms(ranked[i].Micros));
            if (i == 0 && ranked[i].Updater is IMapUpdateBreakdown breakdown && breakdown.DescribeLastUpdate() is { Length: > 0 } inner)
            {
                sb.Append(" (").Append(inner).Append(')');
            }
        }

        if (SimulationMicros >= DominantMicros)
        {
            string name = "packets";
            long top = PacketsMicros;
            if (HeartbeatMicros > top) { name = "heartbeat"; top = HeartbeatMicros; }
            if (ActiveCellsMicros > top) { name = "active cells"; top = ActiveCellsMicros; }
            if (ranked.Length > 0 && ranked[0].Micros > top) { name = ranked[0].Updater.GetType().Name; top = ranked[0].Micros; }
            sb.Append("; DOMINANT ").Append(name).Append(' ').Append(Ms(top));
        }

        sb.Append("; cleanup: grids ").Append(Ms(GridMicros))
            .Append(", deferred ").Append(Ms(DeferredMicros))
            .Append("; grid lifecycle: created ").Append(Grids.Created).Append(", loaded ").Append(Grids.Loaded)
            .Append(", unloaded ").Append(Grids.Unloaded)
            .Append(" (").Append(Ms(Grids.CreateMicros + Grids.LoadMicros + Grids.UnloadMicros)).Append(')');

        sb.Append("; navmesh tile reads on world thread: ").Append(Tiles.SyncTileReads).Append(" (")
            .Append(Tiles.SyncTileReadMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms)");
        if (Tiles.TileLoadFailures > 0)
        {
            sb.Append(", failed ").Append(Tiles.TileLoadFailures);
        }

        if (Tiles.TilesPending > 0)
        {
            sb.Append(", pending ").Append(Tiles.TilesPending);
        }

        if (SlowestTile.Ticks > 0)
        {
            sb.Append(", slowest map ").Append(SlowestTile.MapId).Append(" tile (").Append(SlowestTile.X).Append(", ").Append(SlowestTile.Y)
                .Append(") ").Append((SlowestTile.Ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture))
                .Append(" ms, ").Append(SlowestTile.PrefetchHit ? "prefetch hit" : "prefetch miss");
        }

        return sb.ToString();
    }

    private static string Ms(long micros) => (micros / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " ms";

    internal const string NamePackets = "session packets";
    internal const string NamePlayerHeartbeats = "player heartbeats";
    internal const string NameUnitHeartbeats = "creature AI and unit heartbeats";
    internal const string NameVisibility = "visibility and relocation";
    internal const string NameValues = "values updates";
    internal const string NameFlush = "flush";
    internal const string NameGrid = "grid lifecycle";
    internal const string NameDeferred = "deferred work";

    /// <summary>One named share of a map update.</summary>
    internal readonly record struct Contributor(string Name, long Micros);

    /// <summary>
    /// The <paramref name="count"/> largest sub-phases of the update, longest first (slow path only: it allocates). Names say which
    /// subsystem the time belongs to: session packets (bots send theirs through the same queue), player heartbeats, creature AI and
    /// unit heartbeats (AI timers, auras and regeneration run in a unit's heartbeat), each map updater by its subsystem, visibility
    /// and relocation, values, flush, grid lifecycle and deferred work.
    /// </summary>
    internal Contributor[] TopContributors(int count)
    {
        var all = new List<Contributor>(Updaters.Count + 9)
        {
            new(NamePackets, PacketsMicros),
            new(NamePlayerHeartbeats, HeartbeatPlayersMicros),
            new(NameUnitHeartbeats, Math.Max(0, HeartbeatMicros - HeartbeatPlayersMicros)),
            new(NameVisibility, VisibilityMicros),
            new(NameValues, ValuesMicros),
            new(NameFlush, FlushMicros),
            new(NameGrid, GridMicros),
            new(NameDeferred, DeferredMicros),
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
    internal static string SubsystemOf(IMapUpdater updater) => SubsystemOf(updater.GetType());

    /// <summary>
    /// <see cref="SubsystemOf(IMapUpdater)"/> for a type. Matches on type names, so updaters in later layers (World, packs) are labelled
    /// without a reference to them. Every <c>InstanceData</c> subclass (the per-instance boss scripts) shares one label.
    /// </summary>
    internal static string SubsystemOf(Type updaterType) => updaterType.Name switch
    {
        "CreatureMapSystem" => "creature AI (CreatureMapSystem)",
        "MapLocomotion" => "movement (MapLocomotion)",
        "MapCollision" => "collision and pathing (MapCollision)",
        "MapCombat" => "combat (MapCombat)",
        "PetMapSystem" or "MapUnitControl" => "pets (" + updaterType.Name + ")",
        "GameObjectMapSystem" => "gameobjects (GameObjectMapSystem)",
        "SpellObjectSystem" or "TrapSystem" or "TotemMapUpdater" => "spell objects (" + updaterType.Name + ")",
        "ReactiveUpdater" or "LiquidAuraInterruptUpdater" or "StealthDetectionUpdater" or "EnchantUpdater" => "spells and auras (" + updaterType.Name + ")",
        "MapDuel" => "duels (MapDuel)",
        "MapEnvironment" => "environment hazards (MapEnvironment)",
        "MapWeather" => "weather (MapWeather)",
        "ZoneAreaUpdater" => "zones and areas (ZoneAreaUpdater)",
        "FishingService" => "fishing (FishingService)",
        "LootRollManager" => "loot (LootRollManager)",
        "TradeMapUpdater" => "trade (TradeMapUpdater)",
        "TransportMapUpdater" => "transports (TransportMapUpdater)",
        "ItemMaintenanceUpdater" => "items (ItemMaintenanceUpdater)",
        "SkillPersistenceUpdater" => "skills (SkillPersistenceUpdater)",
        "QuestNpcMapUpdater" => "quest NPCs (QuestNpcMapUpdater)",
        "InstanceMapUpdater" => "instances (InstanceMapUpdater)",
        "TaxiFlightSystem" or "FlightUpdater" => "taxi flights (" + updaterType.Name + ")",
        "InstanceData" => "instance scripts (InstanceData)",
        var name => IsInstanceData(updaterType) ? "instance scripts (InstanceData)" : name,
    };

    private static bool IsInstanceData(Type type)
    {
        for (Type? t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.Name == "InstanceData")
            {
                return true;
            }
        }

        return false;
    }

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
