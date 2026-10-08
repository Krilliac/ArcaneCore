using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Bounded ordinary ghost recovery for one managed player: release, walk back to the body, wait out the reclaim delay, reclaim.
/// The bound is progress, not a count of thinks: the recovery gives up ("playerbot-recovery-stalled", an action fault) only when
/// nothing moved it on for <see cref="NoProgressMs"/>, and a walk that stops closing on the body for <see cref="StuckMs"/> is
/// "playerbot-recovery-stuck". Waiting at the body while the server's reclaim delay runs (30, 60 or 120 s by recent deaths,
/// vmangos GetCorpseReclaimDelay) is progress: the old per-think cap of 360 thinks ran out after 36 s at a 100 ms think interval
/// and faulted bots that were only waiting (live, 2026-10-07).
/// </summary>
internal sealed class PlayerbotRecovery(WorldSession session, PlayerbotOptions options)
{
    internal const long NoProgressMs = 60_000;
    internal const long StuckMs = 10_000;
    private PlayerbotRoute? _route;
    private ObjectGuid _routeCorpse;
    private long _progressMs = -1;
    private long _closingMs = -1;
    private float _bestDistance = float.PositiveInfinity;

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
        if (now - _progressMs > NoProgressMs)
            throw new InvalidOperationException("playerbot-recovery-stalled");
        if (session.ManagedBudget is { Remaining: <= 0 }) return false;

        if ((player.Flags & PlayerFlags.Ghost) == 0)
        {
            if (!PlayerbotMovementControl.Stop(session, player)) return false;
            bool released = session.TryManagedAction(WorldOpcode.CmsgRepopRequest, []);
            if (released) _progressMs = now;
            return released;
        }

        if (player.Combat.Corpse is not { } corpse || corpse.Map != player.Map)
            return false;
        if (WithinReclaimDistance(player, corpse))
        {
            _route = null;
            _routeCorpse = default;
            if (!PlayerbotMovementControl.Stop(session, player)) return false;
            // The client's Resurrect button stays disabled until SMSG_CORPSE_RECLAIM_DELAY runs out; wait like it does.
            if (player.Map?.Combat.CorpseReclaimWaitSeconds(player) is > 0)
            {
                _progressMs = now;
                return false;
            }

            return session.TryManagedAction(WorldOpcode.CmsgReclaimCorpse,
                PlayerbotNavigation.GuidPayload(corpse.Guid.Value));
        }

        if (_route is null || _route.Complete || _routeCorpse != corpse.Guid)
        {
            Vector3 origin = new(player.X, player.Y, player.Z);
            Vector3 destination = new(corpse.X, corpse.Y, corpse.Z);
            float distance = Vector3.Distance(origin, destination);
            float chunk = MathF.Min(options.MaxRouteYards * 0.9f, Math.Max(1, options.MaxPathPoints - 2));
            if (distance > chunk) destination = origin + ((destination - origin) * (chunk / distance));
            if (!PlayerbotNavigation.TryPlan(player, destination, options, out _route))
                return false;
            if (_routeCorpse != corpse.Guid) { _bestDistance = float.PositiveInfinity; _closingMs = now; }
            _routeCorpse = corpse.Guid;
        }

        if (_closingMs < 0) _closingMs = now;
        bool advanced = PlayerbotNavigation.TryAdvance(session, _route!, options, elapsedMs, session.World.NowMs);
        if (!advanced) _route = null;
        float after = Distance(player, corpse);
        if (float.IsFinite(after) && after < _bestDistance - 0.01f)
        {
            _bestDistance = after;
            _closingMs = now;
            _progressMs = now;
        }
        else if (now - _closingMs > StuckMs)
        {
            throw new InvalidOperationException("playerbot-recovery-stuck");
        }

        return advanced;
    }

    internal void Reset()
    {
        _route = null;
        _routeCorpse = default;
        _progressMs = -1;
        _closingMs = -1;
        _bestDistance = float.PositiveInfinity;
    }

    private static bool WithinReclaimDistance(Player player, Corpse corpse)
    {
        float max = CombatConstants.CorpseReclaimRadius + corpse.BoundingRadius + player.BoundingRadius;
        float distance = Distance(player, corpse);
        return float.IsFinite(distance) && distance < max;
    }

    private static float Distance(WorldObject player, WorldObject corpse)
        => MathF.Sqrt(MathF.Pow(player.X - corpse.X, 2) + MathF.Pow(player.Y - corpse.Y, 2)
            + MathF.Pow(player.Z - corpse.Z, 2));
}
