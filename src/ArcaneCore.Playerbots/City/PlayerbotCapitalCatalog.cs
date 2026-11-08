using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.World.Playerbots;

/// <summary>The kinds of service a capital city holds for a bot (<see cref="PlayerbotCapitalCatalog"/>). A spawn can have several.</summary>
[Flags]
internal enum CapitalService
{
    None = 0,
    Auctioneer = 1,
    Banker = 2,
    Innkeeper = 4,
    ClassTrainer = 8,
    ProfessionTrainer = 16,
    Vendor = 32,
    Repair = 64,
    FlightMaster = 128,
    Mailbox = 256,
}

/// <summary>
/// One service spawn of a capital: the creature spawn (or, for <see cref="CapitalService.Mailbox"/>, a game object spawn dressed as one), the
/// services it offers, and what is needed to ask the live rules whether a bot may use it. The records are immutable and shared by every bot.
/// </summary>
/// <param name="Services">Every <see cref="CapitalService"/> the spawn offers.</param>
/// <param name="Entry">The creature (or game object) template entry.</param>
/// <param name="Spawn">The content spawn row (a game object spawn is described by the same fields).</param>
/// <param name="FactionTemplate">creature_template.faction, the key of the reaction rules (0 for a mailbox).</param>
/// <param name="FactionId">The faction template's faction id, for the trainer quote.</param>
/// <param name="TrainerClass">The class a class trainer teaches (0 otherwise).</param>
internal sealed record CapitalSpawn(CapitalService Services, uint Entry, CreatureSpawn Spawn, uint FactionTemplate, uint FactionId, byte TrainerClass)
{
    internal Vector3 Position => new(Spawn.X, Spawn.Y, Spawn.Z);
}

/// <summary>
/// One capital city: its zone and map, where its services stand, and which teams it is friendly to. Immutable.
/// </summary>
internal sealed class Capital
{
    private readonly Dictionary<CapitalService, CapitalSpawn[]> _byKind;

    internal Capital(uint zoneId, uint mapId, string name, Vector3 center, float radius, bool allianceFriendly, bool hordeFriendly,
        IEnumerable<CapitalSpawn> spawns)
    {
        ZoneId = zoneId;
        MapId = mapId;
        Name = name;
        Center = center;
        Radius = radius;
        AllianceFriendly = allianceFriendly;
        HordeFriendly = hordeFriendly;
        Spawns = [.. spawns];
        _byKind = [];
        foreach (CapitalService kind in Enum.GetValues<CapitalService>())
        {
            if (kind == CapitalService.None) continue;
            _byKind[kind] = [.. Spawns.Where(s => (s.Services & kind) != 0)];
        }

        // The anchor a trip walks to: the auctioneer or banker nearest the middle of the city (somebody always stands on walkable ground),
        // else any service spawn.
        CapitalSpawn[] hubs = [.. Of(CapitalService.Auctioneer).Concat(Of(CapitalService.Banker))];
        Anchor = (hubs.Length > 0 ? hubs : [.. Spawns]).OrderBy(s => Vector3.DistanceSquared(s.Position, center)).FirstOrDefault();
    }

    internal uint ZoneId { get; }

    internal uint MapId { get; }

    internal string Name { get; }

    /// <summary>The mean position of the city's auctioneers and bankers.</summary>
    internal Vector3 Center { get; }

    /// <summary>The distance from <see cref="Center"/> of the farthest service spawn, held to 60..700 yards.</summary>
    internal float Radius { get; }

    internal bool AllianceFriendly { get; }

    internal bool HordeFriendly { get; }

    internal IReadOnlyList<CapitalSpawn> Spawns { get; }

    /// <summary>The spawn a trip walks to, or null for a capital with no spawn at all.</summary>
    internal CapitalSpawn? Anchor { get; }

    internal bool FriendlyTo(Team team) => team == Team.Alliance ? AllianceFriendly : HordeFriendly;

    /// <summary>The spawns that offer <paramref name="kind"/> (a single flag; empty for none).</summary>
    internal IReadOnlyList<CapitalSpawn> Of(CapitalService kind) => _byKind.TryGetValue(kind, out CapitalSpawn[]? spawns) ? spawns : [];

    /// <summary>Whether a position on <paramref name="mapId"/> is within the city's service area.</summary>
    internal bool Contains(uint mapId, Vector3 position)
        => mapId == MapId && Vector3.DistanceSquared(position, Center) <= Radius * Radius;
}

/// <summary>
/// The capital cities a bot can make a trip to, derived once from content rather than written down: a capital is a zone the area table flags
/// <see cref="AreaFlags.Capital"/> (the flag the rest rule reads, RestService.OnZoneEntered); its services are the creature spawns that stand
/// inside that zone (the position asked of the terrain, never a hard-coded coordinate) and carry a service npc flag; its mailboxes are the mailbox
/// game object spawns beside its auctioneers and bankers. The catalog is built off the world thread (<see cref="PlayerbotCapitals"/>) and is
/// immutable afterwards, so every bot reads it without a lock.
/// </summary>
internal sealed class PlayerbotCapitalCatalog
{
    /// <summary>How far from the middle of a city a service spawn is looked for (yards): the biggest capital, Stormwind, is about 600 across.</summary>
    internal const float GatherYards = 700f;

    /// <summary>A mailbox this near a city's auctioneers or bankers is one of its mailboxes.</summary>
    internal const float MailboxYards = 220f;

    private const float MinRadius = 60f;
    private const float MaxRadius = 700f;

    private const CapitalService ServiceFlags = CapitalService.Auctioneer | CapitalService.Banker | CapitalService.Innkeeper
        | CapitalService.ClassTrainer | CapitalService.ProfessionTrainer | CapitalService.Vendor | CapitalService.Repair | CapitalService.FlightMaster;

    private readonly Capital[] _capitals;

    private PlayerbotCapitalCatalog(Capital[] capitals) => _capitals = capitals;

    internal static PlayerbotCapitalCatalog Empty { get; } = new([]);

    /// <summary>A catalog of ready-made capitals (tests, and anything that knows its cities without content).</summary>
    internal static PlayerbotCapitalCatalog Of(IEnumerable<Capital> capitals) => new([.. capitals.OrderBy(c => c.ZoneId)]);

    internal IReadOnlyList<Capital> Capitals => _capitals;

    internal Capital? Find(uint zoneId) => _capitals.FirstOrDefault(c => c.ZoneId == zoneId);

    /// <summary>The capitals friendly to <paramref name="team"/>.</summary>
    internal IEnumerable<Capital> For(Team team) => _capitals.Where(c => c.FriendlyTo(team));

    /// <summary>The capital whose service area holds a position, or null.</summary>
    internal Capital? At(uint mapId, Vector3 position) => _capitals.FirstOrDefault(c => c.Contains(mapId, position));

    /// <summary>
    /// Build the catalog. <paramref name="locate"/> answers (zone, area) for a position (the WMO group it stands in, else the terrain's area flag run through the area table;
    /// (0, 0) when unknown, which leaves the spawn out). It may be slow and is called from the building thread only. Cancelled through
    /// <paramref name="cancel"/>.
    /// </summary>
    /// <param name="areas">The area table rows; the capital zones are the ones flagged <see cref="AreaFlags.Capital"/>.</param>
    /// <param name="content">The creature content (templates and spawns).</param>
    /// <param name="objects">The game object content for the mailboxes, or null for none.</param>
    /// <param name="factions">The faction templates, to judge which teams a city is friendly to; null judges by the area's team.</param>
    /// <param name="locate">(map, x, y, z) to (zone, area).</param>
    /// <param name="cancel">Stops the build between lookups.</param>
    internal static PlayerbotCapitalCatalog Build(IReadOnlyList<AreaTemplate> areas, CreatureContent content, GameObjectContent? objects,
        FactionTemplateCatalog? factions, Func<uint, float, float, float, (uint ZoneId, uint AreaId)> locate, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(locate);
        Dictionary<uint, AreaTemplate> zones = areas.Where(a => a.IsZone && (a.Flags & (uint)AreaFlags.Capital) != 0)
            .GroupBy(a => a.Entry).ToDictionary(g => g.Key, g => g.First());
        if (zones.Count == 0) return Empty;

        // A position is asked twice (as a seed, then as a member of the city): once is enough.
        var memo = new Dictionary<(uint Map, float X, float Y, float Z), (uint ZoneId, uint AreaId)>();
        (uint ZoneId, uint AreaId) Where(uint map, float x, float y, float z)
        {
            cancel.ThrowIfCancellationRequested();
            var key = (map, x, y, z);
            if (!memo.TryGetValue(key, out var at)) memo[key] = at = locate(map, x, y, z);
            return at;
        }

        Dictionary<uint, (CapitalService Services, CreatureTemplate Template)> serviceTemplates = [];
        foreach (CreatureTemplate template in content.Templates)
        {
            CapitalService services = ServicesOf(template);
            if (services != CapitalService.None) serviceTemplates[template.Entry] = (services, template);
        }

        var capitals = new List<Capital>();
        foreach (IGrouping<uint, AreaTemplate> map in zones.Values.GroupBy(z => z.MapId).OrderBy(g => g.Key))
        {
            // Every service spawn of the map, once; the seeds are the ones that mark a city (an auction house, a bank, an inn).
            var onMap = new List<(CapitalService Services, CreatureTemplate Template, CreatureSpawn Spawn)>();
            foreach (CreatureSpawn spawn in content.GetSpawns(map.Key))
            {
                if (!serviceTemplates.TryGetValue(spawn.Entry, out var found)) continue;
                if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) continue;
                onMap.Add((found.Services, found.Template, spawn));
            }

            var seeds = new Dictionary<uint, List<Vector3>>();
            // In tile order, so a locator that loads a tile of model data for each lookup loads each tile once.
            foreach (var (services, _, spawn) in onMap.OrderBy(s => (int)(32 - s.Spawn.X / 533.33333f)).ThenBy(s => (int)(32 - s.Spawn.Y / 533.33333f)))
            {
                if ((services & (CapitalService.Auctioneer | CapitalService.Banker | CapitalService.Innkeeper)) == 0) continue;
                uint zone = Where(map.Key, spawn.X, spawn.Y, spawn.Z).ZoneId;
                if (!zones.ContainsKey(zone)) continue;
                if (!seeds.TryGetValue(zone, out List<Vector3>? list)) seeds[zone] = list = [];
                if ((services & (CapitalService.Auctioneer | CapitalService.Banker)) != 0) list.Add(new Vector3(spawn.X, spawn.Y, spawn.Z));
            }

            foreach ((uint zoneId, List<Vector3> hubs) in seeds.OrderBy(p => p.Key))
            {
                // A capital needs a hub (an auction house or a bank) to have a middle and a reason to visit.
                if (hubs.Count == 0 || zones[zoneId].MapId != map.Key) continue;
                Vector3 center = Vector3.Zero;
                foreach (Vector3 hub in hubs) center += hub;
                center /= hubs.Count;

                var spawns = new List<CapitalSpawn>();
                foreach (var (services, template, spawn) in onMap)
                {
                    if (Vector3.DistanceSquared(center, new Vector3(spawn.X, spawn.Y, spawn.Z)) > GatherYards * GatherYards) continue;
                    if (Where(map.Key, spawn.X, spawn.Y, spawn.Z).ZoneId != zoneId) continue;
                    spawns.Add(Describe(services, template, spawn, factions));
                }

                foreach (CapitalSpawn mailbox in Mailboxes(objects, map.Key, spawns, center))
                    spawns.Add(mailbox);

                float radius = spawns.Count == 0 ? MinRadius
                    : Math.Clamp(spawns.Max(s => Vector3.Distance(center, s.Position)), MinRadius, MaxRadius);
                (bool alliance, bool horde) = Friendly(zones[zoneId], spawns, factions);
                capitals.Add(new Capital(zoneId, map.Key, zones[zoneId].Name, center, radius, alliance, horde,
                    spawns.OrderBy(s => s.Spawn.Guid)));
            }
        }

        return capitals.Count == 0 ? Empty : new PlayerbotCapitalCatalog([.. capitals.OrderBy(c => c.ZoneId)]);
    }

    private static CapitalService ServicesOf(CreatureTemplate template)
    {
        var flags = (NpcFlags)template.NpcFlags;
        CapitalService services = CapitalService.None;
        if ((flags & NpcFlags.Auctioneer) != 0) services |= CapitalService.Auctioneer;
        if ((flags & NpcFlags.Banker) != 0) services |= CapitalService.Banker;
        if ((flags & NpcFlags.Innkeeper) != 0) services |= CapitalService.Innkeeper;
        if ((flags & NpcFlags.Vendor) != 0) services |= CapitalService.Vendor;
        if ((flags & NpcFlags.Repair) != 0) services |= CapitalService.Repair;
        if ((flags & NpcFlags.FlightMaster) != 0) services |= CapitalService.FlightMaster;
        if ((flags & NpcFlags.Trainer) != 0)
            services |= (TrainerType)template.TrainerType == TrainerType.Class ? CapitalService.ClassTrainer : CapitalService.ProfessionTrainer;
        return services & ServiceFlags;
    }

    private static CapitalSpawn Describe(CapitalService services, CreatureTemplate template, CreatureSpawn spawn, FactionTemplateCatalog? factions)
    {
        byte trainerClass = (services & CapitalService.ClassTrainer) != 0 ? template.TrainerClass : (byte)0;
        uint factionId = factions?.Find(template.Faction)?.Faction ?? 0;
        return new CapitalSpawn(services, template.Entry, spawn, template.Faction, factionId, trainerClass);
    }

    private static IEnumerable<CapitalSpawn> Mailboxes(GameObjectContent? objects, uint mapId, List<CapitalSpawn> spawns, Vector3 center)
    {
        if (objects is null) yield break;
        Vector3[] hubs = [.. spawns.Where(s => (s.Services & (CapitalService.Auctioneer | CapitalService.Banker)) != 0).Select(s => s.Position)];
        if (hubs.Length == 0) yield break;
        foreach (GameObjectSpawn spawn in objects.GetSpawns(mapId))
        {
            if (objects.FindTemplate(spawn.Entry) is not { } template || template.Type != (uint)GameObjectType.Mailbox) continue;
            var position = new Vector3(spawn.X, spawn.Y, spawn.Z);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) continue;
            if (!hubs.Any(hub => Vector3.DistanceSquared(hub, position) <= MailboxYards * MailboxYards)) continue;
            yield return new CapitalSpawn(CapitalService.Mailbox, spawn.Entry, new CreatureSpawn
            {
                Guid = spawn.Guid, Entry = spawn.Entry, MapId = spawn.MapId, X = spawn.X, Y = spawn.Y, Z = spawn.Z, Orientation = spawn.Orientation,
            }, 0, 0, 0);
        }
    }

    /// <summary>
    /// Which teams the city is friendly to: the share of its auctioneers, bankers and innkeepers that are not hostile to a representative
    /// faction template of the team (Human 1 for the Alliance, Orc 2 for the Horde) is at least half. Without those templates the area's own
    /// team decides (no team: both).
    /// </summary>
    private static (bool Alliance, bool Horde) Friendly(AreaTemplate zone, List<CapitalSpawn> spawns, FactionTemplateCatalog? factions)
    {
        FactionTemplateRecord? human = factions?.Find(1), orc = factions?.Find(2);
        CapitalSpawn[] judges = [.. spawns.Where(s => (s.Services & (CapitalService.Auctioneer | CapitalService.Banker | CapitalService.Innkeeper)) != 0
            && s.FactionTemplate != 0)];
        if (factions is not null && human is not null && orc is not null && judges.Length > 0)
        {
            return (Share(judges, factions, human), Share(judges, factions, orc));
        }

        return zone.Team switch
        {
            AreaTeams.Ally => (true, false),
            AreaTeams.Horde => (false, true),
            _ => (true, true),
        };

        static bool Share(CapitalSpawn[] judges, FactionTemplateCatalog factions, FactionTemplateRecord player)
        {
            int friendly = 0, known = 0;
            foreach (CapitalSpawn spawn in judges)
            {
                if (factions.Find(spawn.FactionTemplate) is not { } npc) continue;
                known++;
                if (!npc.IsHostileTo(player)) friendly++;
            }

            return known > 0 && friendly * 2 >= known;
        }
    }
}
