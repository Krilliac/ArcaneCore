using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Transports;

/// <summary>Which per-episode "already reported" latch on <see cref="LocomotionState"/> a transport anomaly uses.</summary>
internal enum TransportAnomalyLatch
{
    PassengerLeftBehind,
    TransportDesync,
}

/// <summary>
/// The player-subject anomaly report the transport code files in several places. Each call site keeps its own
/// <see cref="LocomotionState.SuppressAnomalies"/> gating and resets; this only builds the subject text, sets a latch and files the report.
/// </summary>
internal static class TransportAnomalies
{
    /// <summary>The ledger subject for a player: its name and decimal low GUID.</summary>
    public static string PlayerSubject(Player player) => $"player (guid {player.Guid.Low})";

    /// <summary>Sets <paramref name="latch"/> and returns true when it was clear; returns false (changing nothing) when this episode was already reported.</summary>
    public static bool TryLatch(LocomotionState state, TransportAnomalyLatch latch)
    {
        if (latch == TransportAnomalyLatch.PassengerLeftBehind)
        {
            if (state.PassengerLeftBehindReportedInEpisode)
            {
                return false;
            }

            state.PassengerLeftBehindReportedInEpisode = true;
            return true;
        }

        if (state.TransportDesyncReportedInEpisode)
        {
            return false;
        }

        state.TransportDesyncReportedInEpisode = true;
        return true;
    }

    /// <summary>Files a Warning anomaly about <paramref name="player"/> in the "transport" subsystem.</summary>
    public static void Report(string code, Player player, string detail)
        => Report("transport", code, player, detail);

    /// <summary>Files a Warning anomaly about <paramref name="player"/> in <paramref name="subsystem"/>.</summary>
    public static void Report(string subsystem, string code, Player player, string detail)
        => AnomalyLedger.Shared.Report(subsystem, code, PlayerSubject(player), detail, AnomalySeverity.Warning);
}
