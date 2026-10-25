using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>One character near a bot, as <see cref="PlayerbotEventListeners.Evaluate"/> sees it: a real player or not, how far, and whether it has put the bot on its ignore list.</summary>
internal readonly record struct PlayerbotListenerCandidate(ulong Guid, bool IsRealPlayer, float Yards, bool IgnoresBot);

/// <summary>Whether a festival line or emote may be made (<see cref="PlayerbotEventListeners.Evaluate"/>).</summary>
internal enum PlayerbotListenVerdict : byte
{
    /// <summary>At least one real player hears it and none of them stands in the way.</summary>
    Heard,

    /// <summary>No real player is within range: a remark is for someone to hear.</summary>
    NoListener,

    /// <summary>A listener has put the bot on its ignore list.</summary>
    Ignored,

    /// <summary>A listener is cut off by the chat safety layer.</summary>
    CutOff,
}

/// <summary>Who hears a line or an emote, and what the verdict is. <see cref="BuiltinOnly"/>: a listener said "ai off", so no model may write the line.</summary>
internal readonly record struct PlayerbotListenerResult(PlayerbotListenVerdict Verdict, IReadOnlyList<ulong> Listeners, bool BuiltinOnly);

/// <summary>
/// Who hears a festival line or emote (docs/areas/playbots-social.md, World events): every real player of either faction within the range
/// of the channel (a /say or a text emote reaches the other faction as well), never a bot. Pure over the characters near the bot; the
/// caller gathers them. Any thread.
/// </summary>
internal static class PlayerbotEventListeners
{
    private static readonly IReadOnlyList<ulong> None = [];

    /// <summary>
    /// The listeners among <paramref name="candidates"/> within <paramref name="rangeYards"/> (inclusive). The verdict is
    /// <see cref="PlayerbotListenVerdict.NoListener"/> without any, else <see cref="PlayerbotListenVerdict.Ignored"/> when one of them ignores
    /// the bot, else <see cref="PlayerbotListenVerdict.CutOff"/> when one is cut off (<c>CutoffSeconds &gt; 0</c> in <paramref name="safetyOf"/>),
    /// else <see cref="PlayerbotListenVerdict.Heard"/>; one who said "ai off" (<c>AiReplies == false</c>) makes the line built-in only for
    /// everyone. Safety is looked up for listeners only, never for a bot or a player out of range.
    /// </summary>
    internal static PlayerbotListenerResult Evaluate(IReadOnlyList<PlayerbotListenerCandidate> candidates, float rangeYards, Func<ulong, BotChatPlayerSafety?> safetyOf)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(safetyOf);
        List<ulong>? listeners = null;
        bool ignored = false, cutOff = false, builtinOnly = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            PlayerbotListenerCandidate candidate = candidates[i];
            if (!candidate.IsRealPlayer || !(candidate.Yards <= rangeYards)) continue;
            (listeners ??= []).Add(candidate.Guid);
            ignored |= candidate.IgnoresBot;
            if (safetyOf(candidate.Guid) is not { } safety) continue;
            cutOff |= safety.CutoffSeconds > 0;
            builtinOnly |= safety.AiReplies == false;
        }

        if (listeners is null) return new PlayerbotListenerResult(PlayerbotListenVerdict.NoListener, None, false);
        PlayerbotListenVerdict verdict = ignored ? PlayerbotListenVerdict.Ignored : cutOff ? PlayerbotListenVerdict.CutOff : PlayerbotListenVerdict.Heard;
        return new PlayerbotListenerResult(verdict, listeners, builtinOnly);
    }
}

/// <summary>Why <see cref="PlayerbotEmoteBudget.TryTake"/> sent or refused an emote.</summary>
internal enum PlayerbotEmoteVerdict : byte
{
    /// <summary>The emote may be sent; every listener and the anchor were charged.</summary>
    Sent,

    /// <summary>A listener has had its festival emotes for the minute.</summary>
    ListenerBudget,

    /// <summary>The anchor has had its festival emotes for the minute.</summary>
    AnchorBudget,
}

/// <summary>
/// The festival emote budgets: each real player in range gets at most <c>EmotesPerListenerMinute</c> a minute from all bots together, each
/// anchor at most <c>EmotesPerAnchorMinute</c>, listeners or not. An emote is sent only when every listener and the anchor have room, and
/// sending charges all of them; a refused emote charges nobody. Sliding windows of one minute on world time. World thread.
/// </summary>
internal sealed class PlayerbotEmoteBudget
{
    /// <summary>The window of both budgets (ms).</summary>
    internal const long WindowMs = 60_000;

    private readonly PlayerbotSocialRateLimiter<ulong> _listeners = new();
    private readonly PlayerbotSocialRateLimiter<ulong> _anchors = new();

    /// <summary>
    /// Whether an emote at <paramref name="anchor"/> heard by <paramref name="listeners"/> may be sent at <paramref name="nowMs"/>, and if so
    /// charge it. The limits are the arguments, so a live change applies to the emotes already sent. A limit of 0 refuses; with no listener
    /// the listener limit has nobody to charge and only the anchor's applies.
    /// </summary>
    internal PlayerbotEmoteVerdict TryTake(ulong anchor, IReadOnlyList<ulong> listeners, long nowMs, int perListener, int perAnchor)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        for (int i = 0; i < listeners.Count; i++)
            if (!_listeners.HasRoom(listeners[i], perListener, nowMs, WindowMs)) return PlayerbotEmoteVerdict.ListenerBudget;
        if (!_anchors.HasRoom(anchor, perAnchor, nowMs, WindowMs)) return PlayerbotEmoteVerdict.AnchorBudget;
        for (int i = 0; i < listeners.Count; i++) _listeners.TryTake(listeners[i], perListener, nowMs, WindowMs);
        _anchors.TryTake(anchor, perAnchor, nowMs, WindowMs);
        return PlayerbotEmoteVerdict.Sent;
    }

    /// <summary>Drop the keys with nothing in their window, so a long session does not grow the tables.</summary>
    internal void Sweep(long nowMs)
    {
        _listeners.Sweep(nowMs, WindowMs);
        _anchors.Sweep(nowMs, WindowMs);
    }
}
