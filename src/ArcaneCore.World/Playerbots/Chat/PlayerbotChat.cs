using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
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
internal sealed class PlayerbotChat : IAsyncDisposable
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
    private readonly Dictionary<(Guid Bot, ulong Player), Conversation> _conversations = [];
    private readonly ConcurrentQueue<BotChatReply> _replies = new();
    private readonly Random _random = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly PlayerbotChatSafety _safety;
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
        _safety = new PlayerbotChatSafety(() => _options.Chat, time, logger);
    }

    private PlayerbotChatOptions Options => _options.Chat;

    /// <summary>The safety state: flags, strikes, cut-offs, choices (<c>.playerbot chat flags</c> and <c>pardon</c>).</summary>
    internal PlayerbotChatSafety Safety => _safety;

    /// <summary>Whether bots answer chat now (live: <c>.reload config</c>). Off: every line takes the fixed path of before.</summary>
    internal bool IsActive => Volatile.Read(ref _disposed) == 0 && Options.Enabled;

    /// <summary>Whether <paramref name="channel"/> is one the bots answer.</summary>
    internal bool Answers(ChatType channel)
    {
        PlayerbotChatChannels wanted = channel switch
        {
            ChatType.Whisper => PlayerbotChatChannels.Whisper,
            ChatType.Party or ChatType.Raid or ChatType.RaidLeader => PlayerbotChatChannels.Party,
            ChatType.Say => PlayerbotChatChannels.Say,
            _ => PlayerbotChatChannels.None,
        };
        return wanted != PlayerbotChatChannels.None && (Options.Channels & wanted) != 0;
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
            _replies.Enqueue(new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, choice, null, AcknowledgeCommand: false, "Safety"));
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
        if (_replies.IsEmpty) return [];
        var ready = new List<BotChatReply>();
        while (ready.Count < MaxRepliesPerDrain && _replies.TryDequeue(out BotChatReply? reply)) ready.Add(reply);
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
        _workers = [.. Enumerable.Range(0, Workers).Select(_ => Task.Run(() => WorkAsync(queue.Reader)))];
        return queue;
    }

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
                            _replies.Enqueue(reply);
                            Interlocked.Increment(ref _answered);
                        }
                        else
                        {
                            Interlocked.Increment(ref _dropped);
                        }
                    }
                    catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
                    {
                        // Never the key: the request carries it, the exception text does not.
                        Interlocked.Increment(ref _dropped);
                        _logger.LogWarning("Bot chat: a reply failed ({Error})", error.GetType().Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
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
        PlayerbotChatSafetyOptions safety = options.Safety;
        bool screening = safety.Enabled;
        bool modelAllowed = true;
        if (screening)
        {
            SafetyVerdict verdict = _safety.ScreenInput(ask.Text);
            if (verdict.Category is { } category)
            {
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
                    _safety.Flag(ask, moderation.Category ?? "flagged", BotChatFlagSource.Moderation, strike: true, ask.Text);
                    return Flagged(ask, null, safety);
                }

                if (moderation.Verdict != BotChatModerationVerdict.Clean)
                {
                    Fail(provider, index, "moderation-failed", TimeSpan.Zero); // never sent unchecked: the next provider answers
                    continue;
                }
            }

            BotChatResult result;
            BotChatPrompt prompt = PlayerbotChatPrompts.Build(Outgoing(ask, options), Outgoing(Memory(ask), options));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 60)));
                try
                {
                    result = await _client.CompleteAsync(provider, key, prompt, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    result = BotChatResult.Fail(BotChatOutcome.Timeout);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
                {
                    result = BotChatResult.Fail(BotChatOutcome.NetworkError);
                }
            }

            Record(provider, index, result);
            if (result.Outcome != BotChatOutcome.Ok) continue;
            (string? text, Party.PlayerbotPartyCommand? command) = PlayerbotChatPrompts.ReadAnswer(result.Text);
            if (text is null)
            {
                Fail(provider, index, "empty-reply", null);
                continue;
            }

            if (screening && safety.ScreenOutput && _safety.ScreenOutput(text).Category is { } said)
            {
                _safety.Flag(ask, said.ToString(), BotChatFlagSource.Output, strike: false, text);
                if (safety.OnOutputFlagged == PlayerbotChatOutputAction.Drop) return null;
                modelAllowed = false; // the built-in provider answers the (clean) line instead
                continue;
            }

            if (!ask.FromMaster || !options.NaturalLanguageCommands) command = null;
            Remember(ask, text);
            return new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, text, command, AcknowledgeCommand: false, provider.Label);
        }

        return null;
    }

    private BotChatReply? Builtin(BotChatAsk ask, PlayerbotChatProviderOptions provider, PlayerbotChatOptions options)
    {
        BuiltinChatAnswer answer;
        lock (_gate)
        {
            string? last = _conversations.TryGetValue((ask.BotId, ask.SenderGuid.Value), out Conversation? conversation) ? conversation.LastReply : null;
            answer = PlayerbotBuiltinChat.Answer(ask, last, options.NaturalLanguageCommands, _random);
            if (answer.Text is null && answer.Command is null) return null;
            StateOf(provider).Succeed(_time.GetUtcNow());
        }

        if (answer.Text is { } text) Remember(ask, text);
        return new BotChatReply(ask.BotId, ask.SenderGuid, ask.SenderName, ask.Channel, answer.Text, answer.Command, AcknowledgeCommand: true, provider.Label);
    }

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
    private static BotChatAsk Outgoing(BotChatAsk ask, PlayerbotChatOptions options)
        => options.Safety.StripPersonalData ? ask with { Text = PlayerbotChatSafetyFilter.StripPersonalData(ask.Text) } : ask;

    private static List<BotChatTurn> Outgoing(List<BotChatTurn> memory, PlayerbotChatOptions options)
        => options.Safety.StripPersonalData ? [.. memory.Select(turn => turn with { Text = PlayerbotChatSafetyFilter.StripPersonalData(turn.Text) })] : memory;

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
            BotChatResult result = await _client.CompleteAsync(classifier, key, PlayerbotChatPrompts.Classifier(text), deadline.Token).ConfigureAwait(false);
            lock (_gate)
            {
                AddSpend(provider, result, _time.GetUtcNow());
            }

            return result.Outcome == BotChatOutcome.Ok ? PlayerbotChatPrompts.ReadClassification(result.Text) : new BotChatModeration(BotChatModerationVerdict.Failed);
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            return new BotChatModeration(BotChatModerationVerdict.Failed);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
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
        lock (_gate)
        {
            AddSpend(provider, result, now);
            if (result.Outcome == BotChatOutcome.Ok)
            {
                StateOf(provider).Succeed(now);
                return;
            }
        }

        TimeSpan? cooldown = result.Outcome switch
        {
            BotChatOutcome.RateLimited => result.RetryAfter is { } after && after > TimeSpan.Zero ? Min(after, TimeSpan.FromMinutes(10)) : null,
            BotChatOutcome.Unauthorized => TimeSpan.FromMinutes(10),
            BotChatOutcome.Rejected => TimeSpan.FromMinutes(5),
            _ => null,
        };
        bool backOff = result.Outcome is BotChatOutcome.RateLimited or BotChatOutcome.Unavailable or BotChatOutcome.Timeout or BotChatOutcome.NetworkError;
        Fail(provider, index, ErrorClass(result.Outcome), cooldown ?? (backOff ? null : TimeSpan.Zero));
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

        if (changed)
            _logger.LogWarning("Bot chat provider #{Index} ({Provider}) failed: {Error}; the next provider answers", index, provider.Label, error);
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

    private void Remember(BotChatAsk ask, string reply)
    {
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
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
    }

    private sealed class ProviderState
    {
        public Queue<DateTimeOffset> Hour { get; } = new();
        public long Replies;
        public long Errors;
        public int Strikes;
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
            CooldownUntil = default;
        }
    }

    private sealed class Conversation
    {
        public List<BotChatTurn> Turns { get; } = [];
        public string? LastReply;
        public DateTimeOffset Touched;
    }

    /// <summary>The spend estimate as shown ("$0.0123").</summary>
    internal static string Usd(double value) => "$" + value.ToString("0.0000", CultureInfo.InvariantCulture);
}
