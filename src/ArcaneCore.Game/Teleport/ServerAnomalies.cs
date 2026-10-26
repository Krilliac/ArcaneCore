using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Teleport;

/// <summary>
/// Front door to <see cref="AnomalyLedger"/> for the server-side outcome checks that concern a player whether a client or a bot
/// drives it (teleport, death, session): the subject is the stable <c>player Name (guid N)</c> with N the decimal low part of the guid, as the other Game writers (MapCombat, the Locomotion observers, TransportAnomalies) write it, the detail is the caller's text
/// followed by <c>| map M at x,y,z</c>. Failure path only: callers format their detail after the check failed.
/// </summary>
public static class ServerAnomalies
{
    /// <summary>Subsystem of the teleport checks.</summary>
    public const string Teleport = "teleport";

    /// <summary>Anomaly code for a near or far teleport whose client acknowledgement timed out.</summary>
    public const string AckTimeout = "ack-timeout";

    /// <summary>Anomaly code for a scheduled or arriving far teleport that did not run within its budget.</summary>
    public const string FarStuck = "far-stuck";

    /// <summary>Subsystem of the death and resurrection checks.</summary>
    public const string Death = "death";

    /// <summary>Subsystem of the session and network checks.</summary>
    public const string Session = "session";

    /// <summary>The normalised key for <paramref name="subsystem"/>/<paramref name="code"/>.</summary>
    public static string Key(string subsystem, string code) => AnomalyLedger.KeyOf(subsystem, code);

    /// <summary>The stable subject for <paramref name="player"/>.</summary>
    public static string SubjectOf(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return SubjectOf(player.Name, player.Guid);
    }

    /// <summary>The stable subject for a player known only by name and guid (a GM trace that outlives the <see cref="Player"/> lookup); identical to <see cref="SubjectOf(Player)"/> for the same player.</summary>
    public static string SubjectOf(string playerName, ObjectGuid guid)
        => string.Create(CultureInfo.InvariantCulture, $"player {playerName} (guid {guid.Low})");

    /// <summary>Report a failed check about <paramref name="player"/>.</summary>
    public static AnomalyRecording Report(Player player, string subsystem, string code, string detail, AnomalySeverity severity = AnomalySeverity.Warning)
    {
        ArgumentNullException.ThrowIfNull(player);
        string fullDetail = float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z)
            ? string.Create(CultureInfo.InvariantCulture, $"{detail} | map {player.MapId} at {player.X:F1},{player.Y:F1},{player.Z:F1}")
            : string.Create(CultureInfo.InvariantCulture, $"{detail} | map {player.MapId} at a non-finite position");
        return AnomalyLedger.Shared.Report(subsystem, code, SubjectOf(player), fullDetail, severity);
    }

    /// <summary>A capture of one player's reports from the shared ledger (tests).</summary>
    public static AnomalyCapture CaptureFor(Player player)
    {
        string subject = SubjectOf(player);
        return AnomalyLedger.Shared.Capture(n => string.Equals(n.Subject, subject, StringComparison.Ordinal));
    }
}
