using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Economy;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>A faction auctioneer in sight: who, where, which house it serves the bot with, and whether the bot may use it from where it stands.</summary>
internal sealed record PlayerbotAuctioneerSighting(NpcInfo Npc, AuctionHouseEntry House, bool Interactable)
{
    internal Vector3 Position => new(Npc.X, Npc.Y, Npc.Z);
}

/// <summary>A mailbox in sight: its guid, position and whether the bot is within interaction distance.</summary>
internal sealed record PlayerbotMailboxSighting(ObjectGuid Guid, Vector3 Position, bool Interactable);

/// <summary>
/// Where the economy bot finds a faction auction house and a mailbox: live sightings first (the bot's own visible objects, checked with the
/// same interaction rules the handlers apply), then static spawn hints of faction auctioneers on the bot's map within
/// <see cref="RadiusYards"/> (the pattern of <see cref="PlayerbotTrainerDestinations"/>). "Faction auction house" means a house other than
/// the neutral goblin one: <see cref="AuctionHouseRules.HouseFor"/> of the auctioneer's faction and the bot's team. A static hint never
/// interacts; it only says where to walk until the real creature comes into view.
/// </summary>
internal sealed class PlayerbotEconomyDestinations(WorldSession session, PlayerbotOptions options)
{
    internal const float RadiusYards = 400f;
    internal const int MaxSpawns = 128;
    internal const uint BackoffMs = 2_000;

    private readonly List<Hint> _hints = [];
    private readonly HashSet<uint> _blockedSpawns = [];
    private readonly PlayerbotEconomyWalker _walker = new(session, options);
    private uint _mapId = uint.MaxValue;
    private int _contentVersion = -1;
    private CreatureContent? _content;
    private Team _team;
    private bool _loaded;
    private uint _hintSpawn;
    private uint _backoffUntil;

    /// <summary>The entry of the auctioneer the bot walks to (0 when none).</summary>
    internal uint TargetEntry { get; private set; }

    /// <summary>Spawn hints known for the current map (tests).</summary>
    internal int HintCount => _hints.Count;

    /// <summary>The nearest faction auctioneer the bot can see, or null.</summary>
    internal PlayerbotAuctioneerSighting? FindAuctioneer(Player player, EconomyFeature economy)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(economy);
        QuestNpcFeature? npcFeature = session.Services.GetService<QuestNpcFeature>();
        if (player.Map is null || npcFeature?.Services is not { } services || services.Deps.Creatures is not { } lookup) return null;
        PlayerbotAuctioneerSighting? best = null;
        float bestDistance = float.MaxValue;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            NpcInfo? npc = lookup.Find(player, guid);
            if (npc is not { IsAlive: true, IsHostile: false, IsInCombat: false, IsNotSelectable: false } || (npc.NpcFlags & NpcFlags.Auctioneer) == 0)
                continue;
            bool interactable = services.InteractableNpc(player, guid, NpcFlags.Auctioneer) is not null;
            AuctionHouseEntry? house = interactable
                ? (session.Services.GetService<IAuctioneerAccess>() ?? new DefaultAuctioneerAccess(npcFeature, economy.Options)).FindHouse(player, guid)
                : AuctionHouseRules.HouseFor(economy.Options, npc.FactionId, player.Team);
            if (house is null || house.Id == AuctionHouseRules.NeutralHouse) continue;
            float distance = Vector3.DistanceSquared(new(player.X, player.Y, player.Z), new(npc.X, npc.Y, npc.Z));
            if (distance >= bestDistance) continue;
            best = new PlayerbotAuctioneerSighting(npc, house, interactable);
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>The nearest spawned mailbox the bot can see, or null.</summary>
    internal PlayerbotMailboxSighting? FindMailbox(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map is not { } map || session.Services.GetService<GameObjectLootFeature>()?.FindSystem(map) is not { } system) return null;
        PlayerbotMailboxSighting? best = null;
        float bestDistance = float.MaxValue;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (guid.High != HighGuid.GameObject || system.Find(guid) is not { Type: GameObjectType.Mailbox, IsSpawned: true } mailbox) continue;
            float distance = Vector3.DistanceSquared(new(player.X, player.Y, player.Z), new(mailbox.X, mailbox.Y, mailbox.Z));
            if (distance >= bestDistance) continue;
            best = new PlayerbotMailboxSighting(guid, new(mailbox.X, mailbox.Y, mailbox.Z),
                system.FindInteractable(player, guid, GameObjectType.Mailbox) is not null);
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// One walking step towards <paramref name="target"/> (a visible object); false when the bot cannot get there (no route, blocked, stuck).
    /// </summary>
    internal bool WalkTo(Player player, Vector3 target, uint elapsedMs) => _walker.Step(player, target, elapsedMs);

    /// <summary>
    /// One walking step towards the nearest faction auctioneer spawn on the bot's map that no live sighting covers yet. False when there is none
    /// within <see cref="RadiusYards"/>, every one is blocked or the bot cannot get there (the visit gives up).
    /// </summary>
    internal bool StepToHint(Player player, EconomyFeature economy, uint elapsedMs, uint now)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(economy);
        if (player.Map is not { } map || session.Services.GetService<QuestNpcFeature>() is not { Services: { } services } npcFeature) return false;
        if (_backoffUntil != 0)
        {
            if (unchecked(now - _backoffUntil) > int.MaxValue) return false;
            _backoffUntil = 0;
        }

        Refresh(player, map.MapId, economy, npcFeature, services);
        if (_hints.Count > 0 && _hints.All(hint => _blockedSpawns.Contains(hint.Spawn.Guid)))
        {
            _blockedSpawns.Clear();
            _backoffUntil = unchecked(now + BackoffMs);
            return false;
        }

        Vector3 here = new(player.X, player.Y, player.Z);
        Hint? hint = _hints.Where(candidate => !_blockedSpawns.Contains(candidate.Spawn.Guid))
            .OrderBy(candidate => Vector3.DistanceSquared(here, candidate.Position)).FirstOrDefault();
        if (hint is null) return false;
        if (_hintSpawn != hint.Spawn.Guid)
        {
            _walker.Clear();
            _hintSpawn = hint.Spawn.Guid;
        }

        TargetEntry = hint.Entry;
        if (_walker.Step(player, hint.Position, elapsedMs)) return true;
        _blockedSpawns.Add(hint.Spawn.Guid);
        _walker.Clear();
        _hintSpawn = 0;
        _backoffUntil = unchecked(now + BackoffMs);
        return false;
    }

    /// <summary>Forget the route (the visit ended or was dropped).</summary>
    internal void Clear()
    {
        _walker.Clear();
        _hintSpawn = 0;
        TargetEntry = 0;
    }

    private void Refresh(Player player, uint mapId, EconomyFeature economy, QuestNpcFeature npcFeature, QuestNpcServices services)
    {
        CreatureContent? content = session.Services.GetService<CreatureWorldFeature>()?.Content;
        if (content is null)
        {
            _hints.Clear();
            _loaded = false;
            return;
        }

        if (_loaded && _mapId == mapId && ReferenceEquals(_content, content) && _contentVersion == content.DefinitionsVersion && _team == player.Team) return;
        _mapId = mapId;
        _content = content;
        _contentVersion = content.DefinitionsVersion;
        _team = player.Team;
        _loaded = true;
        _hints.Clear();
        _blockedSpawns.Clear();
        _walker.Clear();
        FactionTemplateCatalog? factions = npcFeature.FactionTemplates;
        Vector3 here = new(player.X, player.Y, player.Z);
        foreach (CreatureTemplate template in content.Templates.Where(t => ((NpcFlags)t.NpcFlags & NpcFlags.Auctioneer) != 0).OrderBy(t => t.Entry))
        {
            if (factions?.Find(template.Faction) is not { } faction || factions.Find(player.FactionTemplate) is null) continue;
            AuctionHouseEntry? house = AuctionHouseRules.HouseFor(economy.Options, faction.Faction, player.Team);
            if (house is null || house.Id == AuctionHouseRules.NeutralHouse
                || !PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, template.Faction)) continue;
            foreach (CreatureSpawn spawn in content.GetSpawns(mapId, template.Entry).Where(IsFinite))
            {
                var position = new Vector3(spawn.X, spawn.Y, spawn.Z);
                if (Vector3.DistanceSquared(here, position) <= RadiusYards * RadiusYards)
                    _hints.Add(new Hint(template.Entry, spawn, position));
            }
        }

        _hints.Sort((a, b) => Vector3.DistanceSquared(here, a.Position).CompareTo(Vector3.DistanceSquared(here, b.Position)));
        if (_hints.Count > MaxSpawns) _hints.RemoveRange(MaxSpawns, _hints.Count - MaxSpawns);
    }

    private static bool IsFinite(CreatureSpawn spawn) => float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z);

    private sealed record Hint(uint Entry, CreatureSpawn Spawn, Vector3 Position);
}

/// <summary>
/// Walks a bot to a point with the ordinary navigation (<see cref="PlayerbotNavigation"/>): plans a route, follows it with the motion the
/// other goals use, and gives up on a target the bot stands still for 5 s on.
/// </summary>
internal sealed class PlayerbotEconomyWalker(WorldSession session, PlayerbotOptions options)
{
    private const uint StuckMs = 5_000;

    private PlayerbotRoute? _route;
    private TileWait _tileWait;
    private Vector3 _target;
    private Vector3 _last;
    private uint _stuckMs;

    /// <summary>
    /// One step towards <paramref name="target"/>; false when no route exists, the route cannot be followed or the bot is stuck. True
    /// (and standing) while a tile of the way is still being read, for <see cref="TileWait.MaxMs"/>.
    /// </summary>
    internal bool Step(Player player, Vector3 target, uint elapsedMs)
    {
        if (!float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z)) return false;
        if (_route is null || _route.Complete || Vector3.DistanceSquared(_target, target) > 1f)
        {
            PlayerbotRoute? walk = null;
            if (PlayerbotElevator.TryBegin(session, player, target, options, out walk))
            {
                Clear();
                return true;
            }
            PlanOutcome planned = walk is not null
                ? PlanOutcome.Route
                : PlayerbotNavigation.PlanToward(player, target, options, ref _tileWait, session.World.NowMs, out walk);
            _route = walk;
            // A tile of the way is still being read: not a verdict on this target. The step holds and plans again next think.
            if (planned == PlanOutcome.Pending) return true;
            if (planned != PlanOutcome.Route || _route is null)
            {
                Clear();
                return false;
            }

            _target = target;
            _last = new(player.X, player.Y, player.Z);
            _stuckMs = 0;
        }

        Vector3 position = new(player.X, player.Y, player.Z);
        _stuckMs = Vector3.DistanceSquared(position, _last) < 0.0025f ? _stuckMs + elapsedMs : 0;
        _last = position;
        if (_stuckMs >= StuckMs || !PlayerbotNavigation.TryAdvance(session, _route, options, elapsedMs, session.World.NowMs))
        {
            Clear();
            return false;
        }

        return true;
    }

    internal void Clear()
    {
        _route = null;
        _stuckMs = 0;
    }
}
