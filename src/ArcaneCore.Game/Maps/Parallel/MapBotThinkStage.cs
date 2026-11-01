using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// What runs the bots' thinks of one map inside the map's update (<see cref="MapBotThinkStage"/>). Implemented by the playerbot feature
/// (ArcaneCore.Playerbots cannot be referenced from here). <see cref="ThinkBots"/> runs on the thread that owns the map for this tick
/// (the world thread, or a map worker with World:Parallel:MapUpdateThreads above 1) and may touch only the bots whose player is on that
/// map, so a bot's state keeps a single writer; anything world-wide it reaches goes through the same gate and deferral rules as every
/// other map update (docs/research/map-local-stages-design-20261024.md, sections 3.4 and 5.7).
/// </summary>
public interface IMapBotThinker
{
    /// <summary>Think for the bots of <paramref name="map"/> within this map's share of the tick's think budget.</summary>
    void ThinkBots(Map map, uint diffMs);
}

/// <summary>
/// The bot think stage of a map update (World:Parallel:BotThinkPerMap, default off): an <see cref="IMapUpdater"/> that hands the map to an
/// <see cref="IMapBotThinker"/>. Attached with <see cref="Map.AddUpdater"/> after the map's other updaters, it runs after them and before
/// Output (Map.Update runs every updater, then the output stages). With the option off it is not attached, or, attached disabled, it does
/// nothing, so the update is today's.
/// <para>
/// Classification: no <see cref="MapAccessAttribute"/>, so it is unclassified and runs inside the serial gate while maps update in
/// parallel (a think reaches world features and the feature's own shared tables). <see cref="MapStageGates"/> keeps it gated whatever
/// <c>World:Parallel:Ungated</c> says: design section 3.4 ("Bots") requires a per-session queue of deferred bot actions, with issue
/// order kept and a re-issued pending action not queued twice, before a think may run outside the gate, and that queue does not exist.
/// </para>
/// Thread affinity: <see cref="Update"/> runs on the owner thread of the map. The instance holds no per-map state, so one instance may
/// be attached to every map. Immutable after construction.
/// </summary>
public sealed class MapBotThinkStage : IMapUpdater
{
    private readonly IMapBotThinker _thinker;
    private readonly bool _enabled;

    /// <param name="thinker">Runs the thinks of a map.</param>
    /// <param name="enabled">World:Parallel:BotThinkPerMap; false makes <see cref="Update"/> a no-op.</param>
    public MapBotThinkStage(IMapBotThinker thinker, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(thinker);
        _thinker = thinker;
        _enabled = enabled;
    }

    /// <summary>Whether this stage thinks (the option it was built with).</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Attach a stage to <paramref name="map"/> when <paramref name="options"/> asks for it; returns the stage, or null when the option
    /// is off (nothing is attached, the map update is unchanged). World thread, outside the update phase (<see cref="Map.AddUpdater"/>).
    /// </summary>
    public static MapBotThinkStage? AttachIfEnabled(Map map, ParallelMapOptions options, IMapBotThinker thinker)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.BotThinkPerMap)
        {
            return null;
        }

        var stage = new MapBotThinkStage(thinker, enabled: true);
        map.AddUpdater(stage);
        return stage;
    }

    public void Update(Map map, uint diffMs)
    {
        if (_enabled)
        {
            _thinker.ThinkBots(map, diffMs);
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }
}
