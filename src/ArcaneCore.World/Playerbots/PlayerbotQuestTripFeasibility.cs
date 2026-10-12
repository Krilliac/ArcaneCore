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
    /// <paramref name="mapId"/> is the bot's map. For a pickup (<paramref name="completed"/> false) it is also the giver's
    /// map. For a completed quest the giver's map(s) come from content, so where the bot stands never decides
    /// "other continent": a quest whose giver and ender share a continent stays feasible after a summon or teleport.
    /// </summary>
    internal bool IsInfeasible(uint mapId, float x, float y, QuestStore quests, CreatureContent content,
        uint questId, float maxDeliveryYards, Player? player = null, bool completed = false)
    {
        if (mapId is not (0 or 1)) return false;
        if (!completed)
            return Evaluate(FactsOf(mapId, quests, content, questId), maxDeliveryYards, x, y, player, false);
        bool anyGiverMap = false;
        for (uint giverMap = 0; giverMap <= 1; giverMap++)
        {
            TripFacts facts = FactsOf(giverMap, quests, content, questId);
            if (!facts.HasLocalGiver) continue;
            anyGiverMap = true;
            // Spawn presence and the bot's position only mean something on the map the bot is standing on.
            bool here = giverMap == mapId;
            if (!Evaluate(facts, maxDeliveryYards, x, y, here ? player : null, here)) return false;
        }
        return anyGiverMap;
    }

    /// <summary>
    /// Whether the trip is unsupported by static content alone: measured from the quest giver's map(s), every ender spawn
    /// in content (present or not) is beyond the limit from the nearest giver, or the enders are all on a different
    /// continent from the giver. Independent of the bot's map and position and of which enders are currently spawned, so it
    /// is the only rule that may justify a durable refusal. Zero disables the distance limit but keeps the
    /// ender-on-another-continent-from-the-giver rule. A quest with no creature giver is never statically infeasible.
    /// </summary>
    internal bool IsStaticallyInfeasible(QuestStore quests, CreatureContent content, uint questId, float maxDeliveryYards)
    {
        bool anyGiverMap = false;
        for (uint giverMap = 0; giverMap <= 1; giverMap++)
        {
            TripFacts facts = FactsOf(giverMap, quests, content, questId);
            if (!facts.HasLocalGiver) continue;
            anyGiverMap = true;
            if (!Evaluate(facts, maxDeliveryYards, 0, 0, null, false)) return false;
        }
        return anyGiverMap;
    }

    private static bool Evaluate(TripFacts facts, float maxDeliveryYards, float x, float y, Player? player, bool completed)
    {
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

    private TripFacts FactsOf(uint mapId, QuestStore quests, CreatureContent content, uint questId)
    {
        ContentCache cache = Caches.GetValue(quests, static _ => new QuestCache()).Contents
            .GetValue(content, static _ => new ContentCache());
        lock (cache.Sync)
        {
            if (cache.Version != content.DefinitionsVersion)
            {
                cache.Facts.Clear();
                cache.Version = content.DefinitionsVersion;
            }
            if (!cache.Facts.TryGetValue((mapId, questId), out TripFacts? facts))
            {
                facts = Scan(mapId, quests, content, questId);
                cache.Facts.Add((mapId, questId), facts);
                Evaluations++;
            }
            return facts;
        }
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
