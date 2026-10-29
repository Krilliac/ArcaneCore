using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The trainers and vendors a bot saves for, read once from the world data: class trainers by class, Mounts trainers by race, and the
/// vendors that stock a mount (<see cref="PlayerbotMountRules.IsMountItem"/>) with each of their mounts, plus every item entry any vendor with a spawn stocks (<see cref="IsVendorStocked"/>; the auction house bot
/// never buys those at the house). One index serves every bot of
/// a world (a bot asks it; nothing is copied per bot) and is rebuilt only when the creature content, its definitions version, the NPC
/// template overrides or the spell table change. Built lazily on the first ask, on the world thread, never per think: the rows are the
/// imported <c>npc_trainer</c> / <c>npc_vendor</c> rows and the creature spawns, so no world-DB query is made.
/// </summary>
internal sealed class PlayerbotServiceIndex
{
    private const int MaxSpawnsPerSite = 8;

    /// <summary>A trainer or vendor creature template with the spawns it has on any map.</summary>
    internal sealed record Site(uint Entry, TrainerType TrainerType, byte TrainerClass, byte TrainerRace, uint FactionTemplate,
        uint FactionId, int Rows, IReadOnlyList<CreatureSpawn> Spawns);

    /// <summary>One mount a vendor stocks.</summary>
    internal sealed record MountOffer(Site Vendor, ItemTemplate Item);

    private static readonly ConditionalWeakTable<QuestNpcServices, PlayerbotServiceIndex> Cache = [];
    private static readonly Site[] None = [];

    private readonly CreatureContent _content;
    private readonly int _version;
    private readonly NpcTemplateMetadataLookup? _metadata;
    private readonly SpellStore _store;
    private readonly Dictionary<byte, Site[]> _classTrainers = [];
    private readonly Dictionary<byte, Site[]> _mountsTrainers = [];

    private readonly object _npcs;
    private readonly object _items;

    private PlayerbotServiceIndex(CreatureContent content, NpcTemplateMetadataLookup? metadata, SpellStore store, object npcs, object items)
    {
        _npcs = npcs;
        _items = items;
        _content = content;
        _version = content.DefinitionsVersion;
        _metadata = metadata;
        _store = store;
    }

    /// <summary>The vendors that stock a mount and every mount they stock, the cheapest (by the item's buy price, then entry) first.</summary>
    internal IReadOnlyList<MountOffer> MountOffers { get; private set; } = [];

    /// <summary>Distinct vendors of <see cref="MountOffers"/> (tests and inspection).</summary>
    internal int MountVendorCount { get; private set; }

    private readonly HashSet<uint> _vendorStocked = [];

    /// <summary>Whether any vendor creature template with at least one spawn stocks item <paramref name="entry"/> (any item, not only mounts).</summary>
    internal bool IsVendorStocked(uint entry) => _vendorStocked.Contains(entry);

    /// <summary>How many distinct item entries <see cref="IsVendorStocked"/> is true for (tests and inspection).</summary>
    internal int VendorStockedCount => _vendorStocked.Count;

    /// <summary>Class trainers of one class that teach something, the one with most offers first (the entry breaks ties).</summary>
    internal IReadOnlyList<Site> ClassTrainers(byte @class) => _classTrainers.TryGetValue(@class, out Site[]? sites) ? sites : None;

    /// <summary>Mounts trainers whose own race is <paramref name="race"/>, the lowest entry first.</summary>
    internal IReadOnlyList<Site> MountsTrainers(byte race) => _mountsTrainers.TryGetValue(race, out Site[]? sites) ? sites : None;

    private uint[] _rankLadder = [];

    /// <summary>
    /// The riding skill rank a riding tier gives, read from the mounts the vendors sell: the distinct skill ranks their riding-skill
    /// items ask for, ascending, so the first tier gives the lowest and each later one the next (the last repeats). No ranks at all
    /// (no riding-skill mounts): no cap.
    /// </summary>
    internal uint RankForTier(int tier) => _rankLadder.Length == 0 ? uint.MaxValue : _rankLadder[Math.Clamp(tier, 0, _rankLadder.Length - 1)];

    /// <summary>The rank the tier before <paramref name="tier"/> gives (0 for the first tier, and when the data has no ladder).</summary>
    internal uint RankFloor(int tier) => tier > 0 && _rankLadder.Length > 0 ? RankForTier(tier - 1) : 0;

    /// <summary>The races that have a Mounts trainer (tests and inspection).</summary>
    internal IEnumerable<byte> MountsTrainerRaces => _mountsTrainers.Keys;

    /// <summary>The index for the world <paramref name="services"/> serves, built on the first ask; null when the creature content or the spell table is not loaded.</summary>
    internal static PlayerbotServiceIndex? Get(WorldSession session, QuestNpcServices services, Player player)
    {
        CreatureContent? content = session.Services.GetService<CreatureWorldFeature>()?.Content;
        SpellStore? store = session.Services.GetService<SpellFeature>()?.System.Store;
        if (content is null || store is null) return null;
        var metadata = services.Deps.Creatures as NpcTemplateMetadataLookup;
        ArcaneCore.Game.Items.IItemTemplateStore items = player.Inventory.Templates;
        // The inventory's store is a live wrapper whose identity survives a reload; the store inside it is what a reload replaces.
        object itemsKey = session.Services.GetService<ArcaneCore.World.Items.ItemsFeature>()?.LoadedStore ?? (object)items;
        if (Cache.TryGetValue(services, out PlayerbotServiceIndex? known) && ReferenceEquals(known._content, content)
            && known._version == content.DefinitionsVersion && ReferenceEquals(known._metadata, metadata) && ReferenceEquals(known._store, store)
            && ReferenceEquals(known._npcs, services.Npcs) && ReferenceEquals(known._items, itemsKey))
            return known;
        var built = new PlayerbotServiceIndex(content, metadata, store, services.Npcs, itemsKey);
        built.Build(services, session.Services.GetService<QuestNpcFeature>()?.FactionTemplates, items);
        Cache.AddOrUpdate(services, built);
        return built;
    }

    private void Build(QuestNpcServices services, FactionTemplateCatalog? factions, ArcaneCore.Game.Items.IItemTemplateStore items)
    {
        var rows = new Dictionary<uint, NpcTemplateMetadata>();
        foreach (NpcTemplateMetadata row in PlayerbotTrainerDestinations.TrainerRows(_content, _metadata)) rows[row.Entry] = row;
        var classTrainers = new Dictionary<byte, List<Site>>();
        var mountsTrainers = new Dictionary<byte, List<Site>>();
        var vendors = new List<MountOffer>();
        int vendorCount = 0;
        foreach (CreatureTemplate template in _content.Templates.OrderBy(t => t.Entry))
        {
            var flags = (NpcFlags)template.NpcFlags;
            if ((flags & (NpcFlags.Trainer | NpcFlags.Vendor)) == 0) continue;
            uint factionId = factions?.Find(template.Faction)?.Faction ?? 0;
            if ((flags & NpcFlags.Trainer) != 0 && rows.GetValueOrDefault(template.Entry) is { } trainer
                && trainer.TrainerType is TrainerType.Class or TrainerType.Mounts
                && services.Npcs.TrainerSpells(template.Entry).Count > 0
                && Spawns(template.Entry) is { Length: > 0 } trainerSpawns)
            {
                var site = new Site(template.Entry, trainer.TrainerType, trainer.TrainerClass, trainer.TrainerRace, template.Faction,
                    factionId, services.Npcs.TrainerSpells(template.Entry).Count, trainerSpawns);
                Dictionary<byte, List<Site>> target = trainer.TrainerType == TrainerType.Class ? classTrainers : mountsTrainers;
                byte key = trainer.TrainerType == TrainerType.Class ? trainer.TrainerClass : trainer.TrainerRace;
                if (!target.TryGetValue(key, out List<Site>? list)) target[key] = list = [];
                list.Add(site);
            }

            if ((flags & NpcFlags.Vendor) == 0) continue;
            IReadOnlyList<VendorItem> stock = services.Npcs.VendorItems(template.Entry);
            if (stock.Count == 0 || Spawns(template.Entry) is not { Length: > 0 } vendorSpawns) continue;
            Site? vendorSite = null;
            foreach (VendorItem item in stock)
            {
                _vendorStocked.Add(item.Item);
                if (items.Find(item.Item) is not { } itemTemplate || !PlayerbotMountRules.IsMountItem(itemTemplate, _store)) continue;
                if (vendorSite is null)
                {
                    vendorSite = new Site(template.Entry, TrainerType.Class, 0, 0, template.Faction, factionId, 0, vendorSpawns);
                    vendorCount++;
                }

                vendors.Add(new MountOffer(vendorSite, itemTemplate));
            }
        }

        foreach ((byte key, List<Site> list) in classTrainers)
            _classTrainers[key] = [.. list.OrderByDescending(site => site.Rows).ThenBy(site => site.Entry)];
        foreach ((byte key, List<Site> list) in mountsTrainers)
            _mountsTrainers[key] = [.. list.OrderBy(site => site.Entry)];
        MountOffers = [.. vendors.OrderBy(offer => offer.Item.BuyPrice).ThenBy(offer => offer.Item.Entry).ThenBy(offer => offer.Vendor.Entry)];
        MountVendorCount = vendorCount;
        _rankLadder = [.. vendors.Where(offer => PlayerbotMountRules.IsRidingSkill(offer.Item.RequiredSkill)).Select(offer => offer.Item.RequiredSkillRank)
            .Distinct().Order()];
    }

    private CreatureSpawn[] Spawns(uint entry)
    {
        var spawns = new List<CreatureSpawn>();
        foreach (uint map in _content.MapsWithSpawns)
        {
            foreach (CreatureSpawn spawn in _content.GetSpawns(map, entry))
            {
                if (spawn.IsFinite() && spawns.Count < MaxSpawnsPerSite) spawns.Add(spawn);
            }
        }

        return [.. spawns];
    }

    /// <summary>The creature a trainer or vendor <paramref name="site"/> stands for at one of its spawns, as an advisory service call sees it.</summary>
    internal static NpcInfo Hint(Site site, CreatureSpawn spawn, NpcFlags flags)
        => new(ObjectGuid.WithEntry(HighGuid.Unit, site.Entry, spawn.Guid), site.Entry, spawn.Guid, flags, spawn.MapId, spawn.X, spawn.Y,
            spawn.Z, 0, true, false, false, false, 0, site.TrainerType, site.TrainerClass, site.TrainerRace, 0, site.FactionId);
}
