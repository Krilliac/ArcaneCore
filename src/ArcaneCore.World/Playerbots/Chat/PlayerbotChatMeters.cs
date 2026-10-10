using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The bot chat instruments (docs/ops/metrics.md). The meter's name starts with <see cref="ArcaneMeters.Prefix"/>, so the metrics
/// store collects it when <c>Ops:Metrics:Enabled</c> is on; with no listener a count is a single enabled check.
/// </summary>
internal static class PlayerbotChatMeters
{
    /// <summary>The bot chat meter.</summary>
    internal static Meter Meter { get; } = new("ArcaneCore.Playerbots.Chat", "1.0");

    /// <summary>
    /// Safety patterns that ran out of time, by <c>kind</c> (<c>input</c>: screening a player's line, <c>output</c>: screening a model
    /// reply, <c>strip</c>: removing personal data before sending).
    /// </summary>
    internal static Counter<long> ScreeningTimeouts { get; } = Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.screening_timeouts", "{screening}", "Bot chat safety screenings that ran out of time.");

    internal static void CountScreeningTimeout(string kind)
    {
        if (ScreeningTimeouts.Enabled) ScreeningTimeouts.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }
}
