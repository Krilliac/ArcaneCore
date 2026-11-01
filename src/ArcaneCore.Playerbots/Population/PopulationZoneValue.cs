using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// A quest a creature in the zone starts. Only creature starters are counted (a quest started by a gameobject or an item is not).
/// <paramref name="RepFactions"/> are the factions of <c>RewRepFaction1..5</c> whose <c>RewRepValue</c> is above 0.
/// </summary>
internal sealed record ZoneQuestFact(uint QuestId, int MinLevel, int QuestLevel, uint RequiredRaces, uint RequiredClasses,
    IReadOnlyList<uint> RepFactions)
{
    /// <summary>Whether a giver of the quest is not hostile to the Alliance (a team cannot take a quest from a giver it must fight).</summary>
    public bool AllianceGiver { get; init; } = true;

    /// <summary>Whether a giver of the quest is not hostile to the Horde.</summary>
    public bool HordeGiver { get; init; } = true;
}

/// <summary>An on-kill reputation source in the zone: kills of it stop paying at <paramref name="MaxRank"/> (the entry's <c>MaxStanding</c>).</summary>
internal sealed record ZoneRepSource(uint FactionId, ReputationRank MaxRank);

/// <summary>
/// A piece of gear obtainable in the zone. <paramref name="FromVendor"/> false: a drop of a dungeon whose entrance lies in the zone;
/// true: sold by a creature spawned in the zone for reputation.
/// </summary>
internal sealed record ZoneGearFact(uint ItemId, uint InventoryType, int ItemLevel, int RequiredLevel, uint AllowableClass,
    uint RequiredFaction, ReputationRank RequiredRank, uint BuyPrice, bool FromVendor);

/// <summary>What the content offers in one zone, independent of any bot. Immutable once built.</summary>
internal sealed record ZoneContentFacts(uint ZoneId)
{
    /// <summary>Quests started by creatures spawned in the zone.</summary>
    public IReadOnlyList<ZoneQuestFact> Quests { get; init; } = [];

    /// <summary>On-kill reputation sources among the creatures spawned in the zone.</summary>
    public IReadOnlyList<ZoneRepSource> RepSources { get; init; } = [];

    /// <summary>Dungeon (entrance in this zone) loot and reputation vendor items in the zone.</summary>
    public IReadOnlyList<ZoneGearFact> Gear { get; init; } = [];

    /// <summary>Instance maps whose entrance trigger lies in the zone (diagnostics).</summary>
    public IReadOnlyList<uint> DungeonMaps { get; init; } = [];

    /// <summary>The template <c>MinLevel</c> of every hostile creature spawn per team, sorted ascending.</summary>
    public IReadOnlyDictionary<Team, int[]> HostileLevels { get; init; } = new Dictionary<Team, int[]>();

    /// <summary>The same for the elite spawns (template <c>Rank</c> 1 or more) only.</summary>
    public IReadOnlyDictionary<Team, int[]> EliteLevels { get; init; } = new Dictionary<Team, int[]>();

    /// <summary>Hostile spawns with skinning loot (profession materials).</summary>
    public int Skinnable { get; init; }
}

/// <summary>
/// The inputs of <see cref="PopulationZoneValue.Build"/>, all plain snapshots or delegates so a test can fake them.
/// </summary>
/// <param name="AllowedMaps">The maps zones may lie on (the population's allowed maps).</param>
/// <param name="SpawnsOnMap">The database creature spawns of a map (the creature content's spawn list).</param>
/// <param name="TemplateOf">A creature template by entry; null when unknown.</param>
/// <param name="QuestOf">A quest template by quest id; null when unknown.</param>
/// <param name="QuestStartersOf">The quest ids a creature entry starts (<c>QuestStore.StartersOf</c>).</param>
/// <param name="Loot">The loot tables: creature loot ids come from <see cref="LootContent.FindCreature"/>, rows from <see cref="LootContent.GetRows"/>.</param>
/// <param name="ItemOf">An item template by entry; null when unknown.</param>
/// <param name="VendorItemsOf">The items a creature entry sells (<c>npc_vendor</c> rows).</param>
/// <param name="AreaTriggers">Every area trigger volume (<c>areatrigger_template</c>).</param>
/// <param name="TeleportOf">The teleport target of an area trigger id; null when the trigger is not a teleport.</param>
/// <param name="MapOf">A map template by id; null when unknown.</param>
/// <param name="OnKillReputation">The <c>creature_onkill_reputation</c> rows.</param>
/// <param name="Factions">The faction templates, for the hostility rule shared with <see cref="PopulationCreatureSurvey"/>.</param>
/// <param name="Gated">The spawn guids listed under a positive game event (never counted).</param>
internal sealed record PopulationZoneValueSources(
    IReadOnlyCollection<uint> AllowedMaps,
    Func<uint, IReadOnlyList<CreatureSpawn>> SpawnsOnMap,
    Func<uint, CreatureTemplate?> TemplateOf,
    Func<uint, QuestTemplate?> QuestOf,
    Func<uint, IReadOnlyList<uint>> QuestStartersOf,
    LootContent Loot,
    Func<uint, ItemTemplate?> ItemOf,
    Func<uint, IReadOnlyList<VendorItem>> VendorItemsOf,
    IReadOnlyList<AreaTriggerTemplate> AreaTriggers,
    Func<uint, AreaTriggerTeleport?> TeleportOf,
    Func<uint, MapTemplate?> MapOf,
    IReadOnlyList<ReputationOnKillEntry> OnKillReputation,
    FactionTemplateCatalog Factions,
    IReadOnlySet<uint> Gated);

/// <summary>
/// What each zone offers a bot, read once from the loaded content (<see cref="Build"/>). Immutable after construction.
/// <para>Thread affinity: safe to read from any thread; <see cref="Build"/> is pure and runs off the world thread.</para>
/// </summary>
internal sealed class PopulationZoneValue
{
    private readonly Dictionary<uint, ZoneContentFacts> _zones;

    public static PopulationZoneValue Empty { get; } = new(new Dictionary<uint, ZoneContentFacts>());

    public PopulationZoneValue(IReadOnlyDictionary<uint, ZoneContentFacts> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _zones = new Dictionary<uint, ZoneContentFacts>(zones.Count);
        foreach (KeyValuePair<uint, ZoneContentFacts> pair in zones) _zones[pair.Key] = pair.Value;
    }

    /// <summary>The facts of the zone; null when the content has none for it.</summary>
    public ZoneContentFacts? Find(uint zoneId) => _zones.GetValueOrDefault(zoneId);

    public IReadOnlyCollection<ZoneContentFacts> Zones => _zones.Values;

    /// <summary>
    /// Builds the facts of every zone from <paramref name="sources"/>. <paramref name="zoneOf"/> maps a world point (map, x, y, z) to its
    /// zone id (0 = unknown, which drops the fact). Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static PopulationZoneValue Build(PopulationZoneValueSources sources, Func<uint, float, float, float, uint> zoneOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(zoneOf);
        ct.ThrowIfCancellationRequested();
        FactionTemplateRecord? alliance = sources.Factions.Find(PopulationCreatureSurvey.PlayerTemplateOf(Team.Alliance));
        FactionTemplateRecord? horde = sources.Factions.Find(PopulationCreatureSurvey.PlayerTemplateOf(Team.Horde));
        var onKill = new Dictionary<uint, ReputationOnKillEntry>();
        foreach (ReputationOnKillEntry entry in sources.OnKillReputation) onKill.TryAdd(entry.CreatureEntry, entry);

        var zones = new Dictionary<uint, ZoneAccumulator>();
        var entries = new Dictionary<uint, CreatureEntryFacts>();
        int seen = 0;
        foreach (uint map in sources.AllowedMaps)
        {
            ct.ThrowIfCancellationRequested();
            foreach (CreatureSpawn spawn in ByTile(sources.SpawnsOnMap(map)))
            {
                if ((++seen & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                if (sources.Gated.Contains(spawn.Guid)) continue;
                if (!entries.TryGetValue(spawn.Entry, out CreatureEntryFacts? facts)) entries[spawn.Entry] = facts = CreatureEntryFacts.Read(spawn.Entry, sources, onKill);
                int forAlliance = 0, forHorde = 0;
                bool ally = alliance is not null && PopulationCreatureSurvey.TryHostileLevel(spawn, alliance, sources.TemplateOf, sources.Factions, sources.Gated, out forAlliance);
                bool orc = horde is not null && PopulationCreatureSurvey.TryHostileLevel(spawn, horde, sources.TemplateOf, sources.Factions, sources.Gated, out forHorde);
                if (!ally && !orc && !facts.Offers) continue;
                uint zoneId = zoneOf(spawn.MapId, spawn.X, spawn.Y, spawn.Z);
                if (zoneId == 0) continue;
                ZoneAccumulator zone = ZoneAccumulator.Of(zones, zoneId);
                foreach (ZoneQuestFact quest in facts.Quests)
                    zone.AddQuest(quest);
                foreach (ZoneRepSource source in facts.RepSources)
                    zone.RepRanks[source.FactionId] = zone.RepRanks.TryGetValue(source.FactionId, out ReputationRank known) && known >= source.MaxRank ? known : source.MaxRank;
                if (facts.VendorGear.Length > 0 && zone.VendorEntries.Add(spawn.Entry))
                    foreach (ZoneGearFact gear in facts.VendorGear) zone.Gear.TryAdd((gear.ItemId, true), gear);
                if (!ally && !orc) continue;
                if (ally) zone.AddHostile(Team.Alliance, forAlliance, facts.Elite);
                if (orc) zone.AddHostile(Team.Horde, forHorde, facts.Elite);
                if (facts.Skinnable) zone.Skinnable++;
            }
        }

        AddDungeons(sources, zoneOf, zones, ct);

        var result = new Dictionary<uint, ZoneContentFacts>(zones.Count);
        foreach ((uint zoneId, ZoneAccumulator zone) in zones) result[zoneId] = zone.ToFacts(zoneId);
        return new PopulationZoneValue(result);
    }

    // Spawns ordered by map tile so a zone lookup that reads one terrain tile at a time loads each tile once.
    private static CreatureSpawn[] ByTile(IReadOnlyList<CreatureSpawn> spawns)
    {
        var ordered = new CreatureSpawn[spawns.Count];
        var keys = new int[spawns.Count];
        for (int i = 0; i < ordered.Length; i++)
        {
            ordered[i] = spawns[i];
            keys[i] = TileKey(spawns[i].X, spawns[i].Y);
        }

        Array.Sort(keys, ordered);
        return ordered;
    }

    private static int TileKey(float x, float y)
    {
        (int X, int Y) tile = TerrainTile.TileOf(x, y) ?? (-1, -1);
        return (tile.X + 1) * 1000 + tile.Y + 1;
    }

    // The 5-man dungeons whose entrance trigger lies in a zone: their rare equippable drops are gear that zone offers.
    private static void AddDungeons(PopulationZoneValueSources sources, Func<uint, float, float, float, uint> zoneOf,
        Dictionary<uint, ZoneAccumulator> zones, CancellationToken ct)
    {
        var dungeonGear = new Dictionary<uint, ZoneGearFact[]>();
        AreaTriggerTemplate[] triggers = [.. sources.AreaTriggers.Where(t => sources.AllowedMaps.Contains(t.MapId))];
        int[] keys = [.. triggers.Select(t => TileKey(t.X, t.Y))];
        Array.Sort(keys, triggers);
        foreach (AreaTriggerTemplate trigger in triggers)
        {
            ct.ThrowIfCancellationRequested();
            if (sources.TeleportOf(trigger.Id) is not { } teleport) continue;
            if (sources.MapOf(teleport.TargetMap) is not { MapType: MapType.Instance }) continue;
            uint zoneId = zoneOf(trigger.MapId, trigger.X, trigger.Y, trigger.Z);
            if (zoneId == 0) continue;
            if (!dungeonGear.TryGetValue(teleport.TargetMap, out ZoneGearFact[]? gear))
                dungeonGear[teleport.TargetMap] = gear = DungeonGear(sources, teleport.TargetMap, ct);
            ZoneAccumulator zone = ZoneAccumulator.Of(zones, zoneId);
            zone.DungeonMaps.Add(teleport.TargetMap);
            foreach (ZoneGearFact fact in gear) zone.Gear.TryAdd((fact.ItemId, false), fact);
        }
    }

    private static ZoneGearFact[] DungeonGear(PopulationZoneValueSources sources, uint map, CancellationToken ct)
    {
        var items = new Dictionary<uint, ZoneGearFact>();
        var entries = new HashSet<uint>();
        foreach (CreatureSpawn spawn in sources.SpawnsOnMap(map))
        {
            ct.ThrowIfCancellationRequested();
            if (!entries.Add(spawn.Entry) || sources.Loot.FindCreature(spawn.Entry) is not { LootId: not 0 } info) continue;
            foreach (LootStoreRow row in sources.Loot.GetRows(LootTableKind.Creature, info.LootId))
            {
                if (row.MinCountOrRef < 0)
                {
                    if (row.ChanceOrQuestChance < 0) continue;
                    foreach (LootStoreRow referenced in sources.Loot.GetRows(LootTableKind.Reference, (uint)-(long)row.MinCountOrRef))
                        AddDrop(sources, items, referenced);
                }
                else
                {
                    AddDrop(sources, items, row);
                }
            }
        }

        return [.. items.Values];
    }

    private static void AddDrop(PopulationZoneValueSources sources, Dictionary<uint, ZoneGearFact> items, LootStoreRow row)
    {
        if (row.Item == 0 || row.MinCountOrRef <= 0 || row.ChanceOrQuestChance < 0 || items.ContainsKey(row.Item)) return;
        if (sources.ItemOf(row.Item) is not { InventoryType: not 0, Quality: >= 3 } item) return;
        items[row.Item] = new ZoneGearFact(item.Entry, item.InventoryType, (int)item.ItemLevel, (int)item.RequiredLevel, item.AllowableClass,
            0, ReputationRank.Neutral, item.BuyPrice, false);
    }

    // What one creature entry contributes wherever it is spawned: the quests it starts, the reputation it pays, the gear it sells.
    private sealed class CreatureEntryFacts
    {
        public ZoneQuestFact[] Quests = [];
        public ZoneRepSource[] RepSources = [];
        public ZoneGearFact[] VendorGear = [];
        public bool Elite;
        public bool Skinnable;

        public bool Offers => Quests.Length > 0 || RepSources.Length > 0 || VendorGear.Length > 0;

        public static CreatureEntryFacts Read(uint entry, PopulationZoneValueSources sources, Dictionary<uint, ReputationOnKillEntry> onKill)
        {
            var facts = new CreatureEntryFacts();
            if (sources.TemplateOf(entry) is { } template) facts.Elite = template.Rank >= 1;
            facts.Skinnable = sources.Loot.FindCreature(entry) is { SkinningLootId: not 0 };

            bool giverHostileToAlliance = false, giverHostileToHorde = false;
            if (sources.TemplateOf(entry) is { } giver)
            {
                if (sources.Factions.Find(PopulationCreatureSurvey.PlayerTemplateOf(Team.Alliance)) is { } alliance)
                    giverHostileToAlliance = PopulationCreatureSurvey.IsHostileTo(giver, alliance, sources.Factions);
                if (sources.Factions.Find(PopulationCreatureSurvey.PlayerTemplateOf(Team.Horde)) is { } horde)
                    giverHostileToHorde = PopulationCreatureSurvey.IsHostileTo(giver, horde, sources.Factions);
            }

            var quests = new List<ZoneQuestFact>();
            foreach (uint id in sources.QuestStartersOf(entry))
            {
                if (sources.QuestOf(id) is not { } quest) continue;
                (uint Faction, int Value)[] rewards =
                [
                    (quest.RewRepFaction1, quest.RewRepValue1), (quest.RewRepFaction2, quest.RewRepValue2), (quest.RewRepFaction3, quest.RewRepValue3),
                    (quest.RewRepFaction4, quest.RewRepValue4), (quest.RewRepFaction5, quest.RewRepValue5),
                ];
                uint[] factions = [.. rewards.Where(r => r.Faction != 0 && r.Value > 0).Select(r => r.Faction).Distinct()];
                quests.Add(new ZoneQuestFact(id, quest.MinLevel, quest.QuestLevel, quest.RequiredRaces, quest.RequiredClasses, factions)
                {
                    AllianceGiver = !giverHostileToAlliance,
                    HordeGiver = !giverHostileToHorde,
                });
            }

            facts.Quests = [.. quests];

            if (onKill.TryGetValue(entry, out ReputationOnKillEntry? rep))
            {
                var paid = new List<ZoneRepSource>(2);
                if (rep.Faction1 != 0 && rep.Value1 > 0) paid.Add(new ZoneRepSource(rep.Faction1, RankOf(rep.MaxStanding1)));
                if (rep.Faction2 != 0 && rep.Value2 > 0) paid.Add(new ZoneRepSource(rep.Faction2, RankOf(rep.MaxStanding2)));
                facts.RepSources = [.. paid];
            }

            var gear = new List<ZoneGearFact>();
            foreach (VendorItem sold in sources.VendorItemsOf(entry))
            {
                if (sources.ItemOf(sold.Item) is not { RequiredReputationFaction: not 0, InventoryType: not 0, Quality: >= 2 } item) continue;
                gear.Add(new ZoneGearFact(item.Entry, item.InventoryType, (int)item.ItemLevel, (int)item.RequiredLevel, item.AllowableClass,
                    item.RequiredReputationFaction, RankOf((byte)Math.Min(item.RequiredReputationRank, 255u)), item.BuyPrice, true));
            }

            facts.VendorGear = [.. gear];
            return facts;
        }

        private static ReputationRank RankOf(byte value) => (ReputationRank)Math.Min(value, (byte)ReputationRank.Exalted);
    }

    private sealed class ZoneAccumulator
    {
        public readonly Dictionary<uint, ZoneQuestFact> Quests = [];
        public readonly Dictionary<uint, ReputationRank> RepRanks = [];
        public readonly Dictionary<(uint Item, bool Vendor), ZoneGearFact> Gear = [];
        public readonly HashSet<uint> VendorEntries = [];
        public readonly SortedSet<uint> DungeonMaps = [];
        public readonly Dictionary<Team, List<int>> Hostile = [];
        public readonly Dictionary<Team, List<int>> Elite = [];
        public int Skinnable;

        public static ZoneAccumulator Of(Dictionary<uint, ZoneAccumulator> zones, uint zoneId)
        {
            if (!zones.TryGetValue(zoneId, out ZoneAccumulator? zone)) zones[zoneId] = zone = new ZoneAccumulator();
            return zone;
        }

        // A quest several givers start is open to a team when any of them is not hostile to it.
        public void AddQuest(ZoneQuestFact quest)
        {
            Quests[quest.QuestId] = Quests.TryGetValue(quest.QuestId, out ZoneQuestFact? known)
                ? known with { AllianceGiver = known.AllianceGiver || quest.AllianceGiver, HordeGiver = known.HordeGiver || quest.HordeGiver }
                : quest;
        }

        public void AddHostile(Team team, int level, bool elite)
        {
            Append(Hostile, team, level);
            if (elite) Append(Elite, team, level);
        }

        private static void Append(Dictionary<Team, List<int>> into, Team team, int level)
        {
            if (!into.TryGetValue(team, out List<int>? list)) into[team] = list = [];
            list.Add(level);
        }

        public ZoneContentFacts ToFacts(uint zoneId) => new(zoneId)
        {
            Quests = [.. Quests.Values.OrderBy(q => q.QuestId)],
            RepSources = [.. RepRanks.OrderBy(p => p.Key).Select(p => new ZoneRepSource(p.Key, p.Value))],
            Gear = [.. Gear.Values.OrderBy(g => g.ItemId).ThenBy(g => g.FromVendor)],
            DungeonMaps = [.. DungeonMaps],
            HostileLevels = Sorted(Hostile),
            EliteLevels = Sorted(Elite),
            Skinnable = Skinnable,
        };

        private static Dictionary<Team, int[]> Sorted(Dictionary<Team, List<int>> from)
        {
            var sorted = new Dictionary<Team, int[]>(from.Count);
            foreach ((Team team, List<int> levels) in from)
            {
                int[] array = [.. levels];
                Array.Sort(array);
                sorted[team] = array;
            }

            return sorted;
        }
    }
}
