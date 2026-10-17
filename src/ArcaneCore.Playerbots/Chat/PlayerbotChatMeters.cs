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

    /// <summary>Lines bots said, by <c>channel</c> (Say, Party, World, Emote, ...) and <c>topic</c> (reply, greet, lfg, ...).</summary>
    internal static Counter<long> LinesSpoken { get; } = Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.lines_spoken", "{line}", "Chat lines and emotes bots said through their own sessions.");

    /// <summary>Lines bots did not say, by <c>channel</c> and <c>reason</c> (Cooldown, Budget, Duplicate, NoListener, BotToBot, ...).</summary>
    internal static Counter<long> LinesDropped { get; } = Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.lines_dropped", "{line}", "Bot chat lines stopped by the speech limits, the safety screening or the delivery checks.");

    internal static void CountSpoken(BotSpeechChannel channel, string topic)
    {
        if (LinesSpoken.Enabled)
            LinesSpoken.Add(1, new KeyValuePair<string, object?>("channel", channel.ToString()), new KeyValuePair<string, object?>("topic", topic));
    }

    internal static void CountDropped(BotSpeechChannel channel, BotSpeechAdmission reason)
    {
        if (LinesDropped.Enabled)
            LinesDropped.Add(1, new KeyValuePair<string, object?>("channel", channel.ToString()), new KeyValuePair<string, object?>("reason", reason.ToString()));
    }
}
