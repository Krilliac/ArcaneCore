using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>Where a dead bot's body lies, seen from the ghost (<see cref="PlayerbotRecovery.Decide"/>).</summary>
internal enum PlayerbotCorpsePlace
{
    /// <summary>No body (a ghost that came back from a restart, or one whose body is gone).</summary>
    None,

    /// <summary>On the map (and instance) the ghost is on.</summary>
    ThisMap,

    /// <summary>On another map: the bot died in a dungeon and was released at a graveyard outside.</summary>
    OtherMap,
}

/// <summary>What a dead bot does next (<see cref="PlayerbotRecovery.Decide"/>).</summary>
internal enum PlayerbotRecoveryStep
{
    /// <summary>Release the spirit (CMSG_REPOP_REQUEST).</summary>
    Release,

    /// <summary>Walk back to the body on this map.</summary>
    WalkToCorpse,

    /// <summary>Walk into the entrance trigger of the dungeon the body is in; the server revives a ghost that enters it.</summary>
    WalkToEntrance,

    /// <summary>Stand at the body while the server's reclaim delay runs (SMSG_CORPSE_RECLAIM_DELAY).</summary>
    WaitForReclaimDelay,

    /// <summary>Stand within reclaim range while a hostile creature is near the ghost (where the reclaim revives it).</summary>
    WaitForHostiles,

    /// <summary>
    /// Walk to a spot inside the reclaim radius of the body that no hostile creature camps and the navigation mesh reaches, and
    /// reclaim there (<see cref="PlayerbotRecovery.FindReviveSpot"/>).
    /// </summary>
    WalkToReviveSpot,

    /// <summary>Reclaim the body (CMSG_RECLAIM_CORPSE).</summary>
    Reclaim,

    /// <summary>Give up on the body and take the spirit healer (CMSG_SPIRIT_HEALER_ACTIVATE).</summary>
    SpiritHealer,

    /// <summary>A body that cannot even be released: the recovery faults (<see cref="PlayerbotRecovery.Stalled"/>).</summary>
    Fault,
}

/// <summary>What a ghost that took the spirit-healer fallback does next (<see cref="PlayerbotRecovery.DecideSpiritHealer"/>).</summary>
internal enum PlayerbotSpiritHealerStep
{
    /// <summary>Within reach of a healer: CMSG_SPIRIT_HEALER_ACTIVATE.</summary>
    Activate,

    /// <summary>A healer is in sight: walk to it.</summary>
    WalkToHealer,

    /// <summary>No healer in sight: walk to the graveyard of this position, where one stands.</summary>
    WalkToGraveyard,

    /// <summary>No healer and no graveyard, or the fallback stalled too: the recovery faults.</summary>
    Fault,
}

/// <summary>
/// Ordinary ghost recovery for one managed player, like a client: release, walk back to the body, wait out the reclaim delay,
/// move off a camped revive point (mangoszero playerbot ReviveFromCorpseAction waits for hostiles to leave), reclaim. The revive
/// point is where the ghost stands, not the body: CMSG_RECLAIM_CORPSE resurrects the player in place (vmangos MiscHandler.cpp:599,
/// <c>ResurrectPlayer</c> without a relocation), and the walk stops as soon as the body is in reclaim range (about 39 yards). A
/// body whose surroundings are camped is no reason to give it up: anywhere inside that radius will do, so the ghost looks for a
/// spot there that is clear of hostiles and reachable (<see cref="PlayerbotRecovery.FindReviveSpot"/>), walks to it and reclaims;
/// only when there is none does it wait for the camp to leave, and take the spirit healer after <see cref="NoProgressMs"/>.
/// <list type="bullet">
/// <item><description>A body on another map — the bot died in a dungeon and was released at a graveyard outside — is reached
/// through the dungeon's entrance: the ghost walks into the area trigger whose <c>areatrigger_teleport</c> leads to the body's
/// map (or to a dungeon it is nested in), <see cref="PlayerbotAreaTriggers"/> reports it, and the server revives a ghost that
/// enters the map its body is in (vmangos Player.cpp:1953-1966, MiscHandler.cpp:712-756).</description></item>
/// <item><description>The bound is progress, not a count of thinks. When nothing moved the recovery on for <see cref="NoProgressMs"/>,
/// or a walk stopped closing on its goal (or could not be planned) for <see cref="StuckMs"/>, or there is no way back to the body,
/// the ghost takes the spirit healer instead (CMSG_SPIRIT_HEALER_ACTIVATE, vmangos NPCHandler.cpp:416): the nearest healer it can
/// see, else it walks to the graveyard of its position, where one stands. Only when that fallback stalls the same way does the
/// recovery fault (<see cref="SpiritHealerFailed"/>); a body that cannot even be released for <see cref="NoProgressMs"/> faults with
/// <see cref="Stalled"/>. Before, a stall or a stuck walk threw at once, and the fault quarantined the bot.</description></item>
/// <item><description>Waiting at the body while the reclaim delay runs (30, 60 or 120 s by recent deaths, vmangos
/// GetCorpseReclaimDelay) is progress (live, 2026-10-07: the bots that faulted were only waiting); waiting for hostiles, or walking
/// from one revive spot to the next, is not, so a body that stays camped for a minute is given up for the healer.</description></item>
/// </list>
/// </summary>
internal sealed class PlayerbotRecovery(WorldSession session, PlayerbotOptions options)
{
    internal const long NoProgressMs = 60_000;
    internal const long StuckMs = 10_000;

    /// <summary>
    /// A hostile creature this close to the ghost keeps it from reclaiming its body (just beyond the 20-yard same-level aggro
    /// radius): the reclaim revives the bot where the ghost stands.
    /// </summary>
    internal const float HostileClearYards = 25f;

    /// <summary>
    /// A revive spot is at least this far from every hostile creature: <see cref="HostileClearYards"/> and a margin, so a bot
    /// that stops a yard or two short of it still passes the camped-body rule.
    /// </summary>
    internal const float ReviveSpotClearYards = HostileClearYards + 2.5f;

    /// <summary>A revive spot is at most this far (3D) from the body: the reclaim radius less a margin for the walk's last yards.</summary>
    internal const float ReviveSpotMaxYards = CombatConstants.CorpseReclaimRadius - 3f;

    /// <summary>A walk to a revive spot longer than this (route length) is not worth it: the camp may have moved by then.</summary>
    internal const float ReviveSpotMaxWalkYards = 90f;

    /// <summary>The spacing of the candidate rings round the body, and the number of candidates on each.</summary>
    internal const float ReviveSpotRingYards = 3f;
    internal const int ReviveSpotAngles = 24;

    /// <summary>Revive spots whose route is asked of the navigation mesh in one search (nearest first).</summary>
    internal const int ReviveSpotMaxQueries = 16;

    /// <summary>The ghost activates a healer from this close: inside the server's interaction range with a margin.</summary>
    internal const float SpiritHealerReachYards = 3f;

    /// <summary>The fault of a body that could not even be released.</summary>
    internal const string Stalled = "playerbot-recovery-stalled";

    /// <summary>The fault of a ghost that could reach neither its body nor a spirit healer.</summary>
    internal const string SpiritHealerFailed = "playerbot-recovery-spirit-healer-failed";

    private PlayerbotRoute? _route;
    private object? _goal;
    private long _progressMs = -1;
    private long _closingMs = -1;
    private float _bestDistance = float.PositiveInfinity;
    private bool _spiritHealer;
    private Vector3? _spot;
    private object? _spotKey;

    /// <summary>The step of the last update (inspection and tests).</summary>
    internal PlayerbotRecoveryStep LastStep { get; private set; } = PlayerbotRecoveryStep.Release;

    /// <summary>The spirit-healer step of the last update while the fallback runs (inspection and tests).</summary>
    internal PlayerbotSpiritHealerStep? LastSpiritHealerStep { get; private set; }

    /// <summary>Whether the spirit-healer fallback was taken for this death.</summary>
    internal bool UsingSpiritHealer => _spiritHealer;

    /// <summary>The revive spot the ghost walks to or last chose (inspection and tests).</summary>
    internal Vector3? ReviveSpot => _spot;

    /// <summary>Where the current walk is going (tests): the last point of its route.</summary>
    internal Vector3? RouteEnd => _route is { Points.Count: > 0 } route ? route.Points[^1] : null;

    /// <summary>
    /// The decision, free of world state. <paramref name="stalled"/>: no progress for <see cref="NoProgressMs"/>, or a walk that
    /// stopped closing for <see cref="StuckMs"/>; <paramref name="fallback"/>: the spirit healer was already chosen for this death;
    /// <paramref name="reviveSpotKnown"/>: a clear, reachable spot inside the reclaim radius exists while a hostile camps the ghost.
    /// </summary>
    internal static PlayerbotRecoveryStep Decide(bool ghost, PlayerbotCorpsePlace corpse, bool entranceKnown, bool atCorpse,
        long reclaimWaitSeconds, bool hostileNearCorpse, bool stalled, bool fallback, bool reviveSpotKnown = false)
    {
        if (!ghost) return stalled ? PlayerbotRecoveryStep.Fault : PlayerbotRecoveryStep.Release;
        if (fallback || stalled) return PlayerbotRecoveryStep.SpiritHealer;
        switch (corpse)
        {
            case PlayerbotCorpsePlace.None:
                return PlayerbotRecoveryStep.SpiritHealer;
            case PlayerbotCorpsePlace.OtherMap:
                return entranceKnown ? PlayerbotRecoveryStep.WalkToEntrance : PlayerbotRecoveryStep.SpiritHealer;
        }

        if (!atCorpse) return PlayerbotRecoveryStep.WalkToCorpse;
        if (reclaimWaitSeconds > 0) return PlayerbotRecoveryStep.WaitForReclaimDelay;
        if (!hostileNearCorpse) return PlayerbotRecoveryStep.Reclaim;
        return reviveSpotKnown ? PlayerbotRecoveryStep.WalkToReviveSpot : PlayerbotRecoveryStep.WaitForHostiles;
    }

    /// <summary>The spirit-healer fallback, free of world state.</summary>
    internal static PlayerbotSpiritHealerStep DecideSpiritHealer(bool healerKnown, bool healerInReach, bool graveyardKnown, bool stalled)
    {
        if (stalled) return PlayerbotSpiritHealerStep.Fault;
        if (healerKnown) return healerInReach ? PlayerbotSpiritHealerStep.Activate : PlayerbotSpiritHealerStep.WalkToHealer;
        return graveyardKnown ? PlayerbotSpiritHealerStep.WalkToGraveyard : PlayerbotSpiritHealerStep.Fault;
    }

    internal bool Update(Player player, uint elapsedMs)
    {
        if (elapsedMs == 0 || !player.IsInWorld)
            return false;
        if (player.IsAlive)
        {
            Reset();
            return false;
        }

        long now = (long)session.World.Uptime.TotalMilliseconds;
        if (_progressMs < 0) _progressMs = now;
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        if (!ghost && now - _progressMs > NoProgressMs)
        {
            LastStep = PlayerbotRecoveryStep.Fault;
            throw new InvalidOperationException(Stalled);
        }

        if (session.ManagedBudget is { Remaining: <= 0 }) return false;
        if (_spiritHealer) return SpiritHealer(player, elapsedMs, now);

        bool stalled = Stuck(now);
        Corpse? corpse = player.Combat.Corpse;
        PlayerbotCorpsePlace place = corpse is null ? PlayerbotCorpsePlace.None
            : corpse.MapId == player.MapId && ReferenceEquals(corpse.Map, player.Map) ? PlayerbotCorpsePlace.ThisMap
            : PlayerbotCorpsePlace.OtherMap;
        AreaTriggerTemplate? entrance = place == PlayerbotCorpsePlace.OtherMap ? FindEntrance(player, corpse!.MapId) : null;
        bool atCorpse = place == PlayerbotCorpsePlace.ThisMap && WithinReclaimDistance(player, corpse!);
        long wait = atCorpse ? player.Map?.Combat.CorpseReclaimWaitSeconds(player) ?? 0 : 0;
        bool hostile = atCorpse && wait <= 0 && HostileNear(player);
        Vector3? spot = hostile && !stalled ? ChooseReviveSpot(player, corpse!) : null;
        PlayerbotRecoveryStep step = Decide(ghost, place, entrance is not null, atCorpse, wait, hostile, stalled, fallback: false,
            reviveSpotKnown: spot is not null);
        LastStep = step;
        switch (step)
        {
            case PlayerbotRecoveryStep.Release:
            {
                if (!PlayerbotMovementControl.Stop(session, player)) return false;
                bool released = session.TryManagedAction(WorldOpcode.CmsgRepopRequest, []);
                if (released) _progressMs = now;
                return released;
            }

            case PlayerbotRecoveryStep.WaitForReclaimDelay:
                // The client's Resurrect button stays disabled until SMSG_CORPSE_RECLAIM_DELAY runs out; wait like it does.
                StandStill(player);
                _progressMs = now;
                return false;

            case PlayerbotRecoveryStep.Fault:
                // A body that was never released and made no progress: never the spirit-healer fallback, which is for ghosts.
                throw new InvalidOperationException(Stalled);

            case PlayerbotRecoveryStep.WaitForHostiles:
                // Not progress: a body camped for NoProgressMs is given up for the spirit healer.
                StandStill(player);
                return false;

            case PlayerbotRecoveryStep.WalkToReviveSpot:
                // Not progress for the no-progress bound: a camp that keeps moving must not keep the ghost walking for good.
                return Walk(player, spot!.Value, _spotKey!, elapsedMs, now, countsAsProgress: false);

            case PlayerbotRecoveryStep.Reclaim:
                if (!StandStill(player)) return false;
                return session.TryManagedAction(WorldOpcode.CmsgReclaimCorpse, PlayerbotNavigation.GuidPayload(corpse!.Guid.Value));

            case PlayerbotRecoveryStep.WalkToCorpse:
                return Walk(player, new Vector3(corpse!.X, corpse.Y, corpse.Z), corpse.Guid, elapsedMs, now);

            case PlayerbotRecoveryStep.WalkToEntrance:
                return Walk(player, new Vector3(entrance!.X, entrance.Y, entrance.Z), entrance, elapsedMs, now);

            default:
                // The way back to the body is lost (stalled, stuck, no body, no entrance): take the spirit healer, with a fresh bound.
                _spiritHealer = true;
                ClearWalk();
                _progressMs = now;
                return SpiritHealer(player, elapsedMs, now);
        }
    }

    internal void Reset()
    {
        ClearWalk();
        _spot = null;
        _spotKey = null;
        _progressMs = -1;
        _spiritHealer = false;
        LastStep = PlayerbotRecoveryStep.Release;
        LastSpiritHealerStep = null;
    }

    private bool Stuck(long now) => now - _progressMs > NoProgressMs || (_closingMs >= 0 && now - _closingMs > StuckMs);

    private bool SpiritHealer(Player player, uint elapsedMs, long now)
    {
        LastStep = PlayerbotRecoveryStep.SpiritHealer;
        Creature? healer = NearestSpiritHealer(player);
        WorldSafeLoc? graveyard = healer is null ? Graveyard(player) : null;
        bool inReach = healer is not null && Distance(player, healer.X, healer.Y, healer.Z) <= SpiritHealerReachYards;
        PlayerbotSpiritHealerStep step = DecideSpiritHealer(healer is not null, inReach, graveyard is not null, Stuck(now));
        LastSpiritHealerStep = step;
        switch (step)
        {
            case PlayerbotSpiritHealerStep.Activate:
                if (!StandStill(player)) return false;
                // Still dead afterwards (the server refused) is not progress: the fallback faults after NoProgressMs.
                return session.TryManagedAction(WorldOpcode.CmsgSpiritHealerActivate, PlayerbotNavigation.GuidPayload(healer!.Guid.Value));
            case PlayerbotSpiritHealerStep.WalkToHealer:
                return Walk(player, new Vector3(healer!.X, healer.Y, healer.Z), healer.Guid, elapsedMs, now);
            case PlayerbotSpiritHealerStep.WalkToGraveyard:
                return Walk(player, new Vector3(graveyard!.X, graveyard.Y, graveyard.Z), graveyard, elapsedMs, now);
            default:
                throw new InvalidOperationException(SpiritHealerFailed);
        }
    }

    /// <summary>Walk towards <paramref name="goal"/> (identified by <paramref name="key"/>) in bounded chunks; closing on it is progress.</summary>
    private bool Walk(Player player, Vector3 goal, object key, uint elapsedMs, long now, bool countsAsProgress = true)
    {
        if (!Equals(_goal, key))
        {
            ClearWalk();
            _goal = key;
            _closingMs = now; // a goal the bot cannot even plan a route to counts as stuck after StuckMs
        }

        if (_route is null || _route.Complete)
        {
            if (!PlayerbotNavigation.TryPlanToward(player, goal, options, out _route))
            {
                _route = null;
                return false;
            }
        }

        bool advanced = PlayerbotNavigation.TryAdvance(session, _route!, options, elapsedMs, session.World.NowMs);
        if (!advanced) _route = null;
        float after = Distance(player, goal.X, goal.Y, goal.Z);
        if (float.IsFinite(after) && after < _bestDistance - 0.01f)
        {
            _bestDistance = after;
            _closingMs = now;
            if (countsAsProgress) _progressMs = now;
        }

        return advanced;
    }

    /// <summary>Stop walking (the bot is where it needs to be, or waiting): true when it stands still.</summary>
    private bool StandStill(Player player)
    {
        ClearWalk();
        return PlayerbotMovementControl.Stop(session, player);
    }

    private void ClearWalk()
    {
        _route = null;
        _goal = null;
        _closingMs = -1;
        _bestDistance = float.PositiveInfinity;
    }

    /// <summary>
    /// The entrance a ghost on its current map walks into to reach a body on <paramref name="corpseMap"/>: the nearest trigger here
    /// whose teleport leads to that map, else to a dungeon it is nested in (the server then sends the ghost on to the body's
    /// dungeon, <c>GhostEntryRules</c>). Null when there is none on this map.
    /// </summary>
    internal AreaTriggerTemplate? FindEntrance(Player player, uint corpseMap)
    {
        WorldMaps maps = WorldMaps.Of(session.World);
        IReadOnlyList<AreaTriggerTemplate> here = PlayerbotAreaTriggers.OnMap(maps, player.MapId);
        if (here.Count == 0) return null;
        var seen = new HashSet<uint>();
        for (uint target = corpseMap; target != 0 && seen.Add(target);)
        {
            AreaTriggerTemplate? nearest = here
                .Where(t => maps.FindAreaTriggerTeleport(t.Id)?.TargetMap == target)
                .OrderBy(t => Distance(player, t.X, t.Y, t.Z))
                .FirstOrDefault();
            if (nearest is not null) return nearest;
            target = maps.Registry.Find(target) is { IsDungeon: true } dungeon ? dungeon.Parent : 0;
        }

        return null;
    }

    /// <summary>
    /// A living hostile creature the ghost can see within <see cref="HostileClearYards"/> of the ghost itself, where CMSG_RECLAIM_CORPSE
    /// revives it (vmangos MiscHandler.cpp:599), which may be up to the reclaim radius away from the body.
    /// </summary>
    internal static bool HostileNear(Player player)
    {
        var here = new Vector3(player.X, player.Y, player.Z);
        foreach (Vector3 hostile in HostilePositions(player))
            if (Vector3.Distance(hostile, here) <= HostileClearYards)
                return true;
        return false;
    }

    /// <summary>The living hostile creatures the ghost can see.</summary>
    private static List<Vector3> HostilePositions(Player player)
    {
        var found = new List<Vector3>();
        if (player.Map is not { } map) return found;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature || !creature.IsInWorld || !creature.IsAlive
                || !ReferenceEquals(creature.Map, map))
                continue;
            if (map.Combat.Hooks.IsHostileTo(creature, player))
                found.Add(new Vector3(creature.X, creature.Y, creature.Z));
        }

        return found;
    }

    /// <summary>
    /// The revive spot to walk to while a hostile camps the ghost: the one chosen before while it is still clear and inside the
    /// reclaim radius, else a new one (<see cref="FindReviveSpot"/>) found with the navigation mesh. Null when there is none.
    /// </summary>
    private Vector3? ChooseReviveSpot(Player player, Corpse corpse)
    {
        if (player.Map is null) return null;
        var body = new Vector3(corpse.X, corpse.Y, corpse.Z);
        List<Vector3> hostiles = HostilePositions(player);
        if (_spot is { } kept && IsReviveSpot(kept, body, hostiles)) return kept;

        Vector3? found = FindReviveSpot(new Vector3(player.X, player.Y, player.Z), body, hostiles, candidate =>
        {
            // The mesh finds the floor: a candidate is a place on the ring at the body's height, and the route ends on the
            // nearest walkable point (a candidate off the mesh ends short of it, which IsReviveSpot then re-checks).
            if (!PlayerbotNavigation.TryPlan(player, candidate, options, out PlayerbotRoute? route) || route is null
                || route.Distance > ReviveSpotMaxWalkYards)
                return null;
            return route.Points[^1];
        });
        if (found is null)
        {
            _spot = null;
            return null;
        }

        _spot = found;
        _spotKey = new object();
        return found;
    }

    private static bool IsReviveSpot(Vector3 spot, Vector3 body, List<Vector3> hostiles)
    {
        if (Vector3.Distance(spot, body) > ReviveSpotMaxYards) return false;
        foreach (Vector3 hostile in hostiles)
            if (Vector3.Distance(hostile, spot) <= ReviveSpotClearYards) return false;
        return true;
    }

    /// <summary>
    /// A place to reclaim a camped body from, free of world state: candidates on rings round the body, within
    /// <see cref="ReviveSpotMaxYards"/> of it (the server revives within <see cref="CombatConstants.CorpseReclaimRadius"/>) and at
    /// least <see cref="ReviveSpotClearYards"/> from every hostile, nearest to the ghost first. <paramref name="reach"/> maps a
    /// candidate to the point the ghost can actually walk to (the navigation mesh's, null when it cannot); the first reachable
    /// point that is still a revive spot is taken. At most <see cref="ReviveSpotMaxQueries"/> candidates are asked.
    /// </summary>
    internal static Vector3? FindReviveSpot(Vector3 ghost, Vector3 body, IReadOnlyList<Vector3> hostiles, Func<Vector3, Vector3?> reach)
    {
        List<Vector3> hostileList = hostiles as List<Vector3> ?? [.. hostiles];
        var candidates = new List<Vector3> { body };
        // Rings every few yards, finely spaced: a camp can leave only a thin slice of the reclaim disc clear (the Frostmane camp
        // round Mirthblade's body left a pocket on the far rim, 29 yards from the nearest troll).
        for (float radius = ReviveSpotRingYards; radius <= ReviveSpotMaxYards - 1f; radius += ReviveSpotRingYards)
            for (int step = 0; step < ReviveSpotAngles; step++)
            {
                float angle = step * 2f * MathF.PI / ReviveSpotAngles;
                candidates.Add(new Vector3(body.X + (MathF.Cos(angle) * radius), body.Y + (MathF.Sin(angle) * radius), body.Z));
            }

        int queries = 0;
        foreach (Vector3 candidate in candidates
            .Where(c => IsReviveSpot(c, body, hostileList))
            .OrderBy(c => Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(ghost.X, ghost.Y))))
        {
            if (++queries > ReviveSpotMaxQueries) break;
            if (reach(candidate) is { } point && IsReviveSpot(point, body, hostileList)) return point;
        }

        return null;
    }

    /// <summary>The nearest living spirit healer the ghost can see (ghosts see the spirit services across the map's view range).</summary>
    private static Creature? NearestSpiritHealer(Player player)
    {
        if (player.Map is not { } map) return null;
        Creature? nearest = null;
        float best = float.PositiveInfinity;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature || !creature.IsInWorld || !creature.IsAlive
                || !ReferenceEquals(creature.Map, map) || (creature.NpcFlags & (uint)NpcFlags.SpiritHealer) == 0)
                continue;
            float distance = Distance(player, creature.X, creature.Y, creature.Z);
            if (distance < best)
            {
                best = distance;
                nearest = creature;
            }
        }

        return nearest;
    }

    /// <summary>The graveyard of the ghost's position on its own map (a spirit healer stands at it), or null.</summary>
    private WorldSafeLoc? Graveyard(Player player)
    {
        if (player.Map is not { } map) return null;
        GraveyardCatalog catalog = WorldGraveyards.Of(session.World).Catalog;
        if (catalog.SafeLocCount == 0) return null;
        (uint zone, uint area) = map.GetZoneAndAreaId(player.X, player.Y, player.Z);
        uint team = player.Team == Team.Alliance ? GraveyardCatalog.TeamAlliance : GraveyardCatalog.TeamHorde;
        WorldSafeLoc? graveyard = GraveyardSelector.FindClosest(catalog, WorldMaps.Of(session.World).Registry, map.MapId,
            player.X, player.Y, player.Z, zone, area, team);
        return graveyard?.MapId == map.MapId ? graveyard : null;
    }

    private static bool WithinReclaimDistance(Player player, Corpse corpse)
    {
        float max = CombatConstants.CorpseReclaimRadius + corpse.BoundingRadius + player.BoundingRadius;
        float distance = Distance(player, corpse.X, corpse.Y, corpse.Z);
        return float.IsFinite(distance) && distance < max;
    }

    private static float Distance(WorldObject from, float x, float y, float z)
        => MathF.Sqrt(MathF.Pow(from.X - x, 2) + MathF.Pow(from.Y - y, 2) + MathF.Pow(from.Z - z, 2));
}
