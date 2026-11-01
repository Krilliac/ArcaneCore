using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The server's say over the flags of an incoming movement block (vmangos HandleMoverRelocation,
/// MovementHandler.cpp:1062-1071): <see cref="MovementInfo.CorrectData"/> removes contradictory flags, then a unit the
/// server has rooted keeps its Root flag whatever the client claims ("Prevent client from removing root flag").
/// It runs first (order 10) so later observers see the corrected block. Every other flag is the client's own; the
/// stricter flag tests of vmangos' anticheat are not part of retail behaviour and are not delivered.
/// </summary>
[MovementObserver(Order = 10)]
public sealed class FlagAuthorityObserver : IClientMovementObserver
{
    public const float SpeedToleranceMultiplier = 1.5f;
    public const float SpeedSlackYards = 15.0f;
    /// <summary>A speed-violation episode ends after this many consecutive legitimate packets.</summary>
    public const int EpisodeEndsAfterLegitPackets = 5;

    /// <summary>A speed-violation episode also ends when the last violation is at least this old.</summary>
    public const uint EpisodeQuietMs = 5000;

    public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
    {
        incoming.CorrectData();

        // The previously stored block carries the server's Root flag (set when the root was acknowledged or enforced).
        if (previous.HasFlag(MovementFlags.Root) && !incoming.HasFlag(MovementFlags.Root))
        {
            incoming.Flags |= MovementFlags.Root;
        }

        CheckSpeedViolation(context, in previous, in incoming);
    }

    private static void CheckSpeedViolation(MovementObserverContext context, in MovementInfo previous, in MovementInfo incoming)
    {
        Player player = context.Player;
        LocomotionState state = player.Locomotion;
        if (state.SuppressAnomalies
            || player.IsGameMaster
            || LocomotionEnvironment.For(context.World).IsBeingTeleported(player)
            || state.ActiveSpline is not null
            || state.Pending.HasPendingOfType(MovementChangeType.KnockBack)
            || (player.UnitFlags & UnitFlags.TaxiFlight) != 0
            || player.Transport is not null
            || previous.HasFlag(MovementFlags.OnTransport)
            || incoming.HasFlag(MovementFlags.OnTransport))
        {
            return;
        }

        uint nowMs = context.World.NowMs;
        if (state.LastTeleportOrKnockbackMs is { } lastTp && unchecked(nowMs - lastTp) < 2000)
        {
            return;
        }

        // Both samples on the server clock: the stored block is stamped with the server time of receipt (Unit.ApplyMovement), and the
        // incoming block is being received now. incoming.Time is the client's own clock and is never compared with it.
        if (previous.Time == 0)
        {
            return;
        }

        uint dtMs = unchecked(nowMs - previous.Time);
        if (dtMs == 0 || dtMs > 60_000)
        {
            return;
        }

        if (!float.IsFinite(previous.X) || !float.IsFinite(previous.Y)
            || !float.IsFinite(incoming.X) || !float.IsFinite(incoming.Y))
        {
            return;
        }

        float dx = incoming.X - previous.X;
        float dy = incoming.Y - previous.Y;
        float horizDist = MathF.Sqrt((dx * dx) + (dy * dy));
        if (!float.IsFinite(horizDist))
        {
            return;
        }

        float allowedSpeed = MathF.Max(
            Unit.BaseRunSpeed,
            MathF.Max(player.RunSpeed, MathF.Max(player.SwimSpeed, player.WalkSpeed)));

        if ((incoming.HasFlag(MovementFlags.Jumping) || previous.HasFlag(MovementFlags.Jumping))
            && float.IsFinite(incoming.JumpXySpeed)
            && incoming.JumpXySpeed > allowedSpeed
            && incoming.JumpXySpeed <= 100.0f)
        {
            allowedSpeed = incoming.JumpXySpeed;
        }

        float dtSec = MathF.Max(dtMs / 1000.0f, 0.05f);
        float maxAllowedDist = (allowedSpeed * dtSec * SpeedToleranceMultiplier) + SpeedSlackYards;
        if (horizDist <= maxAllowedDist)
        {
            // A legitimate packet: the episode ends after enough of them in a row, or once the player has been quiet long enough.
            if (state.SpeedViolationInEpisode
                && (++state.SpeedLegitStreak >= EpisodeEndsAfterLegitPackets
                    || (state.LastSpeedViolationReportMs is { } lastViolation && unchecked(nowMs - lastViolation) >= EpisodeQuietMs)))
            {
                state.SpeedViolationInEpisode = false;
                state.SpeedLegitStreak = 0;
            }

            return;
        }

        state.SpeedLegitStreak = 0;
        bool quiet = state.LastSpeedViolationReportMs is { } last && unchecked(nowMs - last) >= EpisodeQuietMs;
        state.LastSpeedViolationReportMs = nowMs;
        if (state.SpeedViolationInEpisode && !quiet)
        {
            return;
        }

        state.SpeedViolationInEpisode = true;
        AnomalySeverity severity = player.Session.IsPlayerbot ? AnomalySeverity.Info : AnomalySeverity.Warning;
        float observedSpeed = horizDist / dtSec;
        AnomalyLedger.Shared.Report(
            "move",
            "speed-violation",
            $"player (guid {player.Guid.Low})",
            $"Horizontal displacement {horizDist:F2}yd over {dtMs}ms ({observedSpeed:F2}yd/s) exceeded max allowed {maxAllowedDist:F2}yd (base speed {allowedSpeed:F2}yd/s) on map {player.MapId} from ({previous.X:F2}, {previous.Y:F2}, {previous.Z:F2}) to ({incoming.X:F2}, {incoming.Y:F2}, {incoming.Z:F2}).",
            severity);
    }
}
