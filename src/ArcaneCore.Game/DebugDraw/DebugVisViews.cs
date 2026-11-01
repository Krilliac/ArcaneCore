using System.Globalization;
using System.Numerics;
using System.Text;

namespace ArcaneCore.Game.DebugDraw;

/// <summary>One marker a view asks for: the same fields as the World layer's marker request (which this assembly cannot see).</summary>
public readonly record struct DebugVisMarker(DebugMarkerKind Kind, Vector3 Position, string Label, bool Emphasis = false);

/// <summary>A rendered view: a title, the lines to print and the markers to draw (never more than <see cref="DebugVisViews.MaxMarkers"/>).</summary>
public sealed record DebugVisView(string Title, IReadOnlyList<string> Lines, IReadOnlyList<DebugVisMarker> Markers);

/// <summary>One bot as the stall view sees it: its status row (<see cref="Code"/> is the stall or error code, null when it makes progress) and where it stands.</summary>
public sealed record BotStallEntry(string Name, string State, string? Code, Vector3? Position);

/// <summary>One realm-authority lease scope: its current holder (null when unheld or expired) and the highest fencing token ever issued.</summary>
public sealed record LeaseEntry(string Scope, string? OwnerNodeId, Guid? OwnerIncarnation, long FencingToken, long HighWaterToken,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>One Scourge invasion zone: the necropolises still standing of <see cref="Total"/>, the next attack time (unix seconds, 0 none) and where the Mouth of Kel'Thuzad stands.</summary>
public sealed record InvasionZoneEntry(uint ZoneId, uint MapId, int Remaining, int Total, long NextAttackUnix, Vector3? Mouth);

/// <summary>The Scourge invasion as the view sees it: enabled or not, battles won, the last attacked zone and every zone.</summary>
public sealed record InvasionSnapshot(bool Enabled, int BattlesWon, uint LastAttackZone, IReadOnlyList<InvasionZoneEntry> Zones);

/// <summary>
/// The data behind <c>.debug vis botstall|leases|invasion</c>: pure functions from a snapshot of server state to the lines and markers the
/// command prints and draws. Nothing here touches the world, so the views render from a fixture. Every view caps its markers at
/// <see cref="MaxMarkers"/>, the most important first.
/// </summary>
public static class DebugVisViews
{
    /// <summary>Most markers one view asks for.</summary>
    public const int MaxMarkers = 50;

    /// <summary>The names of the six invaded zones (ScourgeInvasionCatalog.Zones).</summary>
    private static readonly Dictionary<uint, string> ZoneNames = new()
    {
        [16] = "Azshara",
        [4] = "Blasted Lands",
        [46] = "Burning Steppes",
        [139] = "Eastern Plaguelands",
        [440] = "Tanaris",
        [618] = "Winterspring",
    };

    /// <summary>The name of an invaded zone, or "zone N".</summary>
    public static string ZoneName(uint zoneId)
        => ZoneNames.TryGetValue(zoneId, out string? name) ? name : F($"zone {zoneId}");

    /// <summary>
    /// Bots that make no progress: every stalled bot gets a red marker where it stands and a line with its stall code; the rest are only
    /// counted. Stalled bots without a position (not in a world) are listed but not drawn.
    /// </summary>
    public static DebugVisView BotStall(IReadOnlyList<BotStallEntry> bots)
    {
        ArgumentNullException.ThrowIfNull(bots);
        var lines = new List<string>();
        var markers = new List<DebugVisMarker>();
        BotStallEntry[] stalled = [.. bots.Where(b => b.Code is not null).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
        lines.Add(F($"bot stall: {stalled.Length} of {bots.Count} bot(s) stalled."));
        foreach (BotStallEntry bot in stalled)
        {
            string text = F($"{bot.Name} [{bot.State}]: {bot.Code}");
            lines.Add(bot.Position is { } at ? text + F($" at ({at.X:F1}, {at.Y:F1}, {at.Z:F1})") : text + " (not in a world)");
            if (bot.Position is { } position && markers.Count < MaxMarkers)
            {
                markers.Add(new DebugVisMarker(DebugMarkerKind.PathCornerBad, position, text, Emphasis: true));
            }
        }

        if (stalled.Length == 0)
        {
            lines.Add("no bot reports a stall or an error.");
        }

        return new DebugVisView("botstall", lines, markers);
    }

    /// <summary>
    /// Who owns each realm-authority lease scope: one line and one marker per scope, stacked above <paramref name="anchor"/> (blue when
    /// a node holds the lease, red when it is unheld or expired at <paramref name="nowUtc"/>).
    /// </summary>
    public static DebugVisView Leases(IReadOnlyList<LeaseEntry> leases, DateTimeOffset nowUtc, Vector3 anchor)
    {
        ArgumentNullException.ThrowIfNull(leases);
        var lines = new List<string>();
        var markers = new List<DebugVisMarker>();
        lines.Add(F($"lease ownership: {leases.Count} scope(s)."));
        for (int i = 0; i < leases.Count; i++)
        {
            LeaseEntry lease = leases[i];
            bool held = lease.OwnerNodeId is not null && lease.ExpiresAtUtc is { } expires && nowUtc < expires;
            string text;
            if (held)
            {
                long left = (long)(lease.ExpiresAtUtc!.Value - nowUtc).TotalSeconds;
                text = F($"{lease.Scope}: held by {lease.OwnerNodeId} (incarnation {lease.OwnerIncarnation:N}), fencing token {lease.FencingToken}, expires in {Duration(left)}");
            }
            else
            {
                // The lease store reports no holder once a lease expired, so the last holder is not known here.
                text = F($"{lease.Scope}: unheld, highest token {lease.HighWaterToken}");
            }

            lines.Add(text);
            if (markers.Count < MaxMarkers)
            {
                var at = new Vector3(anchor.X, anchor.Y, anchor.Z + (2f * (i + 1)));
                markers.Add(new DebugVisMarker(held ? DebugMarkerKind.PathCorner : DebugMarkerKind.PathCornerBad, at, text, Emphasis: !held));
            }
        }

        return new DebugVisView("leases", lines, markers);
    }

    /// <summary>
    /// The Scourge invasion: state and every zone with its necropolises left and next attack. A zone on <paramref name="mapId"/> with a
    /// known Mouth position gets a red marker there while the invasion is enabled and the zone still has necropolises standing (the Mouth only
    /// stands in an attacked zone).
    /// </summary>
    public static DebugVisView Invasion(InvasionSnapshot snapshot, long nowUnix, uint mapId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lines = new List<string>();
        var markers = new List<DebugVisMarker>();
        string last = snapshot.LastAttackZone == 0 ? "none yet" : ZoneName(snapshot.LastAttackZone);
        lines.Add(F($"scourge invasion: {(snapshot.Enabled ? "enabled" : "disabled")}, {snapshot.BattlesWon} battle(s) won, last attack {last}."));
        foreach (InvasionZoneEntry zone in snapshot.Zones)
        {
            var text = new StringBuilder(F($"{ZoneName(zone.ZoneId)} ({zone.ZoneId}): {zone.Remaining}/{zone.Total} necropolis(es) standing"));
            if (zone.NextAttackUnix > 0)
            {
                long wait = zone.NextAttackUnix - nowUnix;
                text.Append(wait > 0 ? F($", next attack in {Duration(wait)}") : ", next attack due");
            }

            string line = text.ToString();
            lines.Add(line);
            // The Mouth of Kel'Thuzad only stands in an attacked zone (necropolises left, invasion running; ScourgeInvasionCatalog).
            if (snapshot.Enabled && zone.Remaining > 0 && zone.MapId == mapId && zone.Mouth is { } mouth && markers.Count < MaxMarkers)
            {
                markers.Add(new DebugVisMarker(DebugMarkerKind.PathCornerBad, mouth, line + " - Mouth of Kel'Thuzad", Emphasis: true));
            }
        }

        return new DebugVisView("invasion", lines, markers);
    }

    /// <summary>Seconds as "1h 02m 03s", leading zero parts left out (negative counts as 0).</summary>
    public static string Duration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        long h = seconds / 3600;
        long m = seconds / 60 % 60;
        long s = seconds % 60;
        if (h > 0)
        {
            return F($"{h}h {m:D2}m {s:D2}s");
        }

        return m > 0 ? F($"{m}m {s:D2}s") : F($"{s}s");
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
