namespace ArcaneCore.Kernel.Configuration;

/// <summary>How serious an announcement is. Ordered: a category's <c>MinimumSeverity</c> lets this level and anything above it through.</summary>
public enum AnnounceSeverity
{
    /// <summary>Routine, expected (a swap applied).</summary>
    Info = 0,

    /// <summary>Something a staff member should look at (a swap rejected).</summary>
    Warning = 1,

    /// <summary>Something failed.</summary>
    Error = 2,

    /// <summary>The server or its data is at risk.</summary>
    Critical = 3,
}

/// <summary>
/// The operations announcement service, bound from "World:Announce". It tells online staff (an on-screen
/// notification plus a chat line, to every account above Player) and optionally every player (a system
/// chat line) when something operational happens. Every category has its own switches, severity floor and
/// rate limit, so one noisy source cannot flood the others. Every announcement is also always logged and
/// counted (<c>arcanecore.announce</c>), whatever these switches say.
/// </summary>
public sealed class AnnounceOptions
{
    public const string SectionName = "World:Announce";

    /// <summary>Master switch for delivery to players and staff. When false announcements are still logged and counted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Live-change announcements: data reloads, hot modules and code hot reload (docs/areas/hot-swap-announcements.md).</summary>
    public HotSwapAnnounceOptions HotSwap { get; set; } = new();
}

/// <summary>Switches every announcement category has.</summary>
public class AnnounceCategoryOptions
{
    /// <summary>Send the announcement to every online account above Player (an on-screen notification and a chat line). Staff only.</summary>
    public bool GmNotify { get; set; } = true;

    /// <summary>Send a short system chat line to every online player. Never carries account names; never carries character names unless <see cref="AllowNamesInWorldMessage"/>.</summary>
    public bool WorldMessage { get; set; }

    /// <summary>Add the staff-only detail (who, how long, counts, hashes) to the staff chat line. Players never see it.</summary>
    public bool IncludeDetails { get; set; } = true;

    /// <summary>Announcements below this severity are logged and counted but not delivered.</summary>
    public AnnounceSeverity MinimumSeverity { get; set; } = AnnounceSeverity.Info;

    /// <summary>Rate limit: how many announcements of this category may go out back to back before the limit applies. 0 disables the limit.</summary>
    public int RateBurst { get; set; } = 10;

    /// <summary>Rate limit: announcements of this category refilled per minute once the burst is used. Suppressed ones are counted and reported on the next delivered announcement.</summary>
    public int RatePerMinute { get; set; } = 20;

    /// <summary>An announcement identical to one delivered this many seconds ago is suppressed. 0 disables the window.</summary>
    public int DedupSeconds { get; set; }

    /// <summary>Let the world message carry character names when the announcing code supplies a text for it. Off by default.</summary>
    public bool AllowNamesInWorldMessage { get; set; }
}

/// <summary>The hot swap category: data reloads (<c>.reload</c>), hot modules (<c>.hotmodule</c>) and code hot reload.</summary>
public sealed class HotSwapAnnounceOptions : AnnounceCategoryOptions
{
    /// <summary>Lanes to announce: any of Data, Module, Code. Empty (the default) announces all of them. A lane that is not turned on never reports anything.</summary>
    public List<string> Lanes { get; set; } = [];

    /// <summary>Also send the world message for a swap that was rejected or failed. Default false: players only hear about changes that happened.</summary>
    public bool WorldMessageOnFailure { get; set; }

    /// <summary>Put the free-text reason the staff member gave (<c>.reload creature_template fixed drop rates</c>) into the world message. The reason is shown to players as typed.</summary>
    public bool WorldMessageIncludeReason { get; set; } = true;
}
