using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>
/// The world-event instruments (docs/ops/metrics.md): <c>arcanecore.playerbot.events{kind,outcome}</c>, <c>arcanecore.playerbot.event_lines{kind}</c>
/// and <c>arcanecore.playerbot.event_suppressed{reason}</c>. The meter's name starts with <see cref="ArcaneMeters.Prefix"/>, so the metrics store
/// collects it when <c>Ops:Metrics:Enabled</c> is on; with no listener a count is a single enabled check. Every label set is closed (the
/// constants below), so a cap that never lets anything through is visible as a count of its reason.
/// </summary>
internal static class PlayerbotEventMeters
{
    /// <summary>The meter.</summary>
    internal static Meter Meter { get; } = new("ArcaneCore.Playerbots.Events", "1.0");

    /// <summary>Festival and event errands by <c>kind</c> and <c>outcome</c>.</summary>
    internal static Counter<long> Events { get; } = Meter.CreateCounter<long>("arcanecore.playerbot.events", "{event}", "Bot event activities by kind and outcome.");

    /// <summary>Lines bots said about an event, by <c>kind</c>.</summary>
    internal static Counter<long> Lines { get; } = Meter.CreateCounter<long>("arcanecore.playerbot.event_lines", "{line}", "Lines bots said about world events, by kind.");

    /// <summary>Lines and emotes not made, by <c>reason</c>.</summary>
    internal static Counter<long> Suppressed { get; } = Meter.CreateCounter<long>("arcanecore.playerbot.event_suppressed", "{line}", "Event lines and emotes not made, by reason.");

    /// <summary>The kind of a festival visit (a bot took a place at an anchor and stood there).</summary>
    internal const string FestivalVisit = "festival_visit";

    /// <summary>A festival visit that ended because the spot could not be reached.</summary>
    internal const string Unreachable = "unreachable";

    /// <summary>A festival visit that ended because the festival ended or the bot went on.</summary>
    internal const string Done = "done";

    /// <summary>A festival visit that was refused because a crowd limit was full.</summary>
    internal const string Capped = "capped";

    /// <summary>A festival visit that ended because the bot was attacked there, or the place went away under it.</summary>
    internal const string Abandoned = "abandoned";

    /// <summary>The line kind of an arrival line.</summary>
    internal const string Arrive = "arrive";

    /// <summary>The line kind of a celebration text emote.</summary>
    internal const string Emote = "emote";

    /// <summary>The line kind of the "an event has started" remark.</summary>
    internal const string Comment = "comment";

    internal const string NoListener = "no_listener";
    internal const string ListenerCap = "listener_cap";
    internal const string CutOff = "cut_off";
    internal const string Ignored = "ignored";
    internal const string EmoteListenerBudget = "emote_listener_budget";
    internal const string EmoteAnchorBudget = "emote_anchor_budget";

    internal static void Event(string kind, string outcome)
    {
        if (Events.Enabled) Events.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("outcome", outcome));
    }

    internal static void Line(string kind)
    {
        if (Lines.Enabled) Lines.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    internal static void Suppress(string reason)
    {
        if (Suppressed.Enabled) Suppressed.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }
}
