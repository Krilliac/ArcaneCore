using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>Why a line was or was not taken for a reply (<see cref="PlayerbotChat.TryAsk"/>).</summary>
internal enum BotChatAdmission
{
    Queued,

    /// <summary>Chat is off (<see cref="PlayerbotChatOptions.Enabled"/>).</summary>
    Disabled,

    /// <summary>The channel is not in <see cref="PlayerbotChatOptions.Channels"/>.</summary>
    Channel,

    /// <summary>The speaker had a reply less than <see cref="PlayerbotChatOptions.PerPlayerCooldownSeconds"/> ago.</summary>
    Cooldown,

    /// <summary>No provider can answer now (every hourly cap spent, or every model provider cooling down or unkeyed).</summary>
    Budget,

    /// <summary>The queue is full.</summary>
    QueueFull,

    /// <summary>A whispered "ai on", "ai off" or "ai": answered by the safety itself, never sent to a provider.</summary>
    Choice,
}

/// <summary>One provider's line of <c>.playerbot chat status</c>. Never holds the key: only whether its variable is set.</summary>
internal sealed record BotChatProviderStatus(
    int Index, PlayerbotChatProviderKind Kind, string Model, string? KeyVariable, bool KeyPresent, int RepliesThisHour,
    int MaxRepliesPerHour, long Replies, long Errors, string? LastError, int CooldownSeconds, bool Priced);

/// <summary><c>.playerbot chat status</c>.</summary>
internal sealed record BotChatStatus(
    bool Enabled, PlayerbotChatChannels Channels, int Queued, long Answered, long Dropped, double SpentTodayUsd, double MaxDailySpendUsd,
    IReadOnlyList<BotChatProviderStatus> Providers, BotChatSafetyStatus Safety);

/// <summary>
/// The bots' chat service (docs/areas/playbots.md, Bot chat). The world thread hands it a line (<see cref="TryAsk"/>: cheap checks
/// and a bounded queue, never I/O); background workers try the providers in order (each with the timeout, its hourly cap, the daily
/// spend cap and a back-off after 429/5xx/timeouts) and put the reply on a queue the world thread drains (<see cref="DrainReplies"/>)
/// and says through the bot's ordinary CMSG_MESSAGECHAT. A failure only means no reply: it never reaches the bot's behaviour.
/// Keys are read from the environment at each request and never logged or shown.
/// <para>
/// Safety (<see cref="PlayerbotChatSafety"/>, docs/areas/playbots.md, Safety and provider policies): every line is screened by the
/// local filter before any model provider is tried, and by a provider's moderation step before that provider sees it; a flagged
/// line goes to no provider and gets the built-in brush-off (or nothing). A model's reply is screened before it is said. A player
/// who is cut off, opted out, or not opted in under RequireOptIn gets built-in replies only. Only the line and the remembered
/// exchanges are sent, under character names, with emails and phone numbers replaced.
/// </para>
/// </summary>
internal sealed partial class PlayerbotChat : IAsyncDisposable
{
    internal const int Workers = 2;
    internal const int MaxConversations = 512;
    internal const int MaxRepliesPerDrain = 32;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly PlayerbotOptions _options;
    private readonly IBotChatClient _client;
    private readonly IBotChatEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderState> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, DateTimeOffset> _lastReplyTo = [];
    private readonly Dictionary<Guid, List<InFlight>> _inFlight = [];
    private readonly Dictionary<(Guid Bot, ulong Player), Conversation> _conversations = [];
    private readonly ConcurrentQueue<BotChatReply> _replies = new();

    private readonly ConcurrentQueue<BotSpeech> _lostSpeech = new();

    /// <summary>World thread: tell a line's owner that it will never be said (<see cref="BotSpeech.Dropped"/>); a fault in the callback is recorded, not thrown.</summary>
    internal void Lost(BotSpeech? speech)
    {
        if (speech?.Dropped is not { } dropped) return;
        try
        {
            dropped();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat", error);
            _logger.LogWarning(error, "Bot chat: a speech-dropped handler failed");
        }
    }
    private readonly Random _random = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly PlayerbotChatSafety _safety;
    private readonly PlayerbotSpeechLimiter _limiter = new();
    private readonly Dictionary<string, long> _heard = new(StringComparer.Ordinal);
    private int _pendingSpeech;
    private long _spoken;
    private long _speechDropped;
    private Channel<BotChatAsk>? _queue;
    private Task[] _workers = [];
    private int _queued;
    private long _answered;
    private long _dropped;
    private DateOnly _spendDay;
    private double _spentToday;
    private int _disposed;

    internal PlayerbotChat(PlayerbotOptions options, IBotChatClient client, IBotChatEnvironment environment, TimeProvider time, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _safety = new PlayerbotChatSafety(() => _options.Chat, time, logger)
        {
            OnStrike = ask => Social?.NoteById(ask.BotId, ask.BotGuid, ask.SenderGuid, BotSocial.PlayerbotSocialEvent.ChatStrike),
        };
    }

    private PlayerbotChatOptions Options => _options.Chat;

    /// <summary>The safety state: flags, strikes, cut-offs, choices (<c>.playerbot chat flags</c> and <c>pardon</c>).</summary>
    internal PlayerbotChatSafety Safety => _safety;

    /// <summary>Whether bots answer chat now (live: <c>.reload config</c>). Off: every line takes the fixed path of before.</summary>
    internal bool IsActive => Volatile.Read(ref _disposed) == 0 && Options.Enabled;

    /// <summary>The speech options (<c>World:Playerbots:Chat:Speech</c>, live).</summary>
    internal PlayerbotSpeechOptions SpeechOptions => Options.Speech;

    /// <summary>The bot-to-bot conversations and world-channel reactions (<see cref="PlayerbotChatOptions.Conversation"/>).</summary>
    internal PlayerbotConversationOptions ConversationOptions => Options.Conversation;

    /// <summary>The bot voice options (<c>World:Playerbots:Chat:Voice</c>, live).</summary>
    internal PlayerbotVoiceOptions VoiceOptions => Options.Voice;

    /// <summary>The central rate limiter of bot speech (world thread; tests).</summary>
    internal PlayerbotSpeechLimiter Limiter => _limiter;

    /// <summary>Lines bots have said through <see cref="NoteSpoken"/> so far.</summary>
    internal long Spoken => Interlocked.Read(ref _spoken);

    /// <summary>Lines refused by <see cref="TrySpeak"/> or stopped at delivery (<see cref="NoteDropped"/>).</summary>
    internal long SpeechDropped => Interlocked.Read(ref _speechDropped);

    /// <summary>The chat clock in milliseconds (the limiter's time).</summary>
    internal long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// Whether <paramref name="channel"/> is one the bots answer. A raid or raid warning line is answered while <c>Raid</c> or
    /// <c>Party</c> is listed (raid lines were answered under Party before Raid existed).
    /// </summary>
    internal bool Answers(ChatType channel)
    {
        PlayerbotChatChannels wanted = channel switch
        {
            ChatType.Whisper => PlayerbotChatChannels.Whisper,
            ChatType.Party => PlayerbotChatChannels.Party,
            ChatType.Raid or ChatType.RaidLeader or ChatType.RaidWarning => PlayerbotChatChannels.Raid | PlayerbotChatChannels.Party,
            ChatType.Say => PlayerbotChatChannels.Say,
            ChatType.Yell => PlayerbotChatChannels.Yell,
            ChatType.Battleground or ChatType.BattlegroundLeader => PlayerbotChatChannels.Battleground,
            ChatType.Guild => PlayerbotChatChannels.Guild,
            ChatType.Channel => PlayerbotChatChannels.World,
            _ => PlayerbotChatChannels.None,
        };
        return wanted != PlayerbotChatChannels.None && (Options.Channels & wanted) != 0;
    }

    // --- speech -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// World thread: a line a bot says on its own (a topic, a need, an LFG call, an emote) or for another lane. The line is checked
    /// (chat and speech on, a text or an emote, a target or a channel name where one is needed), screened by the chat safety's output
    /// filter like a model's reply, cleaned to the client's 255 characters, peeked against the bot's own limits
    /// (<see cref="PlayerbotSpeechLimiter.Peek"/>) and queued; the world thread's drain says it through the bot's session as a client's
    /// chat packet after the place checks (a listener, the channel's budget, the dedupe: <see cref="PlayerbotSpeechDelivery"/>). Queued
    /// means it reached the queue, not that it will be heard.
    /// </summary>
    internal BotSpeechAdmission TrySpeak(Guid botId, ObjectGuid botGuid, BotSpeech speech)
    {
        ArgumentNullException.ThrowIfNull(speech);
        PlayerbotSpeechOptions options = SpeechOptions;
        if (!IsActive || !options.Enabled) return BotSpeechAdmission.Disabled;
        string text = string.Empty;
        if (speech.Channel == BotSpeechChannel.Emote)
        {
            if (speech.TextEmote == 0) return BotSpeechAdmission.Empty;
        }
        else
        {
            if (PlayerbotChatPrompts.Sanitize(speech.Text) is not { } clean) return BotSpeechAdmission.Empty;
            text = clean;
            if (speech.Channel == BotSpeechChannel.Whisper && string.IsNullOrWhiteSpace(speech.Target)) return BotSpeechAdmission.Empty;
            if (speech.Channel == BotSpeechChannel.World && string.IsNullOrWhiteSpace(speech.ChannelName)) return BotSpeechAdmission.Empty;
            PlayerbotChatSafetyOptions safety = Options.Safety;
            if (safety.Enabled && safety.ScreenOutput && _safety.ScreenOutput(text) is { } verdict && (verdict.Flagged || verdict.TimedOut))
                return Refused(speech, BotSpeechAdmission.Flagged);
        }

        BotSpeechAdmission peek = _limiter.Peek(botId, botGuid, speech, options, NowMs);
        if (peek != BotSpeechAdmission.Queued) return Refused(speech, peek);
        if (Interlocked.Increment(ref _pendingSpeech) > options.MaxQueuedLines)
        {
            Interlocked.Decrement(ref _pendingSpeech);
            return Refused(speech, BotSpeechAdmission.QueueFull);
        }

        BotSpeech said = speech with { Text = text };
        _replies.Enqueue(new BotChatReply(botId, speech.TargetGuid, speech.Target ?? string.Empty, said.ChatType, text.Length == 0 ? null : text, null,
            AcknowledgeCommand: false, SpeechProvider, speech.Guild, said));
        return BotSpeechAdmission.Queued;
    }

    /// <summary>The <see cref="BotChatReply.Provider"/> of a <see cref="TrySpeak"/> line.</summary>
    internal const string SpeechProvider = "Speech";

    private BotSpeechAdmission Refused(BotSpeech speech, BotSpeechAdmission why)
    {
        NoteDropped(speech, why);
        return why;
    }

    /// <summary>
    /// The turn a bot's answer to another bot would have now (1 for a new exchange), or 0 when bots must not answer (bot-to-bot talk off,
    /// the exchange's turns spent, the pair's or the global budget spent). World thread; nothing is taken.
    /// </summary>
    internal int NextBotTurn(ObjectGuid speaker, ObjectGuid listener) => _limiter.NextBotTurn(speaker, listener, SpeechOptions, NowMs);

    /// <summary>World thread, at delivery: count and log a line a bot said (bot, channel, topic and length; never the text).</summary>
    internal void NoteSpoken(string bot, BotSpeech speech, int length)
    {
        Interlocked.Increment(ref _spoken);
        PlayerbotChatMeters.CountSpoken(speech.Channel, speech.Topic);
        _logger.LogDebug("Bot {Bot} says {Channel}{Where} ({Topic}, {Length} chars)", bot, speech.Channel,
            speech.ChannelName is { } name ? " in " + name : string.Empty, speech.Topic, length);
        Settle(speech, BotSpeechAdmission.Queued);
    }

    /// <summary>Count a line that was not said, by reason (<see cref="PlayerbotChatMeters.CountDropped"/>).</summary>
    internal void NoteDropped(BotSpeech speech, BotSpeechAdmission why)
    {
        Interlocked.Increment(ref _speechDropped);
        PlayerbotChatMeters.CountDropped(speech.Channel, why);
        Lost(speech);
        Settle(speech, why);
    }

    /// <summary>
    /// World thread: a line's fate, once known: said (<see cref="BotSpeechAdmission.Queued"/>) or not, and why. Raised for a line refused
    /// by <see cref="TrySpeak"/> (before it returns) and for one said or stopped at delivery; the speech is the line as said (equal by value
    /// to the one handed in). The conversation director follows its lines with it.
    /// </summary>
    internal event Action<BotSpeech, BotSpeechAdmission>? SpeechSettled;

    private void Settle(BotSpeech speech, BotSpeechAdmission outcome)
    {
        if (SpeechSettled is not { } handlers) return;
        try
        {
            handlers(speech, outcome);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat", error);
            _logger.LogWarning(error, "Bot chat: a speech-settled handler failed");
        }
    }

    /// <summary>
    /// World thread: every chat line a bot's session received, for the lanes that react to what bots hear (another bot's line flagged
    /// <see cref="BotHeardLine.SenderIsBot"/>). Raised by <see cref="Hear"/>; a handler that throws is logged and recorded.
    /// </summary>
    internal event Action<BotHeardLine>? LineHeard;

    /// <summary>Whether anything listens to <see cref="LineHeard"/> (only while the social memory is enabled, live) or answers through <see cref="Responder"/> (the party AI skips building the line otherwise).</summary>
    internal bool HasHearers => (LineHeard is not null && Social is { Enabled: true }) || Responder is not null;

    /// <summary>
    /// World thread: the one feature that may answer a heard line itself (the bot voice, <see cref="PlayerbotNeedsVoice"/>: an "inv" to an
    /// LFG call, a price question to a WTS call). True means it answered: the party AI then gives the line no reply of its own.
    /// </summary>
    internal Func<BotHeardLine, bool>? Responder { get; set; }

    /// <summary>What bots have called out lately (their LFG, LFM, WTS and WTB calls) and the resurrections they owe thanks for (world thread).</summary>
    internal PlayerbotVoiceBoard Voice { get; } = new();

    /// <summary>
    /// World thread: hand a heard line to <see cref="LineHeard"/>, then to <see cref="Responder"/>; true when the responder answered it.
    /// <see cref="BotHeardLine.FirstHearing"/> is set here: true for the first bot reporting this sender's line on this channel within ten
    /// seconds (a channel line reaches every bot in the channel).
    /// </summary>
    internal bool Hear(BotHeardLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Action<BotHeardLine>? handlers = LineHeard;
        Func<BotHeardLine, bool>? responder = Responder;
        if (handlers is null && responder is null) return false;
        long now = NowMs;
        string key = string.Concat(((byte)line.Type).ToString(CultureInfo.InvariantCulture), "|", line.ChannelName, "|",
            line.SenderGuid.Value.ToString("X", CultureInfo.InvariantCulture), "|", line.Text);
        bool first;
        lock (_heard)
        {
            if (_heard.Count > 4096)
                foreach (KeyValuePair<string, long> stale in _heard.Where(pair => now - pair.Value > 10_000).ToArray()) _heard.Remove(stale.Key);
            if (_heard.Count > 4096) _heard.Clear();
            first = !_heard.TryGetValue(key, out long at) || now - at > 10_000;
            if (first) _heard[key] = now;
        }

        BotHeardLine heard = line with { FirstHearing = first };
        try
        {
            handlers?.Invoke(heard);
            return responder?.Invoke(heard) == true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat", error);
            _logger.LogWarning(error, "Bot chat: a heard-line handler failed");
            return false;
        }
    }

    /// <summary>
    /// World thread: take a line for a reply. Queued means a worker will answer it (or, when every provider fails, nothing will);
    /// anything else means it was not taken and the caller keeps its fixed behaviour.
    /// </summary>
    internal BotChatAdmission TryAsk(BotChatAsk ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (!IsActive) return BotChatAdmission.Disabled;
        if (!Answers(ask.Channel)) return BotChatAdmission.Channel;
        if (_safety.TryChoice(ask) is { } choice)
        {
            _replies.Enqueue(new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, choice, null, AcknowledgeCommand: false, "Safety",
                Speech: ask.Speech));
            return BotChatAdmission.Choice;
        }

        DateTimeOffset now = _time.GetUtcNow();
        PlayerbotChatOptions options = Options;
        lock (_gate)
        {
            if (_lastReplyTo.TryGetValue(ask.SenderGuid.Value, out DateTimeOffset last) && now - last < TimeSpan.FromSeconds(options.PerPlayerCooldownSeconds))
                return BotChatAdmission.Cooldown;
            if (!options.EffectiveProviders().Any(provider => CanAnswer(provider, now, out _))) return BotChatAdmission.Budget;
            Channel<BotChatAsk> queue = EnsureWorkers();
            if (Volatile.Read(ref _queued) >= options.MaxQueuedRequests || !queue.Writer.TryWrite(ask)) return BotChatAdmission.QueueFull;
            Interlocked.Increment(ref _queued);
            _lastReplyTo[ask.SenderGuid.Value] = now;
            if (_lastReplyTo.Count > 4096)
                foreach (ulong stale in _lastReplyTo.Where(pair => now - pair.Value > TimeSpan.FromHours(1)).Select(pair => pair.Key).ToArray())
                    _lastReplyTo.Remove(stale);
        }

        return BotChatAdmission.Queued;
    }

    /// <summary>
    /// World thread: take an announcement a bot makes on its own (<see cref="BotChatAsk.Topic"/> set): a guild line (docs/areas/playbots-social.md)
    /// or, with <see cref="BotChatAsk.Speech"/> set, a line on any channel the speech API knows (the topic's built-in template, or a model's
    /// line when one is configured; it is said with that envelope, through the same limits as <see cref="TrySpeak"/>).
    /// Nobody spoke, so there is no channel list, safety choice or per-player cooldown to apply: the line needs chat on, a provider
    /// with budget (the hourly caps, the spend cap) and room in the queue. Queued means a worker will write it (or, when every provider
    /// fails, nothing will); anything else means it was not taken.
    /// </summary>
    internal BotChatAdmission TryAnnounce(BotChatAsk ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (ask.Topic is null) throw new ArgumentException("an announcement needs a topic", nameof(ask));
        if (!IsActive) return BotChatAdmission.Disabled;
        DateTimeOffset now = _time.GetUtcNow();
        PlayerbotChatOptions options = Options;
        lock (_gate)
        {
            if (!options.EffectiveProviders().Any(provider => CanAnswer(provider, now, out _))) return BotChatAdmission.Budget;
            Channel<BotChatAsk> queue = EnsureWorkers();
            if (Volatile.Read(ref _queued) >= options.MaxQueuedRequests || !queue.Writer.TryWrite(ask)) return BotChatAdmission.QueueFull;
            Interlocked.Increment(ref _queued);
        }

        return BotChatAdmission.Queued;
    }

    /// <summary>
    /// World thread, after <see cref="TryAsk"/> took a line: the AI disclosure to show its speaker first (once per login,
    /// <paramref name="login"/> from <see cref="PlayerbotChatSafety.LoginOf"/>), or null.
    /// </summary>
    internal string? TakeDisclosure(BotChatAsk ask, long login) => IsActive ? _safety.TakeDisclosure(ask, login, Options.HasModelProvider) : null;

    /// <summary>World thread: the automatic mutes to apply (<see cref="PlayerbotChatSafetyOptions.AutoMute"/>).</summary>
    internal IReadOnlyList<BotChatMute> DrainMutes() => _safety.DrainMutes();

    /// <summary>
    /// Log the provider chain at start: each provider's kind and model, and whether its key variable is set (the name of the
    /// variable and yes/no; never the value).
    /// </summary>
    internal void LogConfiguration()
    {
        PlayerbotChatOptions options = Options;
        if (!options.Enabled)
        {
            _logger.LogInformation("Bot chat is off: bots keep their fixed replies");
            return;
        }

        int index = 0;
        foreach (PlayerbotChatProviderOptions provider in options.EffectiveProviders())
        {
            string? variable = provider.EffectiveKeyVariable;
            string key = variable is null ? "no key" : $"key {variable} {(string.IsNullOrEmpty(_environment.Get(variable)) ? "missing (skipped)" : "present")}";
            _logger.LogInformation("Bot chat provider #{Index}: {Provider}, {Key}, at most {PerHour} replies an hour", index++, provider.Label, key, provider.MaxRepliesPerHour);
        }

        PlayerbotChatSafetyOptions safety = options.Safety;
        (int terms, int patterns) = _safety.Filter().Size;
        _logger.LogInformation(
            "Bot chat safety: screening {Screening} ({Terms} terms, {Patterns} patterns; categories {Categories}), disclosure {Disclosure}, opt-out {OptOut}, require opt-in {OptIn}, strip personal data {Strip}",
            safety.Enabled ? "on" : "OFF", terms, patterns, safety.Categories, safety.Disclosure ? "on" : "off", safety.AllowOptOut ? "on" : "off",
            safety.RequireOptIn ? "on" : "off", safety.StripPersonalData ? "on" : "off");
        if (!safety.Enabled && options.HasModelProvider)
            _logger.LogWarning("Bot chat safety screening is off while a model provider is configured: players' lines reach the provider unscreened");
    }

    /// <summary>World thread: the replies ready to be said (at most <see cref="MaxRepliesPerDrain"/> per call).</summary>
    internal IReadOnlyList<BotChatReply> DrainReplies()
    {
        while (_lostSpeech.TryDequeue(out BotSpeech? lost)) Lost(lost); // the lines the workers gave up on, told on the world thread
        if (_replies.IsEmpty) return [];
        var ready = new List<BotChatReply>();
        while (ready.Count < MaxRepliesPerDrain && _replies.TryDequeue(out BotChatReply? reply))
        {
            if (ReferenceEquals(reply.Provider, SpeechProvider)) Interlocked.Decrement(ref _pendingSpeech);
            ready.Add(reply);
        }

        return ready;
    }

    /// <summary>The state for <c>.playerbot chat status</c> (any thread).</summary>
    internal BotChatStatus Status()
    {
        PlayerbotChatOptions options = Options;
        DateTimeOffset now = _time.GetUtcNow();
        var lines = new List<BotChatProviderStatus>();
        lock (_gate)
        {
            RollSpendDay(now);
            int index = 0;
            foreach (PlayerbotChatProviderOptions provider in options.EffectiveProviders())
            {
                ProviderState state = StateOf(provider);
                state.Prune(now);
                string? variable = provider.EffectiveKeyVariable;
                lines.Add(new BotChatProviderStatus(index++, provider.Kind, provider.Kind == PlayerbotChatProviderKind.Builtin ? "templates" : provider.EffectiveModel,
                    variable, variable is not null && !string.IsNullOrEmpty(_environment.Get(variable)), state.Hour.Count, provider.MaxRepliesPerHour,
                    state.Replies, state.Errors, state.LastError,
                    state.CooldownUntil > now ? (int)Math.Ceiling((state.CooldownUntil - now).TotalSeconds) : 0, provider.Price is not null));
            }

            return new BotChatStatus(options.Enabled, options.Channels, Volatile.Read(ref _queued), Interlocked.Read(ref _answered),
                Interlocked.Read(ref _dropped), _spentToday, options.MaxDailySpendUsd, lines, _safety.Status());
        }
    }

    // --- workers ----------------------------------------------------------------------------------------------------------

    private Channel<BotChatAsk> EnsureWorkers()
    {
        if (_queue is { } existing) return existing;
        var queue = Channel.CreateBounded<BotChatAsk>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });
        _queue = queue;
        _workers = [.. Enumerable.Range(0, WorkerCount(Options)).Select(_ => Task.Run(() => WorkAsync(queue.Reader)))];
        return queue;
    }

    /// <summary>
    /// <see cref="Workers"/>, plus, with an in-process model provider, enough to keep its slots and its queue fed: its scheduler can
    /// only put a player's line ahead of ambient chatter if both have reached it.
    /// </summary>
    internal static int WorkerCount(PlayerbotChatOptions options)
    {
        if (!(options.Providers ?? []).Any(p => p?.Kind == PlayerbotChatProviderKind.InProcess)) return Workers;
        PlayerbotChatInProcessOptions local = options.InProcess;
        return Math.Clamp(Workers + local.MaxConcurrent + local.MaxQueued, Workers, 16);
    }

    // --- in-flight requests (cancelled when the bot leaves or a newer line supersedes them) -----------------------------------

    /// <summary>One model request on its way: its deadline source, cancelled by <see cref="Abandon"/> or a superseding line.</summary>
    private sealed class InFlight(CancellationTokenSource cancellation, BotChatWorkload workload)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public BotChatWorkload Workload { get; } = workload;

        /// <summary>Nobody wants the answer any more: whatever comes back is not said and counts against no provider.</summary>
        public volatile bool Abandoned;
    }

    private InFlight Track(Guid botId, CancellationTokenSource cancellation, BotChatWorkload workload)
    {
        var flight = new InFlight(cancellation, workload);
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(botId, out List<InFlight>? list)) _inFlight[botId] = list = [];
            if (workload == BotChatWorkload.Interactive)
            {
                // A player now talks to this bot: chatter it was still composing is no longer wanted (and would only hold a slot).
                foreach (InFlight other in list)
                {
                    if (other.Workload != BotChatWorkload.Ambient || other.Abandoned) continue;
                    other.Abandoned = true;
                    other.Cancellation.Cancel();
                }
            }

            list.Add(flight);
        }

        return flight;
    }

    private void Untrack(Guid botId, InFlight flight)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(botId, out List<InFlight>? list)) return;
            list.Remove(flight);
            if (list.Count == 0) _inFlight.Remove(botId);
        }
    }

    /// <summary>
    /// The bot <paramref name="botId"/> stopped (logged out, removed): cancel every model request still being written for it (any
    /// thread). Its answers are not said and count against no provider. Returns how many were cancelled.
    /// </summary>
    internal int Abandon(Guid botId)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(botId, out List<InFlight>? list)) return 0;
            int cancelled = 0;
            foreach (InFlight flight in list)
            {
                if (flight.Abandoned) continue;
                flight.Abandoned = true;
                flight.Cancellation.Cancel();
                cancelled++;
            }

            return cancelled;
        }
    }

    /// <summary>
    /// One model request with the chat's deadline, tracked so <see cref="Abandon"/> can cancel it. Null when it was abandoned (the bot
    /// left, or a player's line superseded it): nothing is said and nothing is recorded against the provider.
    /// </summary>
    private async Task<BotChatResult?> RequestAsync(PlayerbotChatProviderOptions provider, string? key, BotChatPrompt prompt, Guid botId, PlayerbotChatOptions options)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60)));
        InFlight flight = Track(botId, deadline, prompt.Workload);
        BotChatResult result;
        try
        {
            result = await _client.CompleteAsync(provider, key, prompt, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!_stop.IsCancellationRequested)
        {
            FaultLedger.Shared.Record("playerbot.chat-delivery", error);
            result = BotChatResult.Fail(BotChatOutcome.Timeout);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            FaultLedger.Shared.Record("playerbot.chat-delivery", error);
            result = BotChatResult.Fail(BotChatOutcome.NetworkError);
        }
        finally
        {
            Untrack(botId, flight);
        }

        return flight.Abandoned ? null : result;
    }

    /// <summary>How urgent an answer to <paramref name="ask"/> is: a player who named the bot (or whispered) first, another bot next, the rest ambient.</summary>
    internal static BotChatWorkload WorkloadOf(BotChatAsk ask)
        => ask.Topic is not null || !ask.Addressed ? BotChatWorkload.Ambient
            : ask.SenderIsBot ? BotChatWorkload.BotToBot
            : BotChatWorkload.Interactive;

    private async Task WorkAsync(ChannelReader<BotChatAsk> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out BotChatAsk? ask))
                {
                    Interlocked.Decrement(ref _queued);
                    try
                    {
                        if (await AnswerAsync(ask).ConfigureAwait(false) is { } reply)
                        {
                            _replies.Enqueue(reply.Speech is null && ask.Speech is not null ? reply with { Speech = ask.Speech } : reply);
                            Interlocked.Increment(ref _answered);
                        }
                        else
                        {
                            Interlocked.Increment(ref _dropped);
                            if (ask.Speech is { } lost) _lostSpeech.Enqueue(lost);
                        }
                    }
                    catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
                    {
                        // Never the key: the request carries it, the exception text does not.
                        Interlocked.Increment(ref _dropped);
                        if (ask.Speech is { } lost) _lostSpeech.Enqueue(lost);
                        FaultLedger.Shared.Record("playerbot.chat", error);
                        _logger.LogWarning("Bot chat: a reply failed ({Error})", error.GetType().Name); // type only, by design: the exception text may carry the request URL or key
                    }
                }
            }
        }
        catch (OperationCanceledException ex) when (_stop.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Bot chat worker stopped on cancellation");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat", ex);
            _logger.LogWarning("Bot chat worker faulted ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Worker: the first provider that answers, in order; null when none does (or the built-in one chooses silence). The line is
    /// screened first: a flagged line is answered by <see cref="Flagged"/> and reaches no model provider.
    /// </summary>
    internal async Task<BotChatReply?> AnswerAsync(BotChatAsk ask)
    {
        PlayerbotChatOptions options = Options;
        if (!options.Enabled) return null;
        if (ask.Topic is { } topic) return await AnnounceAsync(ask, topic, options).ConfigureAwait(false);
        PlayerbotChatSafetyOptions safety = options.Safety;
        bool screening = safety.Enabled;
        bool modelAllowed = true;
        if (screening)
        {
            PlayerbotChatSafetyFilter filter = _safety.Filter();
            await filter.Warmed.ConfigureAwait(false); // a worker thread: never a cold first match on a player's line
            SafetyVerdict verdict = _safety.ScreenInput(ask.Text, filter);
            if (verdict.Category is { } category)
            {
                if (ask.SenderIsBot) return null; // another bot's line: no strike, no brush-off, no exchange
                _safety.Flag(ask, category.ToString(), BotChatFlagSource.Local, Strikes(category), ask.Text);
                return Flagged(ask, category, safety);
            }

            if (verdict.TimedOut) modelAllowed = false; // not judged in time: kept from the models, no strike
        }

        if (modelAllowed && options.HasModelProvider && !_safety.ModelAllowed(ask)) modelAllowed = false;
        int index = -1;
        foreach (PlayerbotChatProviderOptions provider in options.EffectiveProviders())
        {
            index++;
            if (provider.Kind != PlayerbotChatProviderKind.Builtin && !modelAllowed) continue;
            string? key;
            lock (_gate)
            {
                if (!CanAnswer(provider, _time.GetUtcNow(), out key)) continue;
            }

            if (provider.Kind == PlayerbotChatProviderKind.Builtin) return Builtin(ask, provider, options);
            if (screening && provider.Moderation != PlayerbotChatModerationKind.None)
            {
                BotChatModeration moderation = await ModerateAsync(provider, key, ask, options).ConfigureAwait(false);
                if (moderation.Verdict == BotChatModerationVerdict.Flagged)
                {
                    if (ask.SenderIsBot) return null;
                    _safety.Flag(ask, moderation.Category ?? "flagged", BotChatFlagSource.Moderation, strike: true, ask.Text);
                    return Flagged(ask, null, safety);
                }

                if (moderation.Verdict != BotChatModerationVerdict.Clean)
                {
                    FailModeration(provider, index, moderation); // never sent unchecked: the next provider answers
                    continue;
                }
            }

            BotChatPrompt prompt = PlayerbotChatPrompts.Build(Outgoing(ask, options), Outgoing(Memory(ask), options)) with { Workload = WorkloadOf(ask) };
            if (await RequestAsync(provider, key, prompt, ask.BotId, options).ConfigureAwait(false) is not { } result) return null; // abandoned: the bot left or a newer line took over
            Record(provider, index, result);
            if (result.Outcome != BotChatOutcome.Ok) continue;
            (string? text, Party.PlayerbotPartyCommand? command) = PlayerbotChatPrompts.ReadAnswer(result.Text);
            if (text is null)
            {
                Fail(provider, index, "empty-reply", null);
                continue;
            }

            if (screening && safety.ScreenOutput && _safety.ScreenOutput(text) is { } verdict && (verdict.Flagged || verdict.TimedOut))
            {
                // Flagged, or not judged in time (fails closed: an unjudged reply is never said, nothing is flagged): the blocked-reply path.
                // A personal-data category from a timeout is no finding: no flag record, no excerpt of the model's text.
                if (verdict.Category is { } said && !verdict.CategoryFromTimeout) _safety.Flag(ask, said.ToString(), BotChatFlagSource.Output, strike: false, text);
                if (safety.OnOutputFlagged == PlayerbotChatOutputAction.Drop) return null;
                modelAllowed = false; // the built-in provider answers the (clean) line instead
                continue;
            }

            if (!ask.FromMaster || !options.NaturalLanguageCommands) command = null;
            BotChatAction action = SocialAction(ask, PlayerbotChatPrompts.ReadAction(result.Text));
            Remember(ask, text);
            return new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, text, command, AcknowledgeCommand: false, provider.Label,
                Action: action);
        }

        return null;
    }

    /// <summary>
    /// Worker: the line for an announcement (<see cref="TryAnnounce"/>). The same chain as an answer, with these differences: the
    /// sentence is screened but a flag never strikes anyone (the sentence is the server's own, there is no player to cut off) and
    /// says nothing; a model provider is used only while <see cref="PlayerbotChatSafetyOptions.RequireOptIn"/> is off (no one has opted
    /// in to an announcement, so it takes built-in lines only); the model prompt is <see cref="PlayerbotChatPrompts.Announcement"/>; the
    /// built-in line is the topic's template; and nothing is remembered. A model's line is screened before it is said, and a flagged one
    /// is replaced by the built-in line (or dropped, <see cref="PlayerbotChatSafetyOptions.OnOutputFlagged"/>).
    /// </summary>
    private async Task<BotChatReply?> AnnounceAsync(BotChatAsk ask, BotChatTopic topic, PlayerbotChatOptions options)
    {
        PlayerbotChatSafetyOptions safety = options.Safety;
        bool screening = safety.Enabled;
        bool modelAllowed = options.HasModelProvider && !safety.RequireOptIn && !ask.BuiltinOnly;
        if (screening)
        {
            SafetyVerdict verdict = _safety.ScreenInput(ask.Text);
            if (verdict.Category is { } category)
            {
                _safety.Flag(ask, category.ToString(), BotChatFlagSource.Local, strike: false, ask.Text);
                return null;
            }

            if (verdict.TimedOut) modelAllowed = false;
        }

        int index = -1;
        foreach (PlayerbotChatProviderOptions provider in options.EffectiveProviders())
        {
            index++;
            if (provider.Kind != PlayerbotChatProviderKind.Builtin && !modelAllowed) continue;
            string? key;
            lock (_gate)
            {
                if (!CanAnswer(provider, _time.GetUtcNow(), out key)) continue;
            }

            if (provider.Kind == PlayerbotChatProviderKind.Builtin) return BuiltinTopic(ask, topic, provider);
            if (screening && provider.Moderation != PlayerbotChatModerationKind.None)
            {
                BotChatModeration moderation = await ModerateAsync(provider, key, ask, options).ConfigureAwait(false);
                if (moderation.Verdict == BotChatModerationVerdict.Flagged)
                {
                    _safety.Flag(ask, moderation.Category ?? "flagged", BotChatFlagSource.Moderation, strike: false, ask.Text);
                    return null;
                }

                if (moderation.Verdict != BotChatModerationVerdict.Clean)
                {
                    FailModeration(provider, index, moderation);
                    continue;
                }
            }

            BotChatPrompt prompt = PlayerbotChatPrompts.Announcement(Outgoing(ask, options), topic) with { Workload = BotChatWorkload.Ambient };
            if (await RequestAsync(provider, key, prompt, ask.BotId, options).ConfigureAwait(false) is not { } result) return null; // abandoned: the bot left or a player's line took over
            Record(provider, index, result);
            if (result.Outcome != BotChatOutcome.Ok) continue;
            string? text = PlayerbotChatPrompts.ReadAnswer(result.Text).Reply;
            if (text is null)
            {
                Fail(provider, index, "empty-reply", null);
                continue;
            }

            if (screening && safety.ScreenOutput && _safety.ScreenOutput(text).Category is { } said)
            {
                _safety.Flag(ask, said.ToString(), BotChatFlagSource.Output, strike: false, text);
                if (safety.OnOutputFlagged == PlayerbotChatOutputAction.Drop) return null;
                modelAllowed = false; // the built-in provider writes the (clean) line instead
                continue;
            }

            return new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, text, null, AcknowledgeCommand: false, provider.Label, topic.GuildName);
        }

        return null;
    }

    /// <summary>The built-in line of an announcement: one phrasing of the topic's template with its placeholders filled; nothing remembered.</summary>
    private BotChatReply? BuiltinTopic(BotChatAsk ask, BotChatTopic topic, PlayerbotChatProviderOptions provider)
    {
        string? text;
        lock (_gate)
        {
            text = PlayerbotBuiltinChat.Pick(topic.TemplateKey, ask, null, _random);
            if (text is null) return null;
            StateOf(provider).Succeed(_time.GetUtcNow());
        }

        // The template is ours, but its values are names (a member's, a boss's): the line is screened like a model's before it is said.
        PlayerbotChatSafetyOptions safety = Options.Safety;
        if (safety.Enabled && safety.ScreenOutput && _safety.ScreenOutput(text).Category is { } said)
        {
            _safety.Flag(ask, said.ToString(), BotChatFlagSource.Output, strike: false, text);
            return null;
        }

        return new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, text, null, AcknowledgeCommand: false, provider.Label, topic.GuildName);
    }

    private BotChatReply? Builtin(BotChatAsk ask, PlayerbotChatProviderOptions provider, PlayerbotChatOptions options)
    {
        BuiltinChatAnswer answer;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            string? last = null;
            BuiltinChatIntent? lastIntent = null;
            if (_conversations.TryGetValue((ask.BotId, ask.SenderGuid.Value), out Conversation? conversation))
            {
                last = conversation.LastReply;
                if (now - conversation.Touched <= TimeSpan.FromMilliseconds(PlayerbotBuiltinChat.FollowUpMs)) lastIntent = conversation.LastIntent;
            }

            answer = PlayerbotBuiltinChat.Answer(ask, last, lastIntent, options.NaturalLanguageCommands, _random);
            if (answer.Text is null && answer.Command is null) return null;
            StateOf(provider).Succeed(now);
        }

        if (answer.Text is { } text) Remember(ask, text, answer.Intent, fromTemplate: true);
        return new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, answer.Text, answer.Command, AcknowledgeCommand: true, provider.Label,
            Action: SocialAction(ask, answer.Action));
    }

    /// <summary>
    /// The social act a reply may carry: never for another bot's line; a quest share only for the party master; an order word is the
    /// party command's business (<see cref="BotChatReply.Command"/>), not an action.
    /// </summary>
    internal static BotChatAction SocialAction(BotChatAsk ask, BotChatAction action)
    {
        if (ask.SenderIsBot || ask.Topic is not null) return BotChatAction.None;
        return action switch
        {
            BotChatAction.Invite or BotChatAction.OpenTrade or BotChatAction.JoinLfg => action,
            BotChatAction.ShareQuest when ask.FromMaster => action,
            _ => BotChatAction.None,
        };
    }

    // --- directed conversations -----------------------------------------------------------------------------------------

    private readonly Dictionary<(ulong A, ulong B), long> _directed = [];

    /// <summary>
    /// World thread: the conversation director runs an exchange between <paramref name="a"/> and <paramref name="b"/> until
    /// <paramref name="untilMs"/> (chat clock): while it does, neither bot answers the other's lines on its own (the director writes both
    /// sides), so an exchange never doubles. <see cref="ReleaseExchange"/> ends it early.
    /// </summary>
    internal void ClaimExchange(ObjectGuid a, ObjectGuid b, long untilMs)
    {
        lock (_directed)
        {
            if (_directed.Count > 256)
                foreach (KeyValuePair<(ulong, ulong), long> stale in _directed.Where(pair => pair.Value < NowMs).ToArray()) _directed.Remove(stale.Key);
            _directed[PairOf(a, b)] = untilMs;
        }
    }

    /// <summary>End a directed exchange (the director finished or dropped it).</summary>
    internal void ReleaseExchange(ObjectGuid a, ObjectGuid b)
    {
        lock (_directed) _directed.Remove(PairOf(a, b));
    }

    /// <summary>Whether the director runs an exchange between these two bots now (their own answers to each other are held).</summary>
    internal bool IsDirected(ObjectGuid speaker, ObjectGuid listener)
    {
        lock (_directed) return _directed.TryGetValue(PairOf(speaker, listener), out long until) && until >= NowMs;
    }

    private static (ulong, ulong) PairOf(ObjectGuid a, ObjectGuid b) => a.Value <= b.Value ? (a.Value, b.Value) : (b.Value, a.Value);

    // --- safety -----------------------------------------------------------------------------------------------------------

    /// <summary>Whether a local flag of <paramref name="category"/> is a strike (self-harm and personal data are not: no one is punished for them).</summary>
    internal static bool Strikes(PlayerbotChatSafetyCategories category)
        => category is not (PlayerbotChatSafetyCategories.SelfHarm or PlayerbotChatSafetyCategories.PersonalData);

    /// <summary>
    /// The answer to a flagged line: nothing (<see cref="PlayerbotChatFlaggedAction.Ignore"/>) or the built-in brush-off (a supportive
    /// line for self-harm, a caution for personal data). The line is not remembered, so it is never sent later as memory either.
    /// </summary>
    private BotChatReply? Flagged(BotChatAsk ask, PlayerbotChatSafetyCategories? category, PlayerbotChatSafetyOptions safety)
    {
        if (safety.OnFlagged == PlayerbotChatFlaggedAction.Ignore) return null;
        string key = category switch
        {
            PlayerbotChatSafetyCategories.SelfHarm => "safety.selfharm",
            PlayerbotChatSafetyCategories.PersonalData => "safety.personal",
            _ => "abuse",
        };
        string? text;
        lock (_gate)
        {
            string? last = _conversations.TryGetValue((ask.BotId, ask.SenderGuid.Value), out Conversation? conversation) ? conversation.LastReply : null;
            text = PlayerbotBuiltinChat.Pick(key, ask, last, _random);
        }

        return text is null ? null : new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, text, null, AcknowledgeCommand: true, "Builtin");
    }

    /// <summary>The line as sent to a model provider: emails and phone numbers replaced when <see cref="PlayerbotChatSafetyOptions.StripPersonalData"/>.</summary>
    private BotChatAsk Outgoing(BotChatAsk ask, PlayerbotChatOptions options)
        => options.Safety.StripPersonalData ? ask with { Text = _safety.StripPersonalData(ask.Text) } : ask;

    private List<BotChatTurn> Outgoing(List<BotChatTurn> memory, PlayerbotChatOptions options)
    {
        if (!options.Safety.StripPersonalData) return memory;
        return [.. memory.Select(turn => turn with { Text = _safety.StripPersonalData(turn.Text) })];
    }

    /// <summary>A provider's moderation step for <paramref name="ask"/>'s line (as it would be sent); any error fails the step.</summary>
    private async Task<BotChatModeration> ModerateAsync(PlayerbotChatProviderOptions provider, string? key, BotChatAsk ask, PlayerbotChatOptions options)
    {
        string text = PlayerbotChatPrompts.Clean(Outgoing(ask, options).Text, PlayerbotChatPrompts.MaxLineLength);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60)));
        try
        {
            if (provider.Moderation == PlayerbotChatModerationKind.Endpoint)
            {
                string? moderationKey = key;
                if (provider.ModerationApiKeyEnvironmentVariable is not null)
                {
                    string? variable = provider.EffectiveModerationKeyVariable;
                    moderationKey = variable is null ? null : _environment.Get(variable);
                    if (variable is not null && string.IsNullOrEmpty(moderationKey)) return new BotChatModeration(BotChatModerationVerdict.Failed);
                }

                return await _client.ModerateAsync(provider, moderationKey, text, deadline.Token).ConfigureAwait(false);
            }

            PlayerbotChatProviderOptions classifier = provider.Clone();
            classifier.MaxTokens = 16;
            classifier.ReasoningEffort = provider.ClassifierReasoningEffort;
            BotChatResult result = await _client.CompleteAsync(classifier, key, PlayerbotChatPrompts.Classifier(text), deadline.Token).ConfigureAwait(false);
            lock (_gate)
            {
                AddSpend(provider, result, _time.GetUtcNow());
            }

            if (result.Outcome == BotChatOutcome.Ok) return PlayerbotChatPrompts.ReadClassification(result.Text);
            return new BotChatModeration(BotChatModerationVerdict.Failed, result.Outcome == BotChatOutcome.ReasoningOnly ? ClassifierReasoningOnly : null);
        }
        catch (OperationCanceledException error) when (!_stop.IsCancellationRequested)
        {
            FaultLedger.Shared.Record("playerbot.chat-safety", error);
            return new BotChatModeration(BotChatModerationVerdict.Failed);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            FaultLedger.Shared.Record("playerbot.chat-safety", error);
            AnomalyLedger.Shared.Report("bot-chat", "safety-filter-fault", "chat-moderation", $"{error.GetType().Name}: {error.Message}");
            return new BotChatModeration(BotChatModerationVerdict.Failed);
        }
    }

    // --- budgets and provider state ---------------------------------------------------------------------------------------

    /// <summary>Under <see cref="_gate"/>: whether <paramref name="provider"/> may answer now, and its key (null for keyless).</summary>
    private bool CanAnswer(PlayerbotChatProviderOptions provider, DateTimeOffset now, out string? key)
    {
        key = null;
        ProviderState state = StateOf(provider);
        state.Prune(now);
        if (state.Hour.Count >= provider.MaxRepliesPerHour) return false;
        if (provider.Kind == PlayerbotChatProviderKind.Builtin) return true;
        if (state.CooldownUntil > now) return false;
        if (provider.EffectiveKeyVariable is { } variable)
        {
            key = _environment.Get(variable);
            if (string.IsNullOrEmpty(key)) { key = null; return false; }
        }

        RollSpendDay(now);
        double cap = Options.MaxDailySpendUsd;
        return !(cap > 0 && provider.Price is { } price && (price.Input > 0 || price.Output > 0) && _spentToday >= cap);
    }

    private void Record(PlayerbotChatProviderOptions provider, int index, BotChatResult result)
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool badRepeat;
        lock (_gate)
        {
            AddSpend(provider, result, now);
            ProviderState state = StateOf(provider);
            if (result.Outcome == BotChatOutcome.Ok)
            {
                state.Succeed(now);
                return;
            }

            badRepeat = CountBad(provider, state, result.Outcome);
        }

        TimeSpan? cooldown = result.Outcome switch
        {
            BotChatOutcome.RateLimited => result.RetryAfter is { } after && after > TimeSpan.Zero ? Min(after, TimeSpan.FromMinutes(10)) : null,
            BotChatOutcome.Unauthorized => TimeSpan.FromMinutes(10),
            BotChatOutcome.Rejected => TimeSpan.FromMinutes(5),
            _ => null,
        };
        // A single unreadable or empty answer costs nothing, but the second in a row backs off like any other provider failure.
        bool backOff = badRepeat || result.Outcome is BotChatOutcome.RateLimited or BotChatOutcome.Unavailable or BotChatOutcome.Timeout or BotChatOutcome.NetworkError;
        Fail(provider, index, ErrorClass(result.Outcome), cooldown ?? (backOff ? null : TimeSpan.Zero));
    }

    /// <summary>
    /// Under <see cref="_gate"/>: count <paramref name="outcome"/> in the provider's run of unreadable answers and say whether it is the second
    /// in a row. Only an HTTP provider's answer counts: an in-process model's bad answer is about the content (context, error, filtered), not
    /// the health of a provider, so it never backs anything off. Any other outcome ends the run.
    /// </summary>
    private static bool CountBad(PlayerbotChatProviderOptions provider, ProviderState state, BotChatOutcome outcome)
    {
        bool http = provider.Kind is PlayerbotChatProviderKind.OpenAICompatible or PlayerbotChatProviderKind.Anthropic;
        state.BadStreak = http && (outcome is BotChatOutcome.BadResponse or BotChatOutcome.ReasoningOnly) ? state.BadStreak + 1 : 0;
        return state.BadStreak >= 2;
    }

    /// <summary>The <see cref="BotChatModeration.Category"/> of a Failed verdict whose classifier call only thought (never a flag category).</summary>
    private const string ClassifierReasoningOnly = "reasoning-only";

    /// <summary>
    /// A moderation step did not return a verdict. A classifier that only thought is counted like a reasoning-only reply (<see cref="Record"/>:
    /// the second in a row backs the provider off, and the first logs the hint); any other failure is "moderation-failed" with no cooldown.
    /// </summary>
    private void FailModeration(PlayerbotChatProviderOptions provider, int index, BotChatModeration moderation)
    {
        if (moderation.Category != ClassifierReasoningOnly)
        {
            Fail(provider, index, "moderation-failed", TimeSpan.Zero);
            return;
        }

        bool badRepeat;
        lock (_gate)
        {
            badRepeat = CountBad(provider, StateOf(provider), BotChatOutcome.ReasoningOnly);
        }

        Fail(provider, index, ErrorClass(BotChatOutcome.ReasoningOnly), badRepeat ? null : TimeSpan.Zero);
    }

    /// <summary>Under <see cref="_gate"/>: add a request's estimated cost to the day's spend.</summary>
    private void AddSpend(PlayerbotChatProviderOptions provider, BotChatResult result, DateTimeOffset now)
    {
        RollSpendDay(now);
        if (provider.Price is { } price)
            _spentToday += ((result.InputTokens + (1.25 * result.CacheWriteTokens) + (0.1 * result.CacheReadTokens)) * price.Input
                + (result.OutputTokens * price.Output)) / 1_000_000d;
    }

    /// <summary>Count a failure; <paramref name="cooldown"/> null = the doubling back-off (15 s up to 5 min), zero = none.</summary>
    private void Fail(PlayerbotChatProviderOptions provider, int index, string error, TimeSpan? cooldown)
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool changed;
        lock (_gate)
        {
            ProviderState state = StateOf(provider);
            changed = state.LastError != error;
            state.Errors++;
            state.LastError = error;
            TimeSpan wait = cooldown ?? Min(TimeSpan.FromSeconds(15 << Math.Min(state.Strikes, 5)), TimeSpan.FromMinutes(5));
            if (cooldown is null) state.Strikes++;
            if (wait > TimeSpan.Zero) state.CooldownUntil = now + wait;
        }

        AnomalyLedger.Shared.Report(
            PlayerbotAnomalies.Subsystem("chat"),
            error == "timeout" ? "client-timeout" : "client-error",
            $"provider {provider.Label}",
            $"provider #{index} ({provider.Label}) failed: {error}",
            AnomalySeverity.Warning);

        if (changed)
            _logger.LogWarning("Bot chat provider #{Index} ({Provider}) failed: {Error}; the next provider answers{Hint}", index, provider.Label, error,
                error == "reasoning-only" ? " (the model spent its tokens thinking; set ReasoningEffort=none for this provider)"
                : error == "rejected" && provider is { Kind: PlayerbotChatProviderKind.OpenAICompatible, JsonResponseFormat: true }
                    ? " (the server may reject response_format json_object, e.g. LM Studio; set JsonResponseFormat=false for this provider)"
                    : string.Empty);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    internal static string ErrorClass(BotChatOutcome outcome) => outcome switch
    {
        BotChatOutcome.RateLimited => "rate-limited",
        BotChatOutcome.Unavailable => "unavailable",
        BotChatOutcome.Unauthorized => "unauthorized",
        BotChatOutcome.Rejected => "rejected",
        BotChatOutcome.Refused => "refused",
        BotChatOutcome.Timeout => "timeout",
        BotChatOutcome.NetworkError => "network",
        BotChatOutcome.ReasoningOnly => "reasoning-only",
        _ => "bad-response",
    };

    private ProviderState StateOf(PlayerbotChatProviderOptions provider)
    {
        string signature = provider.Signature;
        if (!_providers.TryGetValue(signature, out ProviderState? state)) _providers[signature] = state = new ProviderState();
        return state;
    }

    private void RollSpendDay(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (today == _spendDay) return;
        _spendDay = today;
        _spentToday = 0;
    }

    // --- memory -----------------------------------------------------------------------------------------------------------

    private List<BotChatTurn> Memory(BotChatAsk ask)
    {
        lock (_gate)
        {
            return _conversations.TryGetValue((ask.BotId, ask.SenderGuid.Value), out Conversation? conversation)
                ? [.. conversation.Turns.TakeLast(Math.Max(0, Options.MemoryExchanges) * 2)]
                : [];
        }
    }

    /// <summary>The bots' social memory seam (set by the feature; null in a host or a test without it).</summary>
    internal BotSocial.PlayerbotSocialRecorder? Social { get; set; }

    // <paramref name="fromTemplate"/>: the reply is one of the built-in lines. Only that text is kept in the social memory (as the bot's own
    // summary of the talk); a model's reply and the player's line never are.
    private void Remember(BotChatAsk ask, string reply, BuiltinChatIntent? intent = null, bool fromTemplate = false)
    {
        Social?.SetTopic(ask.BotId, ask.SenderGuid, intent?.ToString() ?? "chat", fromTemplate ? reply : string.Empty);
        lock (_gate)
        {
            var key = (ask.BotId, ask.SenderGuid.Value);
            if (!_conversations.TryGetValue(key, out Conversation? conversation))
            {
                if (_conversations.Count >= MaxConversations)
                    _conversations.Remove(_conversations.MinBy(pair => pair.Value.Touched).Key);
                _conversations[key] = conversation = new Conversation();
            }

            conversation.Touched = _time.GetUtcNow();
            conversation.LastReply = reply;
            conversation.LastIntent = intent;
            conversation.Turns.Add(new BotChatTurn(true, PlayerbotChatPrompts.Clean(ask.Text, PlayerbotChatPrompts.MaxLineLength)));
            conversation.Turns.Add(new BotChatTurn(false, reply));
            int keep = 2 * 16;
            if (conversation.Turns.Count > keep) conversation.Turns.RemoveRange(0, conversation.Turns.Count - keep);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _queue?.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug(error, "Bot chat workers did not complete within the dispose timeout");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat", ex);
            _logger.LogWarning("Bot chat worker faulted during dispose ({Error})", ex.GetType().Name);
        }
    }

    private sealed class ProviderState
    {
        public Queue<DateTimeOffset> Hour { get; } = new();
        public long Replies;
        public long Errors;
        public int Strikes;
        public int BadStreak;
        public string? LastError;
        public DateTimeOffset CooldownUntil;

        public void Prune(DateTimeOffset now)
        {
            while (Hour.Count > 0 && now - Hour.Peek() >= PlayerbotChat.Hour) Hour.Dequeue();
        }

        public void Succeed(DateTimeOffset now)
        {
            Hour.Enqueue(now);
            Replies++;
            Strikes = 0;
            BadStreak = 0;
            CooldownUntil = default;
        }
    }

    private sealed class Conversation
    {
        public List<BotChatTurn> Turns { get; } = [];
        public string? LastReply;
        public BuiltinChatIntent? LastIntent;
        public DateTimeOffset Touched;
    }

    /// <summary>The spend estimate as shown ("$0.0123").</summary>
    internal static string Usd(double value) => "$" + value.ToString("0.0000", CultureInfo.InvariantCulture);
}
