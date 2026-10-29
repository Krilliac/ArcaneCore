using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.EventScenarios;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>One placed festival NPC of a running festival, with what decides whether it is an anchor (the world-wide facts; the team's come later).</summary>
internal sealed class PlayerbotFestivalSpawn(CreatureSpawn spawn, uint faction, PlayerbotAnchorFacts facts, PlayerbotAnchorKind kind, ushort root)
{
    internal CreatureSpawn Spawn { get; } = spawn;

    internal uint Faction { get; } = faction;

    /// <summary>The template's own facts (flags, selectable, behaviour); the rest start at their qualifying defaults.</summary>
    internal PlayerbotAnchorFacts Facts { get; } = facts;

    internal PlayerbotAnchorKind Kind { get; } = kind;

    internal ushort Root { get; } = root;

    internal Vector3 Position => new(Spawn.X, Spawn.Y, Spawn.Z);

    internal uint? Zone { get; set; }
}

/// <summary>A festival that runs now: its root event, holiday, flavour and the placed NPCs of the root and of its running members.</summary>
internal sealed class PlayerbotFestival(ushort root, uint holidayId, string description, PlayerbotFestivalFlavour flavour, IReadOnlyList<PlayerbotFestivalSpawn> spawns)
{
    internal ushort Root { get; } = root;

    internal uint HolidayId { get; } = holidayId;

    internal string Description { get; } = description;

    internal PlayerbotFestivalFlavour Flavour { get; } = flavour;

    internal IReadOnlyList<PlayerbotFestivalSpawn> Spawns { get; } = spawns;
}

/// <summary>
/// The world's picture of the festivals (docs/areas/playbots-social.md, World events): which festivals run, their anchor NPCs, the places
/// bots hold around them (<see cref="Claims"/>), and the one clock and day count the bots and the events lane share. One per world, owned by
/// <see cref="ManagedPlayerbotFeature"/>; the game-event listener it registers marks the picture stale only when a festival event, or an
/// event linked to one, starts or stops, so the hourly bells and the day and night events cost nothing.
/// <para>
/// Layers, each built on first use after a change and shared by every bot: (1) the running festivals and their placed NPCs with the
/// world-wide anchor facts; (2) per town and team, the anchors a bot of that team may use (present, inside the home, within the radius,
/// approachable, outside enforced-PvP zones and scenario areas). A per-bot read is a key comparison and two dictionary lookups.
/// </para>
/// Thread affinity: world thread only (the listener is told on the world thread).
/// </summary>
internal sealed class PlayerbotWorldEventBoard(
    IServiceProvider services, PlayerbotOptions options, Func<long> clock, Func<long> unixSeconds, Func<uint, Vector3, uint> zoneAt,
    Func<PlayerbotFestivalFlavours> flavours) : IGameEventListener, IDisposable
{
    /// <summary>The most placed NPCs a festival keeps per map.</summary>
    internal const int MaxSpawnsPerMap = 512;

    private static readonly IReadOnlyList<PlayerbotFestivalAnchor> NoAnchors = [];
    private static readonly IReadOnlyList<(uint Map, Vector3 Centre, float Radius)> NoAreas = [];

    private readonly Dictionary<ushort, PlayerbotFestival> _festivals = [];
    private readonly Dictionary<(string Town, uint MapId, uint Team), TownAnchors> _towns = [];
    private readonly Dictionary<(string Town, ulong Anchor), bool> _unreachable = [];
    private readonly Dictionary<(string Town, uint Team), long> _townAside = [];
    private readonly Dictionary<ulong, long> _reported = [];
    private IDisposable? _registration;
    private WorldRuntime? _world;
    private int _stamp;
    private int _version;
    private BuildKey _built;
    private bool _hasBuilt;
    private IReadOnlySet<ushort> _excluded = new HashSet<ushort>();
    private string _excludedText = string.Empty;
    private IReadOnlyList<(uint Map, Vector3 Centre, float Radius)> _areas = NoAreas;

    private readonly record struct BuildKey(GameEventService? Service, bool Initialised, int Stamp, CreatureContent? Content, int ContentVersion, string Excluded, int Areas);

    private sealed record TownAnchors(Vector3 Centre, float Radius, float SpotRadius, IReadOnlyList<PlayerbotFestivalAnchor> Anchors);

    /// <summary>The places bots hold at the anchors.</summary>
    internal PlayerbotFestivalClaims Claims { get; } = new();

    /// <summary>World milliseconds on the clock the bots and the events lane share (the social feature's).</summary>
    internal long NowMs => clock();

    /// <summary>Unix seconds (UTC), for the celebrate roll's day.</summary>
    internal long UnixSeconds => unixSeconds();

    /// <summary>The live options.</summary>
    internal PlayerbotOptions Options => options;

    /// <summary>The zone of a point (0 when the terrain knows none).</summary>
    internal uint ZoneAt(uint mapId, Vector3 position) => zoneAt(mapId, position);

    /// <summary>Start hearing the game events of <paramref name="world"/>. Safe to call again (the earlier registration is replaced). World thread.</summary>
    internal void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _registration?.Dispose();
        _registration = services.GetService<GameEventFeature>()?.AddListener(this);
        _stamp++;
    }

    /// <summary>Stop hearing the game events and forget the picture and every claim.</summary>
    public void Dispose()
    {
        _registration?.Dispose();
        _registration = null;
        _festivals.Clear();
        _towns.Clear();
        _unreachable.Clear();
        _townAside.Clear();
        _reported.Clear();
        Claims.Clear();
        _hasBuilt = false;
    }

    /// <summary>
    /// A game event started or stopped: the picture is stale when it is a festival event (a root or an event linked to one), whether or not it
    /// is running; any other event changes nothing here.
    /// </summary>
    public void OnEventChanged(ushort eventId, bool active, bool resume)
    {
        GameEventService? service = services.GetService<GameEventFeature>()?.Service;
        if (service is null) return;
        ParseExcluded();
        if (PlayerbotHolidayCatalog.RootOf(eventId, service.Find, _excluded) is not null) _stamp++;
    }

    /// <summary>Whether <paramref name="eventId"/> is itself a festival root (a holiday event that is not excluded): the events whose start is remarked on.</summary>
    internal bool IsFestivalRoot(ushort eventId)
    {
        GameEventService? service = services.GetService<GameEventFeature>()?.Service;
        if (service?.Find(eventId) is not { } definition) return false;
        ParseExcluded();
        return PlayerbotHolidayCatalog.IsRoot(definition, _excluded);
    }

    /// <summary>Bump the picture's stamp (a reload of the options that name excluded events does it by value; tests).</summary>
    internal void Invalidate() => _stamp++;

    // --- the running festivals ----------------------------------------------------------------------------------------

    /// <summary>A number that changes whenever the picture of the festivals was rebuilt (a bot that decided on an older one decides again).</summary>
    internal int Version
    {
        get
        {
            Refresh();
            return _version;
        }
    }

    /// <summary>The festivals that run now, in no particular order.</summary>
    internal IReadOnlyCollection<PlayerbotFestival> Festivals()
    {
        Refresh();
        return _festivals.Values;
    }

    /// <summary>The running festival whose root event is <paramref name="root"/>, or null.</summary>
    internal PlayerbotFestival? FestivalOf(ushort root)
    {
        Refresh();
        return _festivals.GetValueOrDefault(root);
    }

    /// <summary>Whether any festival runs.</summary>
    internal bool AnyFestival()
    {
        Refresh();
        return _festivals.Count > 0;
    }

    /// <summary>
    /// Whether the festival <paramref name="root"/> has an anchor NPC of its own in <paramref name="zone"/> of <paramref name="mapId"/>
    /// (every placed NPC that talks, gives quests or sells, selectable and no guard, whatever the team and the home). The zone of an NPC
    /// is looked up when first needed and kept.
    /// </summary>
    internal bool HasAnchorInZone(ushort root, uint mapId, uint zone)
    {
        if (FestivalOf(root) is not { } festival) return false;
        foreach (PlayerbotFestivalSpawn spawn in festival.Spawns)
        {
            if (spawn.Spawn.MapId != mapId) continue;
            spawn.Zone ??= zoneAt(mapId, spawn.Position);
            if (spawn.Zone == zone) return true;
        }

        return false;
    }

    private void ParseExcluded()
    {
        string text = options.Social.Events.Holidays.ExcludedEvents ?? string.Empty;
        if (string.Equals(text, _excludedText, StringComparison.Ordinal)) return;
        _excludedText = text;
        (IReadOnlySet<ushort> ids, string? error) = PlayerbotHolidayOptions.ParseExcludedEvents(text);
        _excluded = error is null ? ids : new HashSet<ushort>();
    }

    private void Refresh()
    {
        ParseExcluded();
        GameEventService? service = services.GetService<GameEventFeature>()?.Service;
        CreatureContent? content = services.GetService<CreatureWorldFeature>()?.Content;
        EventScenarioEngine? engine = services.GetService<PlayerbotEventScenarioFeature>()?.Engine;
        var key = new BuildKey(service, service?.IsInitialised == true, _stamp, content, content?.DefinitionsVersion ?? 0, _excludedText, engine?.AreasVersion ?? 0);
        if (_hasBuilt && key == _built) return;
        _built = key;
        _hasBuilt = true;
        _areas = engine?.ActiveAreas() ?? NoAreas;
        _version++;
        Rebuild(service, content);
    }

    private void Rebuild(GameEventService? service, CreatureContent? content)
    {
        _festivals.Clear();
        _towns.Clear();
        _unreachable.Clear();
        if (service is not { IsInitialised: true } || content is null) return;
        HashSet<ushort> running = [.. service.ActiveEvents];
        if (running.Count == 0) return;
        IReadOnlyDictionary<ushort, ushort> members = PlayerbotHolidayCatalog.ActiveMembers(service.Events, running, _excluded);
        if (members.Count == 0) return;

        // The creature guids each festival spawns while it runs: the positive game_event_creature rows of its root and running members.
        var wanted = new Dictionary<uint, ushort>();
        foreach ((ushort id, ushort root) in members)
            if (service.Rows.Creatures.TryGetValue(id, out IReadOnlyList<uint>? guids))
                foreach (uint guid in guids) wanted.TryAdd(guid, root);
        var spawnsByRoot = new Dictionary<ushort, List<PlayerbotFestivalSpawn>>();
        foreach (ushort root in members.Values.Distinct()) spawnsByRoot[root] = [];
        if (wanted.Count > 0)
        {
            var perMap = new Dictionary<(ushort Root, uint Map), int>();
            foreach (uint map in content.MapsWithSpawns)
            {
                foreach (CreatureSpawn spawn in content.GetSpawns(map))
                {
                    if (!wanted.TryGetValue(spawn.Guid, out ushort root) || !float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) continue;
                    if (content.FindTemplate(spawn.Entry) is not { } template) continue;
                    var facts = new PlayerbotAnchorFacts
                    {
                        NpcFlags = (NpcFlags)template.NpcFlags,
                        NotSelectable = (template.UnitFlags & (uint)UnitFlags.NotSelectable) != 0,
                        Behaviour = template.Behaviour,
                    };
                    if (PlayerbotFestivalRules.AnchorKind(facts, float.PositiveInfinity) is not { } kind) continue;
                    int count = perMap.GetValueOrDefault((root, map));
                    if (count >= MaxSpawnsPerMap) continue;
                    perMap[(root, map)] = count + 1;
                    spawnsByRoot[root].Add(new PlayerbotFestivalSpawn(spawn, template.Faction, facts, kind, root));
                }
            }
        }

        foreach ((ushort root, List<PlayerbotFestivalSpawn> spawns) in spawnsByRoot)
        {
            if (service.Find(root) is not { } definition) continue;
            spawns.Sort(static (a, b) => a.Spawn.Guid.CompareTo(b.Spawn.Guid));
            _festivals[root] = new PlayerbotFestival(root, definition.HolidayId, definition.Description,
                flavours().For(definition.HolidayId, definition.Description), spawns);
        }
    }

    // --- anchors per town and team -------------------------------------------------------------------------------------

    /// <summary>
    /// The anchors a bot of <paramref name="witness"/>'s team may gather around in <paramref name="town"/>: the placed NPCs of the festivals
    /// that gather, present, inside the home, within <c>SpotRadiusYards</c> of its centre, approachable by the team, in a zone where PvP is not
    /// enforced on it and outside the scenario areas. Built once per town and team after a change, from the first requesting bot (the
    /// witness lends its map, faction and team), and shared by every bot of that town and team. Empty when no festival runs.
    /// </summary>
    internal IReadOnlyList<PlayerbotFestivalAnchor> AnchorsFor(PlayerbotHomeArea town, Player witness)
    {
        ArgumentNullException.ThrowIfNull(town);
        ArgumentNullException.ThrowIfNull(witness);
        Refresh();
        if (_festivals.Count == 0 || town.Place is not { Length: > 0 } place) return NoAnchors;
        float spotRadius = options.Social.Events.Holidays.SpotRadiusYards;
        (string, uint, uint) key = (place, town.MapId, (uint)witness.Team);
        if (_towns.TryGetValue(key, out TownAnchors? known) && known.Centre == town.Centre && known.Radius == town.Radius && known.SpotRadius == spotRadius) return known.Anchors;

        var anchors = new List<PlayerbotFestivalAnchor>();
        WorldStateHooks hooks = WorldStateHooks.For(_world ?? throw new InvalidOperationException("the event board is not attached"));
        foreach (PlayerbotFestival festival in _festivals.Values)
        {
            if (!festival.Flavour.Gather) continue;
            foreach (PlayerbotFestivalSpawn spawn in festival.Spawns)
            {
                if (spawn.Spawn.MapId != town.MapId) continue;
                PlayerbotAnchorFacts facts = spawn.Facts with
                {
                    Present = PlayerbotWorldDestinations.IsPresent(witness, spawn.Spawn),
                    InsideHome = town.Contains(spawn.Spawn),
                    DistanceFromTownYards = town.DistanceTo(spawn.Spawn.X, spawn.Spawn.Y),
                    Approachable = ApproachableBy(witness, spawn.Faction),
                    PvpEnforced = PvpEnforced(hooks, spawn, witness.Team),
                    InsideScenarioArea = InScenarioArea(spawn),
                };
                if (PlayerbotFestivalRules.AnchorKind(facts, spotRadius) is { } kind)
                    anchors.Add(new PlayerbotFestivalAnchor(spawn.Spawn.Guid, spawn.Spawn.Entry, spawn.Position, kind, spawn.Root));
            }
        }

        anchors.Sort(static (a, b) => a.Guid.CompareTo(b.Guid));
        _towns[key] = new TownAnchors(town.Centre, town.Radius, spotRadius, anchors);
        return anchors;
    }

    /// <summary>
    /// Whether the player's team may walk up to an NPC of this faction template. Without faction data for either side nothing says the NPC
    /// is hostile, so it counts as approachable; with data, the trainer approach rule decides (a hostile or unfriendly reaction refuses).
    /// </summary>
    private bool ApproachableBy(Player witness, uint faction)
    {
        QuestNpcFeature? quests = services.GetService<QuestNpcFeature>();
        FactionTemplateCatalog? factions = quests?.FactionTemplates;
        if (quests?.Services is not { } npcServices || factions is null || factions.Find(faction) is null || factions.Find(witness.FactionTemplate) is null) return true;
        return PlayerbotTrainerDestinations.CanApproachTrainer(witness, npcServices, factions, faction);
    }

    private bool PvpEnforced(WorldStateHooks hooks, PlayerbotFestivalSpawn spawn, Team team)
    {
        spawn.Zone ??= zoneAt(spawn.Spawn.MapId, spawn.Position);
        if (spawn.Zone is not { } zone || zone == 0 || hooks.Locator.Find(zone) is not { } area) return false;
        return PvpAreaRules.IsEnforced(area.Team, (AreaFlags)area.Flags, team, hooks.Zones.PvpRealmMode, inBattleground: false);
    }

    private bool InScenarioArea(PlayerbotFestivalSpawn spawn)
    {
        foreach ((uint map, Vector3 centre, float radius) in _areas)
        {
            if (map != spawn.Spawn.MapId) continue;
            float dx = spawn.Spawn.X - centre.X, dy = spawn.Spawn.Y - centre.Y;
            if ((dx * dx) + (dy * dy) <= radius * radius) return true;
        }

        return false;
    }

    // --- one-time path probe, aside lists, reports ----------------------------------------------------------------------

    /// <summary>
    /// Whether the walk to an anchor in <paramref name="town"/> was found impossible by <paramref name="probe"/>; the answer is kept for the
    /// board's life (until the next rebuild), so the path query runs once per town and anchor.
    /// </summary>
    internal bool IsUnreachable(string town, ulong anchor, Func<bool> probe)
    {
        if (_unreachable.TryGetValue((town, anchor), out bool known)) return known;
        bool result = probe();
        _unreachable[(town, anchor)] = result;
        return result;
    }

    /// <summary>Set the festival of <paramref name="town"/> aside for <paramref name="team"/> until <paramref name="untilMs"/> (a crowd that was attacked disperses).</summary>
    internal void SetTownAside(string town, uint team, long untilMs) => _townAside[(town, team)] = untilMs;

    /// <summary>Whether the festival of <paramref name="town"/> is set aside for <paramref name="team"/> at <paramref name="nowMs"/>.</summary>
    internal bool IsTownAside(string town, uint team, long nowMs)
    {
        if (_townAside.Count == 0 || !_townAside.TryGetValue((town, team), out long until)) return false;
        if (until > nowMs) return true;
        _townAside.Remove((town, team));
        return false;
    }

    /// <summary>Whether a report about <paramref name="anchor"/> is due (once per anchor per hour); the caller reports and the answer is then remembered.</summary>
    internal bool TakeReportSlot(ulong anchor, long nowMs)
    {
        if (_reported.TryGetValue(anchor, out long last) && nowMs - last < 3_600_000) return false;
        if (_reported.Count >= 4096) _reported.Clear();
        _reported[anchor] = nowMs;
        return true;
    }
}
