using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots;

/// <summary>Shared, content-version-keyed giver-to-ender facts for quest travel decisions.</summary>
internal sealed class PlayerbotQuestTripFeasibility
{
    private static readonly ConditionalWeakTable<QuestStore, QuestCache> Caches = new();

    /// <summary>Content scans performed by this caller, for regression tests.</summary>
    internal int Evaluations { get; private set; }

    /// <summary>
    /// Reject only a proven unsupported trip. Giver-to-ender distances are static; ender spawn gates and, for a
    /// completed quest, the bot's position are checked on every call. Zero disables the distance limit, while
    /// preserving the other-continent check.
    /// </summary>
    internal bool IsInfeasible(uint mapId, float x, float y, QuestStore quests, CreatureContent content,
        uint questId, float maxDeliveryYards, Player? player = null, bool completed = false)
    {
        if (mapId is not (0 or 1)) return false;
        ContentCache cache = Caches.GetValue(quests, static _ => new QuestCache()).Contents
            .GetValue(content, static _ => new ContentCache());
        TripFacts facts;
        lock (cache.Sync)
        {
            if (cache.Version != content.DefinitionsVersion)
            {
                cache.Facts.Clear();
                cache.Version = content.DefinitionsVersion;
            }
            if (!cache.Facts.TryGetValue((mapId, questId), out facts!))
            {
                facts = Scan(mapId, quests, content, questId);
                cache.Facts.Add((mapId, questId), facts);
                Evaluations++;
            }
        }
        if (!facts.Certain) return false;
        if (facts.Local.Length == 0) return facts.OtherContinent;
        if (maxDeliveryYards == 0 || !facts.HasLocalGiver) return false;
        float limitSquared = maxDeliveryYards * maxDeliveryYards;
        bool present = false;
        foreach (Ender ender in facts.Local)
        {
            if (player is not null && !PlayerbotWorldDestinations.IsPresent(player, ender.Spawn)) continue;
            present = true;
            if (ender.GiverDistanceSquared <= limitSquared) return false;
            if (completed)
            {
                float dx = ender.Spawn.X - x, dy = ender.Spawn.Y - y;
                if (dx * dx + dy * dy <= limitSquared) return false;
            }
        }
        return present;
    }

    private static TripFacts Scan(uint mapId, QuestStore quests, CreatureContent content, uint questId)
    {
        IReadOnlyList<uint> entries = quests.CreatureEndersOf(questId);
        if (entries.Count == 0) return new TripFacts(false, false, false, []);
        List<CreatureSpawn> givers = [];
        foreach (uint entry in quests.CreatureStartersOf(questId))
            givers.AddRange(content.GetSpawns(mapId, entry).Where(PlayerbotWorldDestinations.IsFiniteDestination));
        List<Ender> local = [];
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
                    if (spawnMap == mapId)
                    {
                        float nearest = float.PositiveInfinity;
                        foreach (CreatureSpawn giver in givers)
                        {
                            float dx = spawn.X - giver.X, dy = spawn.Y - giver.Y;
                            nearest = Math.Min(nearest, dx * dx + dy * dy);
                        }
                        local.Add(new Ender(spawn, nearest));
                    }
                    else if (spawnMap is 0 or 1) other = true;
                    else return new TripFacts(false, false, false, []);
                }
            }
            if (!known) return new TripFacts(false, false, false, []);
        }
        return new TripFacts(true, other, givers.Count != 0, [.. local]);
    }

    private sealed class QuestCache
    {
        internal readonly ConditionalWeakTable<CreatureContent, ContentCache> Contents = new();
    }

    private sealed class ContentCache
    {
        internal readonly object Sync = new();
        internal readonly Dictionary<(uint MapId, uint QuestId), TripFacts> Facts = [];
        internal int Version = -1;
    }

    private sealed record Ender(CreatureSpawn Spawn, float GiverDistanceSquared);
    private sealed record TripFacts(bool Certain, bool OtherContinent, bool HasLocalGiver, Ender[] Local);
}
