using System.Diagnostics.Metrics;
using System.Globalization;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using ArcaneCore.World.Playerbots.Chat.Inference.Serving;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>
/// The provider transport with the in-process model in front of it: a request for a <see cref="PlayerbotChatProviderKind.InProcess"/>
/// provider is answered by <see cref="InProcessChatEngine"/> on this server's CPU, anything else goes to the wrapped transport
/// (<see cref="HttpBotChatClient"/>) unchanged. <see cref="PlayerbotChat"/> treats it as any provider: a failure here moves on to the
/// next provider, so the built-in templates answer whenever the model cannot (docs/areas/playbots.md, In-process model).
/// <para>
/// Never throws for a bad or missing model: the load failure is reported once (anomaly <c>bot-chat/model-load-failed</c>) and logged,
/// and every request then fails as <see cref="BotChatOutcome.Rejected"/>. A reply is bounded by
/// <see cref="PlayerbotChatInProcessOptions.BudgetMs"/> (over it: <see cref="BotChatOutcome.Timeout"/>, and
/// <see cref="PlayerbotChatInProcessOptions.CircuitBreakerOverruns"/> in a row rest the model for
/// <see cref="PlayerbotChatInProcessOptions.CircuitBreakerSeconds"/>), at most <see cref="PlayerbotChatInProcessOptions.MaxConcurrent"/>
/// run and <see cref="PlayerbotChatInProcessOptions.MaxQueued"/> wait (past that: busy), and each reply passes
/// <see cref="InProcessChatPrompt.Screen"/>. It costs nothing: its price is zero, so it never counts toward the daily spend cap.
/// </para>
/// </summary>
internal sealed class InProcessBotChatClient : IBotChatClient, IDisposable
{
    private readonly IBotChatClient _inner;
    private readonly PlayerbotOptions _options;
    private readonly string _contentRoot;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Func<string, PlayerbotChatInProcessOptions, InProcessChatEngine>? _loader;
    private Task<InProcessChatEngine?>? _load;
    private string? _loadError;
    private int _waiting;
    private int _consecutiveOverruns;
    private DateTimeOffset _circuitOpenUntil;
    private long _replies, _tokens, _promptTokens, _reusedTokens, _overruns, _circuitOpens, _busy, _filtered, _unloaded, _cancelled, _errors, _dropped;
    private double _decodeMs, _prefillMs;
    private int _disposed;

    /// <param name="inner">The transport of every other provider kind.</param>
    /// <param name="options">The live playerbot options (<c>Chat</c> and <c>Chat:InProcess</c> are read from it).</param>
    /// <param name="contentRoot">What a relative <see cref="PlayerbotChatInProcessOptions.ModelPath"/> is relative to.</param>
    /// <param name="loader">Tests: builds the engine instead of loading the file.</param>
    internal InProcessBotChatClient(IBotChatClient inner, PlayerbotOptions options, string? contentRoot, ILogger? logger,
        TimeProvider? time = null, Func<string, PlayerbotChatInProcessOptions, InProcessChatEngine>? loader = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _contentRoot = contentRoot ?? AppContext.BaseDirectory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _loader = loader;
        if (_options.Enabled && _options.Chat.Enabled && Configured) _ = EnsureLoaded(); // start mapping the model now: the first reply should not wait for it
    }

    /// <summary>Whether a provider of kind InProcess is configured now.</summary>
    internal bool Configured => (_options.Chat.Providers ?? []).Any(p => p?.Kind == PlayerbotChatProviderKind.InProcess);

    /// <summary>Why the model did not load (null while loading, when loaded, or when never asked for).</summary>
    internal string? LoadError => Volatile.Read(ref _loadError);

    /// <summary>The loaded engine, or null (not configured, still loading, or failed).</summary>
    internal InProcessChatEngine? Engine => _load is { IsCompletedSuccessfully: true } load ? load.Result : null;

    public Task<BotChatResult> CompleteAsync(PlayerbotChatProviderOptions provider, string? apiKey, BotChatPrompt prompt, CancellationToken cancellationToken)
        => provider.Kind == PlayerbotChatProviderKind.InProcess
            ? CompleteLocalAsync(prompt, cancellationToken)
            : _inner.CompleteAsync(provider, apiKey, prompt, cancellationToken);

    public Task<BotChatModeration> ModerateAsync(PlayerbotChatProviderOptions provider, string? apiKey, string text, CancellationToken cancellationToken)
        => provider.Kind == PlayerbotChatProviderKind.InProcess
            ? Task.FromResult(new BotChatModeration(BotChatModerationVerdict.Failed))
            : _inner.ModerateAsync(provider, apiKey, text, cancellationToken);

    private async Task<BotChatResult> CompleteLocalAsync(BotChatPrompt prompt, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return Fallback("disposed", BotChatResult.Fail(BotChatOutcome.Unavailable));
        InProcessChatEngine? engine = await EnsureLoaded().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (engine is null || Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Increment(ref _unloaded);
            return Fallback("unloaded", BotChatResult.Fail(BotChatOutcome.Rejected));
        }

        PlayerbotChatInProcessOptions options = _options.Chat.InProcess;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_circuitOpenUntil > now)
                return Fallback("circuit", BotChatResult.Fail(BotChatOutcome.RateLimited, _circuitOpenUntil - now));
        }

        if (Interlocked.Increment(ref _waiting) > engine.Slots + options.MaxQueued)
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Increment(ref _busy);
            return Fallback("busy", BotChatResult.Fail(BotChatOutcome.RateLimited, TimeSpan.FromSeconds(1)));
        }

        try
        {
            ChatGenerationSettings settings = Settings(options);
            List<int>? tokens = InProcessChatPrompt.Build(prompt, engine.Model.Tokenizer, engine.ContextTokens - settings.MaxNewTokens);
            if (tokens is null) { Interlocked.Increment(ref _filtered); return Fallback("context", BotChatResult.Fail(BotChatOutcome.BadResponse)); }
            ChatGenerationResult result;
            try
            {
                result = await engine.GenerateAsync(tokens, settings, WorkloadOf(prompt.Workload), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                Count("cancelled");
                throw; // PlayerbotChat turns its own deadline into a timeout
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Interlocked.Increment(ref _errors);
                FaultLedger.Shared.Record("playerbot.chat-inprocess", error);
                FaultLedger.Shared.Record("playerbot.chat-inference", error);
                AnomalyLedger.Shared.Report("bot-chat", "inference-fault", "in-process chat model", $"{error.GetType().Name}: {error.Message}");
                _logger?.LogWarning("Bot chat in-process generation failed ({Type}: {Message}); the next provider answers", error.GetType().Name, error.Message);
                return Fallback("error", BotChatResult.Fail(BotChatOutcome.BadResponse));
            }

            return Finish(result, prompt, options);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private BotChatResult Finish(ChatGenerationResult result, BotChatPrompt prompt, PlayerbotChatInProcessOptions options)
    {
        lock (_gate)
        {
            _prefillMs += result.PrefillMs;
            _decodeMs += result.DecodeMs;
        }

        Interlocked.Add(ref _promptTokens, result.PromptTokens - result.ReusedTokens);
        Interlocked.Add(ref _reusedTokens, result.ReusedTokens);
        switch (result.Status)
        {
            case ChatGenerationStatus.Overrun:
                Overran(options);
                return Fallback("overrun", BotChatResult.Fail(BotChatOutcome.Timeout));
            case ChatGenerationStatus.Cancelled:
                Interlocked.Increment(ref _cancelled);
                return Fallback("cancelled", BotChatResult.Fail(BotChatOutcome.Timeout));
            case ChatGenerationStatus.ContextFull:
                return Fallback("context", BotChatResult.Fail(BotChatOutcome.BadResponse));
            case ChatGenerationStatus.Dropped:
                Interlocked.Increment(ref _dropped);
                return Fallback("dropped", BotChatResult.Fail(BotChatOutcome.RateLimited, TimeSpan.FromSeconds(1)));
            case ChatGenerationStatus.Failed:
                Interlocked.Increment(ref _errors);
                return Fallback("error", BotChatResult.Fail(BotChatOutcome.BadResponse));
        }

        lock (_gate) _consecutiveOverruns = 0;
        Interlocked.Add(ref _tokens, result.GeneratedTokens);
        if (InProcessChatMeters.Tokens.Enabled) InProcessChatMeters.Tokens.Add(result.GeneratedTokens);
        string? text = InProcessChatPrompt.Screen(result.Text, BotNameOf(prompt), options.MaxReplyCharacters, truncated: result.StopReason == "length");
        if (text is null)
        {
            Interlocked.Increment(ref _filtered);
            return Fallback("filtered", BotChatResult.Fail(BotChatOutcome.BadResponse));
        }

        Interlocked.Increment(ref _replies);
        if (InProcessChatMeters.Replies.Enabled) InProcessChatMeters.Replies.Add(1);
        return new BotChatResult(BotChatOutcome.Ok, text, result.PromptTokens, result.GeneratedTokens);
    }

    private void Overran(PlayerbotChatInProcessOptions options)
    {
        Interlocked.Increment(ref _overruns);
        if (InProcessChatMeters.Overruns.Enabled) InProcessChatMeters.Overruns.Add(1);
        bool opened = false;
        int seconds = options.CircuitBreakerSeconds;
        lock (_gate)
        {
            // A reply already in flight when the circuit opened is counted above but neither re-trips nor re-reports it:
            // the open circuit already answers for it, and the half-open counter (N-1) must survive the rest.
            if (_circuitOpenUntil > _time.GetUtcNow()) return;
            if (++_consecutiveOverruns >= options.CircuitBreakerOverruns)
            {
                _circuitOpenUntil = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
                _consecutiveOverruns = options.CircuitBreakerOverruns - 1; // half-open after the rest: one more overrun reopens it
                opened = true;
            }
        }

        if (!opened) return;
        Interlocked.Increment(ref _circuitOpens);
        AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + "chat", "inprocess-circuit-open", "in-process chat model",
            string.Create(CultureInfo.InvariantCulture, $"{options.CircuitBreakerOverruns} replies in a row ran over the {options.BudgetMs} ms budget; the templates answer for {seconds} s"),
            AnomalySeverity.Warning);
        _logger?.LogWarning("Bot chat in-process model ran over its {Budget} ms budget {Count} times in a row; the built-in templates answer for {Seconds} s",
            options.BudgetMs, options.CircuitBreakerOverruns, seconds);
    }

    private static BotChatResult Fallback(string reason, BotChatResult result)
    {
        Count(reason);
        return result;
    }

    private static void Count(string reason)
    {
        if (InProcessChatMeters.Fallbacks.Enabled) InProcessChatMeters.Fallbacks.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>The sampling of one reply: the ported sampler chain's settings, the chat-line constraint (one line) and the stop strings.</summary>
    internal static ChatGenerationSettings Settings(PlayerbotChatInProcessOptions options) => new(
        options.MaxNewTokens, options.BudgetMs, (float)options.Temperature, options.TopK, (float)options.TopP, (float)options.RepetitionPenalty, options.Seed,
        StopAtNewline: true, PenaltyWindow: options.RepetitionWindow, MinP: (float)options.MinP,
        StopSequences: options.StopSequences is { Count: > 0 } stops ? stops : null);

    /// <summary>The engine's scheduling settings.</summary>
    internal static ChatServingOptions Serving(PlayerbotChatInProcessOptions options)
        => new(options.PrefixCacheTokens, options.AmbientQueueMs, options.PrefillChunkTokens);

    /// <summary>The scheduler class of a request's workload.</summary>
    internal static WorkloadClass WorkloadOf(BotChatWorkload workload) => workload switch
    {
        BotChatWorkload.BotToBot => WorkloadClass.BotToBot,
        BotChatWorkload.Ambient => WorkloadClass.Ambient,
        _ => WorkloadClass.InteractivePlayer,
    };

    /// <summary>The bot's name from its facts ("You are Bob, a level ..."), for removing a "Bob:" the model may start with.</summary>
    internal static string BotNameOf(BotChatPrompt prompt)
    {
        const string lead = "You are ";
        string facts = prompt.DynamicSystem ?? string.Empty;
        if (!facts.StartsWith(lead, StringComparison.Ordinal)) return string.Empty;
        int comma = facts.IndexOf(',', lead.Length);
        return comma > lead.Length ? facts[lead.Length..comma] : string.Empty;
    }

    // --- loading ------------------------------------------------------------------------------------------------------------

    private Task<InProcessChatEngine?> EnsureLoaded()
    {
        lock (_gate)
        {
            if (_load is null && Volatile.Read(ref _disposed) != 0) return Task.FromResult<InProcessChatEngine?>(null);
            return _load ??= Task.Run(Load);
        }
    }

    private bool IsDisposed()
    {
        lock (_gate) return Volatile.Read(ref _disposed) != 0;
    }

    private InProcessChatEngine? Load()
    {
        PlayerbotChatInProcessOptions options = _options.Chat.InProcess;
        string path = options.ModelPath;
        try
        {
            if (string.IsNullOrWhiteSpace(options.ModelPath)) throw new InvalidDataException($"{PlayerbotOptions.SectionName}:Chat:InProcess:ModelPath is empty");
            path = Path.GetFullPath(Path.Combine(_contentRoot, options.ModelPath));
            InProcessChatEngine engine = _loader is { } load
                ? load(path, options)
                : InProcessChatEngine.Load(path, options.ContextTokens, options.Threads, options.MaxConcurrent, Serving(options));
            ChatModel model = engine.Model;
            int index = _options.Chat.EffectiveProviders().ToList().FindIndex(p => p.Kind == PlayerbotChatProviderKind.InProcess);
            _logger?.LogInformation("Bot chat provider #{Index}: InProcess {Model} {Arch} {Params} {Quant}, {Threads} threads, budget {Budget} ms",
                index, model.Name, model.Config.Architecture, ChatModel.FormatParameters(model.Parameters), model.Quantization, engine.Threads, options.BudgetMs);
            _logger?.LogInformation(
                "Bot chat in-process model {Path}: {Layers} layers, {Dim} wide, vocabulary {Vocab}, context {Context} tokens x {Slots} slot(s), {FileMb} MB mapped, {CacheMb} MB of cache and buffers, pre-tokenizer {Pre}{Exact}",
                path, model.Config.Layers, model.Config.Dim, model.Config.Vocabulary, engine.ContextTokens, engine.Slots,
                model.File.FileLength / (1024 * 1024), engine.AllocatedBytes / (1024 * 1024), model.Tokenizer.Pre, model.Tokenizer.PreExact ? string.Empty : " (unknown: the gpt2 split stands in)");
            if (IsDisposed()) { engine.Dispose(); return null; } // disposed while loading: nobody else owns this engine
            LogServing(engine, options, WarmSharedPrefix(engine));
            // Dispose sets _disposed before it waits on this load, so a result that lands after its 30 s timeout is caught here too.
            if (IsDisposed()) { engine.Dispose(); return null; }
            return engine;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // One report, then the templates: a bad model file must never stop the server or the bots.
            Volatile.Write(ref _loadError, error.Message);
            FaultLedger.Shared.Record("playerbot.chat-inference", error);
            AnomalyLedger.Shared.Report("bot-chat", "inference-fault", "chat model " + Path.GetFileName(path), $"{error.GetType().Name}: {error.Message}");
            AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + "chat", "model-load-failed", "chat model " + Path.GetFileName(path),
                $"{path}: {error.GetType().Name}: {error.Message}; the built-in templates answer instead");
            _logger?.LogWarning("Bot chat in-process model {Path} did not load ({Type}: {Message}); the built-in templates answer instead",
                path, error.GetType().Name, error.Message);
            return null;
        }
    }

    // --- status -------------------------------------------------------------------------------------------------------------

    /// <summary>Compute the system rules every prompt starts with once, into the prefix cache (a failure only costs the reuse). Returns (tokens, ms).</summary>
    private (int Tokens, double Ms) WarmSharedPrefix(InProcessChatEngine engine)
    {
        if (engine.PrefixCacheTokens == 0) return (0, 0);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Task<int> warm = engine.WarmPrefixAsync(InProcessChatPrompt.SharedPrefix(engine.Model.Tokenizer));
            int tokens = warm.Wait(TimeSpan.FromSeconds(60)) ? warm.Result : 0;
            return (tokens, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.chat-inference", error);
            AnomalyLedger.Shared.Report("bot-chat", "inference-fault", "in-process chat model", $"prefix warm: {error.GetType().Name}: {error.Message}");
            _logger?.LogWarning("Bot chat in-process model: the shared prompt prefix was not cached ({Type}: {Message}); every reply computes it", error.GetType().Name, error.Message);
            return (0, 0);
        }
    }

    private void LogServing(InProcessChatEngine engine, PlayerbotChatInProcessOptions options, (int Tokens, double Ms) warm)
    {
        ChatServingOptions serving = engine.Serving;
        _logger?.LogInformation(
            "Bot chat in-process scheduler: interactive_player > bot_to_bot > ambient (a higher class preempts a lower one, ambient dropped after {AmbientMs} ms queued), {Slots} slot(s), prefill chunk {Chunk} tokens; prefix cache {PrefixTokens} tokens, shared prefix {Warm} tokens prefilled once in {WarmMs:0} ms; sampling temperature {Temperature} top_k {TopK} top_p {TopP} min_p {MinP} repeat {Repeat} over {Window}, {Stops} stop string(s), one-line constraint on",
            serving.AmbientQueueMs, engine.Slots, serving.PrefillChunkTokens, engine.PrefixCacheTokens, warm.Tokens, warm.Ms,
            options.Temperature, options.TopK, options.TopP, options.MinP, options.RepetitionPenalty, options.RepetitionWindow, options.StopSequences?.Count ?? 0);
    }

    /// <summary>The counters (tests, the status line).</summary>
    internal InProcessChatStats Stats()
    {
        double prefill, decode;
        bool open;
        lock (_gate)
        {
            prefill = _prefillMs;
            decode = _decodeMs;
            open = _circuitOpenUntil > _time.GetUtcNow();
        }

        long tokens = Interlocked.Read(ref _tokens), computed = Interlocked.Read(ref _promptTokens);
        return new InProcessChatStats(Interlocked.Read(ref _replies), tokens, decode > 0 ? tokens / (decode / 1000) : 0,
            computed, Interlocked.Read(ref _reusedTokens), prefill > 0 ? computed / (prefill / 1000) : 0,
            Interlocked.Read(ref _overruns), Interlocked.Read(ref _circuitOpens), open, Interlocked.Read(ref _busy), Interlocked.Read(ref _filtered),
            Interlocked.Read(ref _unloaded), Interlocked.Read(ref _cancelled), Interlocked.Read(ref _errors), Interlocked.Read(ref _dropped));
    }

    /// <summary>The <c>.playerbot chat status</c> line, or null when no InProcess provider is configured and none was ever loaded.</summary>
    internal string? StatusLine()
    {
        if (!Configured && _load is null) return null;
        string state = _load switch
        {
            null => "not loaded",
            { IsCompleted: false } => "loading",
            { IsCompletedSuccessfully: true, Result: not null } => "loaded",
            _ => "failed (" + (LoadError ?? "unknown") + ")",
        };
        string model = Engine is { } engine
            ? $"{engine.Model.Name} {engine.Model.Config.Architecture} {ChatModel.FormatParameters(engine.Model.Parameters)} {engine.Model.Quantization} threads={engine.Threads} slots={engine.Slots}"
            : "-";
        InProcessChatStats s = Stats();
        PlayerbotChatInProcessOptions o = _options.Chat.InProcess;
        return string.Create(CultureInfo.InvariantCulture,
            $"Bot chat in-process: {state} model={model} budget={o.BudgetMs}ms replies={s.Replies} tokens={s.Tokens} tok/s={s.TokensPerSecond:0.0} prompt-tok/s={s.PromptTokensPerSecond:0.0} reused={s.ReusedTokens} fallbacks={s.Fallbacks} overruns={s.Overruns} circuit={(s.CircuitOpen ? "open" : "closed")} busy={s.Busy} filtered={s.Filtered} unloaded={s.Unloaded} cancelled={s.Cancelled} errors={s.Errors} dropped={s.Dropped}{(Engine is { } running ? " " + running.ServingStats.Describe() : string.Empty)}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task<InProcessChatEngine?>? load;
        lock (_gate) load = _load;
        if (load is null) return;
        try
        {
            if (load.Wait(TimeSpan.FromSeconds(30))) load.Result?.Dispose();
        }
        catch (AggregateException error)
        {
            FaultLedger.Shared.Record("playerbot.chat-inference", error);
            AnomalyLedger.Shared.Report("bot-chat", "inference-fault", "in-process chat model", $"{error.GetType().Name}: {error.Message}");
            _logger?.LogDebug(error, "Bot chat in-process model load faulted at shutdown");
        }
    }
}

/// <summary>The in-process model's counters (<c>.playerbot chat status</c>; tokens per second are averages since start).</summary>
internal readonly record struct InProcessChatStats(
    long Replies, long Tokens, double TokensPerSecond, long PromptTokens, long ReusedTokens, double PromptTokensPerSecond,
    long Overruns, long CircuitOpens, bool CircuitOpen, long Busy, long Filtered, long Unloaded, long Cancelled, long Errors, long Dropped = 0)
{
    /// <summary>Requests the templates (or the next provider) answered instead.</summary>
    public long Fallbacks => Overruns + Busy + Filtered + Unloaded + Cancelled + Errors + Dropped;
}

/// <summary>The in-process chat instruments, on the bot chat meter (<see cref="PlayerbotChatMeters.Meter"/>; docs/ops/metrics.md).</summary>
internal static class InProcessChatMeters
{
    internal static Counter<long> Replies { get; } = PlayerbotChatMeters.Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.inprocess.replies", "{reply}", "Bot chat replies written by the in-process model.");

    internal static Counter<long> Tokens { get; } = PlayerbotChatMeters.Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.inprocess.tokens", "{token}", "Tokens the in-process chat model generated.");

    internal static Counter<long> Overruns { get; } = PlayerbotChatMeters.Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.inprocess.overruns", "{reply}", "In-process chat replies dropped for running over the time budget.");

    /// <summary>By <c>reason</c>: overrun, circuit, busy, filtered, unloaded, context, cancelled, error, disposed.</summary>
    internal static Counter<long> Fallbacks { get; } = PlayerbotChatMeters.Meter.CreateCounter<long>(
        "arcanecore.playerbot.chat.inprocess.fallbacks", "{request}", "In-process chat requests the next provider (the templates) answered instead.");
}
