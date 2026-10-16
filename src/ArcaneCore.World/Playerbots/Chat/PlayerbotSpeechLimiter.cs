using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The place a line is said, for its shared budget: <see cref="Key"/> names it ("ch:1:trade - city", "local:0:12", "group:4", "guild:Foo"),
/// <see cref="Cap"/> lines in <see cref="WindowMs"/> from all bots together. A null scope (a whisper, an emote) has no shared budget.
/// </summary>
internal readonly record struct SpeechScope(string Key, int Cap, long WindowMs)
{
    internal const long MinuteMs = 60_000;

    /// <summary>
    /// A world channel's scope: <paramref name="perMinute"/> lines a minute, the minute stretched by <paramref name="botsInChannel"/> over
    /// <paramref name="crowd"/> when the channel is crowded with bots (50 bots, crowd 10: 3 lines per five minutes).
    /// </summary>
    internal static SpeechScope Channel(string key, int perMinute, int botsInChannel, int crowd)
        => new(key, perMinute, MinuteMs * Math.Max(1, (botsInChannel + Math.Max(1, crowd) - 1) / Math.Max(1, crowd)));
}

/// <summary>
/// The central rate limiter of bot speech (<see cref="PlayerbotChat"/>): per bot (a gap between lines it starts, a per-minute cap), per
/// target, per shared scope (a world channel per zone, the say/yell area, a group, a guild), the dedupe of identical lines in one place,
/// and the bot-to-bot guard (turns per exchange, lines per pair and hour, lines per minute in all). Time is milliseconds passed in by the
/// caller, so tests drive it by hand. Thread-safe (one lock; the world thread is its only caller in practice).
/// </summary>
internal sealed class PlayerbotSpeechLimiter
{
    private const long HourMs = 3_600_000;
    private const int MaxEntries = 50_000;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, long> _botLast = [];
    private readonly Dictionary<Guid, Queue<long>> _botMinute = [];
    private readonly Dictionary<(Guid Bot, ulong Target), long> _target = [];
    private readonly Dictionary<string, Queue<long>> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _said = new(StringComparer.Ordinal);
    private readonly Dictionary<(ulong A, ulong B), Pair> _pairs = [];
    private readonly Queue<long> _botToBot = new();
    private long _sweptAtMs = long.MinValue / 2;

    /// <summary>Lines taken so far (tests and status).</summary>
    internal long Taken { get; private set; }

    /// <summary>
    /// The turn a bot's answer to another bot would have now (1 for a new exchange), or 0 when the exchange may not go on: bot-to-bot
    /// talk is off, its turns are spent, or the pair's hourly lines or the global minute are spent. Nothing is taken.
    /// </summary>
    internal int NextBotTurn(ObjectGuid speaker, ObjectGuid listener, PlayerbotSpeechOptions options, long nowMs)
    {
        if (!options.BotToBot || options.BotToBotMaxTurns <= 0) return 0;
        lock (_gate)
        {
            int turn = 1;
            if (_pairs.TryGetValue(PairKey(speaker, listener), out Pair? pair))
            {
                if (nowMs - pair.LastMs < options.BotToBotQuietSeconds * 1000L) turn = pair.Turns + 1;
                if (Count(pair.Hour, nowMs, HourMs) >= options.BotToBotPairLinesPerHour) return 0;
            }
            else if (options.BotToBotPairLinesPerHour <= 0)
            {
                return 0;
            }

            if (turn > options.BotToBotMaxTurns || Count(_botToBot, nowMs, SpeechScope.MinuteMs) >= options.BotToBotLinesPerMinute) return 0;
            return turn;
        }
    }

    /// <summary>The checks of <see cref="TryTake"/> that need no place (bot gap and cap, target gap, bot-to-bot), taking nothing.</summary>
    internal BotSpeechAdmission Peek(Guid bot, ObjectGuid botGuid, BotSpeech speech, PlayerbotSpeechOptions options, long nowMs)
    {
        lock (_gate) return Check(bot, botGuid, speech, null, options, nowMs, peekOnly: true);
    }

    /// <summary>Every check; when the line may be said, record it (the bot, the target, the scope, the dedupe, the bot-to-bot counts).</summary>
    internal BotSpeechAdmission TryTake(Guid bot, ObjectGuid botGuid, BotSpeech speech, SpeechScope? scope, PlayerbotSpeechOptions options, long nowMs)
    {
        lock (_gate)
        {
            Sweep(nowMs, options);
            BotSpeechAdmission admission = Check(bot, botGuid, speech, scope, options, nowMs, peekOnly: false);
            if (admission != BotSpeechAdmission.Queued) return admission;
            Record(bot, botGuid, speech, scope, options, nowMs);
            Taken++;
            return BotSpeechAdmission.Queued;
        }
    }

    private BotSpeechAdmission Check(Guid bot, ObjectGuid botGuid, BotSpeech speech, SpeechScope? scope, PlayerbotSpeechOptions options, long nowMs, bool peekOnly)
    {
        bool high = speech.Priority == BotSpeechPriority.High;
        if (_botMinute.TryGetValue(bot, out Queue<long>? minute) && Count(minute, nowMs, SpeechScope.MinuteMs) >= options.BotLinesPerMinute)
            return BotSpeechAdmission.Cooldown;
        if (!high && !speech.IsReply && _botLast.TryGetValue(bot, out long last) && nowMs - last < options.BotGapSeconds * 1000L)
            return BotSpeechAdmission.Cooldown;
        if (!high && !speech.TargetGuid.IsEmpty && _target.TryGetValue((bot, speech.TargetGuid.Value), out long told)
            && nowMs - told < options.TargetGapSeconds * 1000L)
            return BotSpeechAdmission.Target;
        if (speech.ToBot)
        {
            if (!options.BotToBot || speech.Turn > options.BotToBotMaxTurns || options.BotToBotMaxTurns <= 0) return BotSpeechAdmission.BotToBot;
            if (Count(_botToBot, nowMs, SpeechScope.MinuteMs) >= options.BotToBotLinesPerMinute) return BotSpeechAdmission.BotToBot;
            if (_pairs.TryGetValue(PairKey(botGuid, speech.TargetGuid), out Pair? pair) && Count(pair.Hour, nowMs, HourMs) >= options.BotToBotPairLinesPerHour)
                return BotSpeechAdmission.BotToBot;
            if (options.BotToBotPairLinesPerHour <= 0) return BotSpeechAdmission.BotToBot;
        }

        if (peekOnly) return BotSpeechAdmission.Queued;
        string place = scope?.Key ?? PrivateKey(bot, speech);
        if (options.DedupeSeconds > 0 && _said.TryGetValue(DedupeKey(place, speech), out long said) && nowMs - said < options.DedupeSeconds * 1000L)
            return BotSpeechAdmission.Duplicate;
        if (scope is { } shared)
        {
            int cap = high ? shared.Cap + 1 : shared.Cap;
            if (shared.Cap <= 0 || (_scopes.TryGetValue(shared.Key, out Queue<long>? window) && Count(window, nowMs, shared.WindowMs) >= cap))
                return BotSpeechAdmission.Budget;
        }

        return BotSpeechAdmission.Queued;
    }

    private void Record(Guid bot, ObjectGuid botGuid, BotSpeech speech, SpeechScope? scope, PlayerbotSpeechOptions options, long nowMs)
    {
        if (!speech.IsReply) _botLast[bot] = nowMs;
        Push(_botMinute, bot, nowMs, SpeechScope.MinuteMs);
        if (!speech.TargetGuid.IsEmpty) _target[(bot, speech.TargetGuid.Value)] = nowMs;
        string place = scope?.Key ?? PrivateKey(bot, speech);
        if (options.DedupeSeconds > 0) _said[DedupeKey(place, speech)] = nowMs;
        if (scope is { } shared) Push(_scopes, shared.Key, nowMs, shared.WindowMs);
        if (speech.ToBot)
        {
            Trim(_botToBot, nowMs, SpeechScope.MinuteMs);
            _botToBot.Enqueue(nowMs);
            (ulong, ulong) key = PairKey(botGuid, speech.TargetGuid);
            if (!_pairs.TryGetValue(key, out Pair? pair)) _pairs[key] = pair = new Pair();
            if (nowMs - pair.LastMs >= options.BotToBotQuietSeconds * 1000L) pair.Turns = 0;
            pair.Turns = Math.Max(pair.Turns + 1, speech.Turn);
            pair.LastMs = nowMs;
            Trim(pair.Hour, nowMs, HourMs);
            pair.Hour.Enqueue(nowMs);
        }
    }

    /// <summary>Drop what no window can see any more (once a minute), and everything if a table runs away.</summary>
    private void Sweep(long nowMs, PlayerbotSpeechOptions options)
    {
        if (nowMs - _sweptAtMs < SpeechScope.MinuteMs && _said.Count < MaxEntries && _scopes.Count < MaxEntries) return;
        _sweptAtMs = nowMs;
        long gap = Math.Max(options.BotGapSeconds, options.TargetGapSeconds) * 1000L;
        foreach (KeyValuePair<Guid, long> entry in _botLast.ToArray()) if (nowMs - entry.Value >= gap) _botLast.Remove(entry.Key);
        foreach (KeyValuePair<(Guid, ulong), long> entry in _target.ToArray()) if (nowMs - entry.Value >= gap) _target.Remove(entry.Key);
        foreach (KeyValuePair<Guid, Queue<long>> entry in _botMinute.ToArray())
        {
            Trim(entry.Value, nowMs, SpeechScope.MinuteMs);
            if (entry.Value.Count == 0) _botMinute.Remove(entry.Key);
        }

        foreach (KeyValuePair<string, Queue<long>> entry in _scopes.ToArray())
        {
            // A stretched channel window can be longer than an hour only for absurd crowds; an hour bounds what is kept.
            Trim(entry.Value, nowMs, HourMs);
            if (entry.Value.Count == 0) _scopes.Remove(entry.Key);
        }

        long dedupe = options.DedupeSeconds * 1000L;
        foreach (KeyValuePair<string, long> entry in _said.ToArray()) if (nowMs - entry.Value >= dedupe) _said.Remove(entry.Key);
        long quiet = Math.Max(options.BotToBotQuietSeconds * 1000L, HourMs);
        foreach (KeyValuePair<(ulong, ulong), Pair> entry in _pairs.ToArray()) if (nowMs - entry.Value.LastMs >= quiet) _pairs.Remove(entry.Key);
        if (_said.Count >= MaxEntries) _said.Clear();
        if (_scopes.Count >= MaxEntries) _scopes.Clear();
    }

    private static (ulong A, ulong B) PairKey(ObjectGuid a, ObjectGuid b) => a.Value <= b.Value ? (a.Value, b.Value) : (b.Value, a.Value);

    /// <summary>A line with no shared scope (a whisper, an emote) is deduped per bot and target.</summary>
    private static string PrivateKey(Guid bot, BotSpeech speech) => $"p:{bot:N}:{speech.Channel}:{speech.TargetGuid.Value:X}";

    private static string DedupeKey(string place, BotSpeech speech)
        => place + "\n" + (speech.Channel == BotSpeechChannel.Emote
            ? speech.TextEmote.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : speech.Text.Trim().ToLowerInvariant());

    private static int Count(Queue<long> window, long nowMs, long windowMs)
    {
        int used = 0;
        foreach (long at in window) if (nowMs - at < windowMs) used++;
        return used;
    }

    private static void Push<TKey>(Dictionary<TKey, Queue<long>> table, TKey key, long nowMs, long windowMs) where TKey : notnull
    {
        if (!table.TryGetValue(key, out Queue<long>? window)) table[key] = window = new Queue<long>();
        Trim(window, nowMs, Math.Max(windowMs, SpeechScope.MinuteMs));
        window.Enqueue(nowMs);
    }

    private static void Trim(Queue<long> window, long nowMs, long windowMs)
    {
        while (window.Count > 0 && nowMs - window.Peek() >= windowMs) window.Dequeue();
    }

    private sealed class Pair
    {
        public int Turns;
        public long LastMs = long.MinValue / 2;
        public Queue<long> Hour { get; } = new();
    }
}
