using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>What a riding trip walks to.</summary>
internal enum PlayerbotRidingTripKind : byte { None, Trainer, Vendor }

/// <summary>
/// The walk to a riding trainer of the bot's race, then to a mount vendor (the pattern of <see cref="PlayerbotTrainerDestinations"/>: a
/// remote spawn index filtered by trainer type and race and by the faction check, planned leg by leg with the stuck clock and a set-aside
/// list). Once the NPC is in sight the ordinary visit (<see cref="PlayerbotTownGoals"/>) buys the riding spell or the mount.
/// A trip is wanted only when the bot's budget says its spendable money covers it (<see cref="PlayerbotBudget.TrainingDue"/>,
/// <see cref="PlayerbotBudget.MountPurchaseDue"/>). Owned by one brain; world thread.
/// </summary>
internal sealed class PlayerbotRidingTrips(WorldSession session, PlayerbotOptions options)
{
    internal const uint StuckMs = 5_000;
    internal const uint BackoffMs = 30_000;
    private const uint IndexMs = 60_000;

    private readonly List<(uint Entry, CreatureSpawn Spawn)> _sites = [];
    private readonly HashSet<uint> _blocked = [];
    private TileWait _tileWait;
    private PlayerbotRoute? _route;
    private uint _routeSpawn;
    private float _best = float.PositiveInfinity;
    private uint _stuckMs;
    private uint _backoffUntil;
    private PlayerbotRidingTripKind _kind;
    private uint _mapId = uint.MaxValue;
    private uint _indexUntil;
    private uint _vendorEntry;

    internal PlayerbotBudget? Budget { get; set; }
    internal PlayerbotSuspensions? Suspensions { get; set; }
    internal uint TargetEntry { get; private set; }
    internal PlayerbotRidingTripKind Kind => _kind;

    private bool Eligible(Player player)
        => options.Enabled && options.Mounts.Enabled && Budget is not null && player.IsInWorld && player.IsAlive && player.Map is not null
            && (player.Flags & PlayerFlags.Ghost) == 0 && !player.Combat.IsInCombat
            && session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is not
                { State: SpellCastState.Preparing or SpellCastState.Casting };

    private PlayerbotRidingTripKind Wanted(Player player)
        => Budget is not { } budget ? PlayerbotRidingTripKind.None
            : budget.TrainingDue(player) ? PlayerbotRidingTripKind.Trainer
            : budget.MountPurchaseDue(player) ? PlayerbotRidingTripKind.Vendor : PlayerbotRidingTripKind.None;

    /// <summary>A riding trip is worth making: the budget says so, the trip is not resting, and a site is known on the bot's map.</summary>
    internal bool HasCandidate(Player player)
    {
        if (!Eligible(player) || Resting() || Wanted(player) == PlayerbotRidingTripKind.None) return false;
        return session.Services.GetService<QuestNpcFeature>()?.Services is { } services && Refresh(player, services) && _sites.Count > 0;
    }

    private (uint Entry, CreatureSpawn Spawn) CurrentSite()
    {
        for (int i = 0; i < _sites.Count; i++)
        {
            if (_sites[i].Spawn.Guid == _routeSpawn) return _sites[i];
        }

        return default;
    }

    private bool Resting() => _backoffUntil != 0 && unchecked((int)(session.World.NowMs - _backoffUntil)) < 0;

    /// <summary>One step of the walk; true when a movement step was attempted.</summary>
    internal bool Update(Player player, uint elapsedMs)
    {
        if (elapsedMs == 0 || !Eligible(player) || Resting() || session.Services.GetService<QuestNpcFeature>()?.Services is not { } services) return false;
        PlayerbotRidingTripKind wanted = Wanted(player);
        if (wanted == PlayerbotRidingTripKind.None) { Clear(); TargetEntry = 0; return false; }
        if (!Refresh(player, services)) return false;
        uint now = session.World.NowMs;
        Vector3 here = new(player.X, player.Y, player.Z);
        if (_routeSpawn != 0 && CurrentSite() is { Spawn: not null } current)
        {
            Vector3 at = new(current.Spawn.X, current.Spawn.Y, current.Spawn.Z);
            float left = Vector3.Distance(here, at);
            if (left < _best - 0.5f) { _best = left; _stuckMs = 0; } else if (!PlayerbotMount.IsBusy(session)) _stuckMs += elapsedMs;
            if (_stuckMs < StuckMs && Suspensions?.IsEntrySuspended(current.Entry, now) != true)
            {
                TargetEntry = current.Entry;
                if (_route is null || _route.Complete || _route.Points.Count == 0 || Vector3.Distance(here, _route.Points[^1]) < 1f)
                {
                    PlanOutcome next = PlayerbotNavigation.PlanToward(player, at, options, ref _tileWait, now, out PlayerbotRoute? leg);
                    _route = leg;
                    if (next == PlanOutcome.Pending) { _stuckMs = _stuckMs > elapsedMs ? _stuckMs - elapsedMs : 0; return true; }
                }

                if (_route is not null && PlayerbotNavigation.TryAdvance(session, _route, options, elapsedMs, now)) return true;
                _route = null;
                return false;
            }

            _blocked.Add(_routeSpawn);
            NoteSetAside(current.Entry);
            Clear();
        }

        int planned = 0;
        foreach ((uint entry, CreatureSpawn spawn) in _sites.Where(s => !_blocked.Contains(s.Spawn.Guid))
                     .OrderBy(s => Vector3.DistanceSquared(here, new Vector3(s.Spawn.X, s.Spawn.Y, s.Spawn.Z))).ToArray())
        {
            if (Suspensions?.IsEntrySuspended(entry, now) == true) continue;
            if (++planned > PlayerbotTownGoals.MaxRepairPlansPerThink) return true;
            PlanOutcome outcome = PlayerbotNavigation.PlanToward(player, new Vector3(spawn.X, spawn.Y, spawn.Z), options, ref _tileWait, now,
                out PlayerbotRoute? route);
            if (outcome == PlanOutcome.Pending) return true;
            if (outcome == PlanOutcome.Refused || route is null) { _blocked.Add(spawn.Guid); NoteSetAside(entry); continue; }
            _route = route;
            _routeSpawn = spawn.Guid;
            _best = Vector3.Distance(here, new Vector3(spawn.X, spawn.Y, spawn.Z));
            _stuckMs = 0;
            TargetEntry = entry;
            if (PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, now)) return true;
            _blocked.Add(spawn.Guid);
            NoteSetAside(entry);
            Clear();
        }

        Clear();
        _blocked.Clear();
        TargetEntry = 0;
        _backoffUntil = Math.Max(1u, unchecked(now + BackoffMs));
        if (!session.IsInChaosProbe)
        {
            PlayerbotAnomalies.Report(player, "mount", "riding-refused",
                $"riding {wanted.ToString().ToLowerInvariant()} trip refused or stalled ({_sites.Count} site(s) on map {player.Map?.MapId ?? player.MapId})");
            if (wanted == PlayerbotRidingTripKind.Trainer)
                PlayerbotAnomalies.Report(player, "progression", "train-failed",
                    $"riding trainer trip refused or stalled ({_sites.Count} site(s) on map {player.Map?.MapId ?? player.MapId})");
        }
        return false;
    }

    /// <summary>Builds the trainer or vendor spawns of the bot's map the faction check lets it approach; rebuilt when the kind or map changes, or every minute.</summary>
    private bool Refresh(Player player, QuestNpcServices services)
    {
        if (player.Map is not { } playerMap || Budget is not { } budget) return false;
        PlayerbotRidingTripKind wanted = Wanted(player);
        uint map = playerMap.MapId;
        uint now = session.World.NowMs;
        uint vendor = budget.Riding.MountVendor;
        if (wanted == _kind && map == _mapId && vendor == _vendorEntry && unchecked((int)(now - _indexUntil)) < 0) return true;
        PlayerbotServiceIndex? index = PlayerbotServiceIndex.Get(session, services, player);
        if (index is null) return false;
        _kind = wanted; _mapId = map; _vendorEntry = vendor; _indexUntil = unchecked(now + IndexMs);
        Clear();
        _sites.Clear();
        _blocked.Clear();
        FactionTemplateCatalog? factions = session.Services.GetService<QuestNpcFeature>()?.FactionTemplates;
        IEnumerable<PlayerbotServiceIndex.Site> sites = wanted switch
        {
            PlayerbotRidingTripKind.Trainer => index.MountsTrainers((byte)player.Race),
            PlayerbotRidingTripKind.Vendor => index.MountOffers.Where(o => o.Vendor.Entry == vendor)
                .Select(o => o.Vendor).DistinctBy(v => v.Entry),
            PlayerbotRidingTripKind.None => [],
            _ => [],
        };
        foreach (PlayerbotServiceIndex.Site site in sites)
        {
            if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, site.FactionTemplate)) continue;
            foreach (CreatureSpawn spawn in site.Spawns.Where(s => s.MapId == map)) _sites.Add((site.Entry, spawn));
        }

        return true;
    }

    /// <summary>A mount vendor was set aside: the budget counts it, and after a few the plan prices another vendor instead of retrying this one for ever.</summary>
    private void NoteSetAside(uint entry)
    {
        if (_kind == PlayerbotRidingTripKind.Vendor) Budget?.NoteVendorFailure(entry);
    }

    private void Clear() { _route = null; _routeSpawn = 0; _stuckMs = 0; _best = float.PositiveInfinity; }
}
