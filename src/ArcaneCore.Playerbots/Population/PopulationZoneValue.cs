using ArcaneCore.Game.Entities;
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
    IReadOnlyList<uint> RepFactions);

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
        => throw new NotImplementedException();
}
