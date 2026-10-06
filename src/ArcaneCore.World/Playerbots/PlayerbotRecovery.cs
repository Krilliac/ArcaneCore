using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>Bounded ordinary ghost recovery for one managed player.</summary>
internal sealed class PlayerbotRecovery(WorldSession session, PlayerbotOptions options)
{
    private const long MaxRecoveryMs = 180_000;
    private const int MaxAttempts = 360;
    private PlayerbotRoute? _route;
    private ObjectGuid _routeCorpse;
    private long _startedMs = -1;
    private int _attempts;
    private int _stuck;

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
        if (_startedMs < 0) _startedMs = now;
        if (now - _startedMs > MaxRecoveryMs)
            throw new InvalidOperationException("playerbot-recovery-stalled");
        if (session.ManagedBudget is { Remaining: <= 0 }) return false;
        if (++_attempts > MaxAttempts) throw new InvalidOperationException("playerbot-recovery-stalled");

        if ((player.Flags & PlayerFlags.Ghost) == 0)
        {
            if (!PlayerbotMovementControl.Stop(session, player)) return false;
            return session.TryManagedAction(WorldOpcode.CmsgRepopRequest, []);
        }

        if (player.Combat.Corpse is not { } corpse || corpse.Map != player.Map)
            return false;
        if (WithinReclaimDistance(player, corpse))
        {
            _route = null;
            _routeCorpse = default;
            if (!PlayerbotMovementControl.Stop(session, player)) return false;
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
            if (_routeCorpse != corpse.Guid) _stuck = 0;
            _routeCorpse = corpse.Guid;
        }

        float before = Distance(player, corpse);
        if (!PlayerbotNavigation.TryAdvance(session, _route!, options, elapsedMs, session.World.NowMs))
        {
            _route = null;
            if (++_stuck > 8) throw new InvalidOperationException("playerbot-recovery-stuck");
            return false;
        }

        float after = Distance(player, corpse);
        if (!float.IsFinite(after) || after >= before - 0.01f)
        {
            if (++_stuck > 8) throw new InvalidOperationException("playerbot-recovery-stuck");
        }
        else
        {
            _stuck = 0;
        }
        return true;
    }

    internal void Reset()
    {
        _route = null;
        _routeCorpse = default;
        _startedMs = -1;
        _attempts = 0;
        _stuck = 0;
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
