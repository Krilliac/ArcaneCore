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
    /// Reject only a proven unsupported trip. <paramref name="completed"/> false is a pickup: <paramref name="mapId"/> is the
    /// giver's map, ender spawn gates are honoured, and the bot's position is ignored. A completed quest uses the rule
    /// below; zero <paramref name="maxDeliveryYards"/> disables the distance limit.
    /// 1. Reachable now: the bot is on map 0 or 1 (on any other map the quest is feasible), some ender spawn of the quest
    ///    (present or not) is on that continent, and either the limit is zero or the bot is within it of such an ender.
    ///    Feasible.
    /// 2. Otherwise <see cref="IsStaticallyInfeasible"/> decides, ignoring the bot.
    /// </summary>
    internal bool IsInfeasible(uint mapId, float x, float y, QuestStore quests, CreatureContent content,
        uint questId, float maxDeliveryYards, Player? player = null, bool completed = false)
    {
        if (mapId is not (0 or 1)) return false;
        TripFacts facts = FactsOf(quests, content, questId);
        if (!facts.Certain) return false;
        if (!completed) return EvaluatePickup(facts, mapId, maxDeliveryYards, player);
        MapFacts here = facts.On(mapId);
        if (here.Enders.Length != 0)
        {
            if (maxDeliveryYards == 0) return false;
            float limitSquared = maxDeliveryYards * maxDeliveryYards;
            foreach (Ender ender in here.Enders)
            {
                float dx = ender.Spawn.X - x, dy = ender.Spawn.Y - y;
                if (dx * dx + dy * dy <= limitSquared) return false;
            }
        }
        return Static(facts, maxDeliveryYards);
    }

    /// <summary>
    /// Whether static content alone proves the trip unsupported, ignoring the bot, spawn gates and which map the bot is
    /// on. It needs at least one creature giver on map 0 or 1, and for every such giver map G: no ender spawn on G, or
    /// (limit above zero) every giver-to-ender distance on G beyond the limit. A quest with no creature giver on map 0
    /// or 1 is never infeasible.
    /// </summary>
    internal bool IsStaticallyInfeasible(QuestStore quests, CreatureContent content, uint questId, float maxDeliveryYards)
    {
        TripFacts facts = FactsOf(quests, content, questId);
        return facts.Certain && Static(facts, maxDeliveryYards);
    }

    private static bool Static(TripFacts facts, float maxDeliveryYards)
    {
        bool anyGiverMap = false;
        float limitSquared = maxDeliveryYards * maxDeliveryYards;
        for (uint map = 0; map <= 1; map++)
        {
            MapFacts g = facts.On(map);
            if (!g.HasGiver) continue;
            anyGiverMap = true;
            if (g.Enders.Length == 0) continue;
            if (maxDeliveryYards == 0) return false;
            foreach (Ender ender in g.Enders)
                if (ender.GiverDistanceSquared <= limitSquared) return false;
        }
        return anyGiverMap;
    }

    private static bool EvaluatePickup(TripFacts facts, uint mapId, float maxDeliveryYards, Player? player)
    {
        MapFacts map = facts.On(mapId);
        if (map.Enders.Length == 0) return facts.On(1 - mapId).Enders.Length != 0;
        if (maxDeliveryYards == 0 || !map.HasGiver) return false;
        float limitSquared = maxDeliveryYards * maxDeliveryYards;
        bool present = false;
        foreach (Ender ender in map.Enders)
        {
            if (player is not null && !PlayerbotWorldDestinations.IsPresent(player, ender.Spawn)) continue;
            present = true;
            if (ender.GiverDistanceSquared <= limitSquared) return false;
        }
        return present;
    }

    private TripFacts FactsOf(QuestStore quests, CreatureContent content, uint questId)
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
            if (!cache.Facts.TryGetValue(questId, out TripFacts? facts))
            {
                facts = Scan(quests, content, questId);
                cache.Facts.Add(questId, facts);
                Evaluations++;
            }
            return facts;
        }
    }

    private static TripFacts Scan(QuestStore quests, CreatureContent content, uint questId)
    {
        IReadOnlyList<uint> entries = quests.CreatureEndersOf(questId);
        if (entries.Count == 0) return TripFacts.Unknown;
        List<CreatureSpawn>[] givers = [[], []];
        foreach (uint entry in quests.CreatureStartersOf(questId))
            for (uint map = 0; map <= 1; map++)
                givers[map].AddRange(content.GetSpawns(map, entry).Where(PlayerbotWorldDestinations.IsFiniteDestination));
        List<Ender>[] enders = [[], []];
        foreach (uint entry in entries)
        {
            bool known = false;
            foreach (uint spawnMap in content.MapsWithSpawns)
            {
                foreach (CreatureSpawn spawn in content.GetSpawns(spawnMap, entry))
                {
                    if (!PlayerbotWorldDestinations.IsFiniteDestination(spawn)) continue;
                    known = true;
                    if (spawnMap is not (0 or 1)) return TripFacts.Unknown;
                    float nearest = float.PositiveInfinity;
                    foreach (CreatureSpawn giver in givers[spawnMap])
                    {
                        float dx = spawn.X - giver.X, dy = spawn.Y - giver.Y;
                        nearest = Math.Min(nearest, dx * dx + dy * dy);
                    }
                    enders[spawnMap].Add(new Ender(spawn, nearest));
                }
            }
            if (!known) return TripFacts.Unknown;
        }
        return new TripFacts(true, new MapFacts(givers[0].Count != 0, [.. enders[0]]),
            new MapFacts(givers[1].Count != 0, [.. enders[1]]));
    }

    private sealed class QuestCache
    {
        internal readonly ConditionalWeakTable<CreatureContent, ContentCache> Contents = new();
    }

    private sealed class ContentCache
    {
        internal readonly object Sync = new();
        internal readonly Dictionary<uint, TripFacts> Facts = [];
        internal int Version = -1;
    }

    private sealed record Ender(CreatureSpawn Spawn, float GiverDistanceSquared);

    private sealed record MapFacts(bool HasGiver, Ender[] Enders)
    {
        internal static readonly MapFacts Empty = new(false, []);
    }

    private sealed record TripFacts(bool Certain, MapFacts Map0, MapFacts Map1)
    {
        internal static readonly TripFacts Unknown = new(false, MapFacts.Empty, MapFacts.Empty);
        internal MapFacts On(uint map) => map == 0 ? Map0 : Map1;
    }
}
