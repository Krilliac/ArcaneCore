using System.Collections.Concurrent;
using System.Reflection;

namespace ArcaneCore.Game.Maps;

/// <summary>The stages of <see cref="Map.Update(uint)"/> that the parallel maps phase gates one by one (updaters are gated per type).</summary>
public enum MapStage
{
    /// <summary>(1) in-world packets, players in transit, logout timers: packet handlers reach every world feature.</summary>
    Packets,

    /// <summary>Unit heartbeats (regeneration, combat timers, aura and swing timers).</summary>
    Heartbeat,

    /// <summary>Marking the active cells around players and active objects (the grid of this map only).</summary>
    ActiveCells,

    /// <summary>(2)-(4) bounding radii, visibility, values and the flush to each player.</summary>
    Output,

    /// <summary>(5) the grid lifecycle (grid loads spawn objects; grid loads and unloads change the reference counts of terrain tiles shared by every instance).</summary>
    Grid,

    /// <summary>(6) work scheduled for after the update (far teleports and other cross-map work).</summary>
    Deferred,
}

/// <summary>
/// Which map-update stages run outside the world's serial gate while maps update in parallel (docs/research/multithreading-plan-20261017.md, W3).
/// The code default is the audited classification: built-in stages by <see cref="CodeDefault"/>, updaters by their
/// <see cref="MapAccessAttribute"/> (unmarked: gated). <c>World:Parallel:Ungated</c> and <c>World:Parallel:Gated</c> (comma-separated stage
/// names or updater type names) override it at start, for experiments on a live server; <c>World:Parallel:GateAllStages</c> gates everything.
/// </summary>
public sealed class MapStageGates
{
    private readonly bool _gateAll;
    private readonly HashSet<string> _ungated;
    private readonly HashSet<string> _gated;
    private readonly bool[] _stageGated;
    private readonly ConcurrentDictionary<Type, bool> _updaterGated = new();

    public MapStageGates(ParallelMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _gateAll = options.GateAllStages;
        _ungated = Parse(options.Ungated);
        _gated = Parse(options.Gated);
        Stage[] stages = Enum.GetValues<MapStage>().Select(stage => new Stage(stage, Decide(stage.ToString(), CodeDefault(stage)))).ToArray();
        _stageGated = new bool[stages.Length];
        foreach (Stage stage in stages)
        {
            _stageGated[(int)stage.Value] = stage.Gated;
        }
    }

    private readonly record struct Stage(MapStage Value, bool Gated);

    /// <summary>The audited classification of a built-in stage.</summary>
    public static MapAccess CodeDefault(MapStage stage) => stage switch
    {
        MapStage.ActiveCells => MapAccess.MapLocal, // GridContainer.MarkActiveCells: this map's grid and active list only
        _ => MapAccess.Unclassified,
    };

    /// <summary>The classification an updater type declares (<see cref="MapAccessAttribute"/>; none: unclassified).</summary>
    public static MapAccess Declared(Type updaterType)
        => updaterType.GetCustomAttribute<MapAccessAttribute>(inherit: false)?.Access ?? MapAccess.Unclassified;

    public bool IsGated(MapStage stage) => _stageGated[(int)stage];

    public bool IsGated(IMapUpdater updater)
    {
        Type type = updater.GetType();
        return _updaterGated.TryGetValue(type, out bool gated) ? gated : _updaterGated.GetOrAdd(type, t => Decide(t.Name, Declared(t)));
    }

    /// <summary>One line for the start-up log: what runs outside the gate.</summary>
    public string Describe()
    {
        if (_gateAll)
        {
            return "every stage gated (GateAllStages)";
        }

        IEnumerable<string> stages = Enum.GetValues<MapStage>().Where(s => !_stageGated[(int)s]).Select(s => s.ToString());
        string overrides = (_ungated.Count > 0 ? " ungated by config: " + string.Join(",", _ungated) : string.Empty)
            + (_gated.Count > 0 ? " gated by config: " + string.Join(",", _gated) : string.Empty);
        return "ungated stages: " + string.Join(",", stages) + "; updaters by [MapAccess(MapLocal)]" + overrides;
    }

    private bool Decide(string name, MapAccess codeDefault)
    {
        if (_gateAll || _gated.Contains(name))
        {
            return true;
        }

        if (_ungated.Contains(name))
        {
            return false;
        }

        return codeDefault != MapAccess.MapLocal;
    }

    private static HashSet<string> Parse(string? list)
        => string.IsNullOrWhiteSpace(list)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(list.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
}
