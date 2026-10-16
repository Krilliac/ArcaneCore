using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Playerbot front door to <see cref="AnomalyLedger"/>: every bot check that finds a silent failure (a lift it never boarded, a ride
/// that ended at the wrong level, a route through a wall, a stall) reports here so the subsystem, the subject and the bot's place are
/// written the same way everywhere.
/// <para>
/// Subsystem: <c>bot-</c> + <paramref name="area"/> (<c>bot-elevator</c>, <c>bot-tram</c>, <c>bot-nav</c>...). Subject: <c>bot Name (guid
/// HEX)</c>, stable per bot, so <see cref="AnomalySnapshot.DistinctSubjects"/> says how many bots hit the key. Detail: the caller's
/// text, then <c>| map M at x,y,z</c>.
/// </para>
/// <para>Failure path only: callers format their detail after the check failed, never per tick on success.</para>
/// </summary>
internal static class PlayerbotAnomalies
{
    /// <summary>The subsystem prefix of every bot key.</summary>
    internal const string Prefix = "bot-";

    /// <summary>The normalised subsystem for <paramref name="area"/> (<c>elevator</c> becomes <c>bot-elevator</c>).</summary>
    internal static string Subsystem(string area) => AnomalyLedger.Normalise(Prefix + area);

    /// <summary>The normalised key for <paramref name="area"/>/<paramref name="code"/> (tests: <c>capture.Any(PlayerbotAnomalies.Key("elevator", "board-missed"))</c>).</summary>
    internal static string Key(string area, string code) => AnomalyLedger.KeyOf(Prefix + area, code);

    /// <summary>The stable subject for <paramref name="player"/>.</summary>
    internal static string SubjectOf(Player player)
        => string.Create(CultureInfo.InvariantCulture, $"bot {player.Name} (guid {player.Guid.Value:X})");

    /// <summary>Report a failed bot check (see the type summary for the shape of the line).</summary>
    internal static AnomalyRecording Report(Player player, string area, string code, string detail, AnomalySeverity severity = AnomalySeverity.Warning)
    {
        ArgumentNullException.ThrowIfNull(player);
        string where = float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z)
            ? string.Create(CultureInfo.InvariantCulture, $" | map {player.MapId} at {player.X:F1},{player.Y:F1},{player.Z:F1}")
            : string.Create(CultureInfo.InvariantCulture, $" | map {player.MapId} at a non-finite position");
        return AnomalyLedger.Shared.Report(Prefix + area, code, SubjectOf(player), detail + where, severity, OriginOf(player));
    }

    /// <summary>
    /// The identity token of the world <paramref name="player"/> is in (<see cref="WorldRuntime.Identity"/>: a token that retains nothing),
    /// carried on its reports as <see cref="AnomalyNotice.Origin"/> so a capture can tell two worlds apart that name a bot alike. Null while
    /// the player is on no map.
    /// </summary>
    internal static object? OriginOf(Player player) => player.Map?.WorldIdentity;

    /// <summary>
    /// A capture of one bot's reports (tests): every report whose subject is <paramref name="player"/>'s, from the shared ledger that
    /// other tests also write to. The bot's world is resolved once, here, never again on the reporting thread, so the bot leaving its map
    /// later (teardown, transfer) changes nothing. A bot on a map hears its own world's reports and unstamped ones (a bot that was on no map
    /// when it reported). A bot on no map when this is called (a unit test's bare <see cref="Player"/>) hears only unstamped reports, never a
    /// stamped one from any world.
    /// </summary>
    internal static AnomalyCapture CaptureFor(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        object? world = OriginOf(player);
        string subject = SubjectOf(player);
        return AnomalyLedger.Shared.Capture(n => string.Equals(n.Subject, subject, StringComparison.Ordinal)
            && (n.Origin is null || (world is not null && ReferenceEquals(n.Origin, world))));
    }

    /// <summary>
    /// A capture of the reports that <paramref name="world"/> itself made (tests): those the reporter stamped with this world's
    /// <see cref="WorldRuntime.Identity"/> (the bot front door, the group coordinator, the lifecycle feature and the population feature do),
    /// narrowed by <paramref name="filter"/>. Reports with no origin or another world's never match.
    /// </summary>
    internal static AnomalyCapture CaptureIn(WorldRuntime world, Func<AnomalyNotice, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        object identity = world.Identity;
        return AnomalyLedger.Shared.Capture(n => ReferenceEquals(n.Origin, identity) && (filter is null || filter(n)));
    }
}
