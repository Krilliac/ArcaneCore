using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots;

/// <summary>Static ender locations for a bot's quest travel decisions. Used only on the world thread.</summary>
internal sealed class PlayerbotQuestTripFeasibility
{
    private readonly Dictionary<(uint MapId, uint QuestId), Enders> _cache = [];
    private QuestStore? _quests;
    private CreatureContent? _content;
    private int _version = -1;

    /// <summary>Number of content scans, for regression tests of the repeated-think cost.</summary>
    internal int Evaluations { get; private set; }

    /// <summary>
    /// Reject only a proven unsupported trip. Static spawn lists are cached per map and quest; player position,
    /// live spawn gates, and the current option are checked on each call. Zero disables the distance limit,
    /// but preserves the cross-continent check.
    /// </summary>
    internal bool IsInfeasible(uint mapId, float x, float y, QuestStore quests, CreatureContent content,
        uint questId, float maxDeliveryYards, Player? player = null)
    {
        if (mapId is not (0 or 1)) return false;
        if (!ReferenceEquals(_quests, quests) || !ReferenceEquals(_content, content)
            || _version != content.DefinitionsVersion)
        {
            _cache.Clear();
            _quests = quests;
            _content = content;
            _version = content.DefinitionsVersion;
        }
        if (!_cache.TryGetValue((mapId, questId), out Enders? enders))
            _cache[(mapId, questId)] = enders = Scan(mapId, quests, content, questId);
        if (!enders.Certain) return false;
        if (enders.Local.Length == 0) return enders.OtherContinent;
        if (maxDeliveryYards == 0) return false;
        float limitSquared = maxDeliveryYards * maxDeliveryYards;
        bool present = false;
        foreach (CreatureSpawn spawn in enders.Local)
        {
            if (player is not null && !PlayerbotWorldDestinations.IsPresent(player, spawn)) continue;
            present = true;
            float dx = spawn.X - x, dy = spawn.Y - y;
            if (dx * dx + dy * dy <= limitSquared) return false;
        }
        return present;
    }

    private Enders Scan(uint mapId, QuestStore quests, CreatureContent content, uint questId)
    {
        Evaluations++;
        IReadOnlyList<uint> entries = quests.CreatureEndersOf(questId);
        if (entries.Count == 0) return new Enders(false, false, []);
        List<CreatureSpawn> local = [];
        bool other = false;
        foreach (uint entry in entries)
        {
            bool known = false;
            foreach (uint spawnMap in content.MapsWithSpawns)
            {
                foreach (CreatureSpawn spawn in content.GetSpawns(spawnMap, entry))
                {
                    if (!PlayerbotWorldDestinations.IsFiniteDestination(spawn)) continue;
                    known = true;
                    if (spawnMap == mapId) local.Add(spawn);
                    else if (spawnMap is 0 or 1) other = true;
                    else return new Enders(false, false, []);
                }
            }
            if (!known) return new Enders(false, false, []);
        }
        return new Enders(true, other, [.. local]);
    }

    private sealed record Enders(bool Certain, bool OtherContinent, CreatureSpawn[] Local);
}
