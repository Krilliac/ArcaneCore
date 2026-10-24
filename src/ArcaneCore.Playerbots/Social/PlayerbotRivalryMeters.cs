using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The rivalry instruments (docs/ops/metrics.md). The meter's name starts with <see cref="ArcaneMeters.Prefix"/>, so the metrics store collects it
/// when <c>Ops:Metrics:Enabled</c> is on; with no listener a count is a single enabled check. Label sets are closed.
/// </summary>
internal static class PlayerbotRivalryMeters
{
    /// <summary>The rivalry meter.</summary>
    internal static Meter Meter { get; } = new("ArcaneCore.Playerbots.Rivalry", "1.0");

    /// <summary>Emotes of the rivalry stage, by <c>kind</c>: <c>greet</c> and <c>rude</c> (the lane), <c>reply_rude</c> and <c>reply_shrug</c> (answers to an emote aimed at a bot).</summary>
    internal static Counter<long> Emotes { get; } = Meter.CreateCounter<long>(
        "arcanecore.playerbot.rivalry_emotes", "{emote}", "Emotes bots sent to the other faction and rude or shrugging answers to emotes.");

    /// <summary>Opt-out commands applied, by <c>change</c>: <c>off</c> (rivals off) and <c>on</c> (rivals on).</summary>
    internal static Counter<long> OptOuts { get; } = Meter.CreateCounter<long>(
        "arcanecore.playerbot.rivalry_optouts", "{change}", "Whispered rivals off and rivals on commands applied.");

    internal static void CountEmote(string kind)
    {
        if (Emotes.Enabled) Emotes.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    internal static void CountOptOut(string change)
    {
        if (OptOuts.Enabled) OptOuts.Add(1, new KeyValuePair<string, object?>("change", change));
    }
}
