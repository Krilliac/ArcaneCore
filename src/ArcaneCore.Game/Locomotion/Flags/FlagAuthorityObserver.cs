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
    public const uint SpeedViolationCooldownMs = 1000;

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

        if (previous.Time == 0 || incoming.Time <= previous.Time)
        {
            return;
        }

        uint dtMs = incoming.Time - previous.Time;
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
            return;
        }

        if (state.LastSpeedViolationReportMs is { } lastReport && unchecked(nowMs - lastReport) < SpeedViolationCooldownMs)
        {
            return;
        }

        state.LastSpeedViolationReportMs = nowMs;
        AnomalySeverity severity = player.Session.IsPlayerbot ? AnomalySeverity.Info : AnomalySeverity.Warning;
        float observedSpeed = horizDist / dtSec;
        AnomalyLedger.Shared.Report(
            "move",
            "speed-violation",
            $"player {player.Name} (guid {player.Guid.Low})",
            $"Horizontal displacement {horizDist:F2}yd over {dtMs}ms ({observedSpeed:F2}yd/s) exceeded max allowed {maxAllowedDist:F2}yd (base speed {allowedSpeed:F2}yd/s) on map {player.MapId} from ({previous.X:F2}, {previous.Y:F2}, {previous.Z:F2}) to ({incoming.X:F2}, {incoming.Y:F2}, {incoming.Z:F2}).",
            severity);
    }
}
