using System.Collections.Concurrent;
using ArcaneCore.Kernel.Cluster.Auxiliary;

namespace ArcaneCore.World.Cluster.Auxiliary;

/// <summary>
/// Realm-scoped playerbot chat service host (<c>docs/CLUSTERING_DESIGN.md</c> §8 Playerbot chat).
/// Screens every line before any provider call, deduplicates by <see cref="ClusterBotChatAskId"/>,
/// enforces a single realm-wide provider budget, and emits advisory mutes when safety screening trips.
/// </summary>
public sealed class RemoteBotChatServiceHost : IClusterBotChatService
{
    private readonly Func<ClusterBotChatAsk, CancellationToken, ValueTask<string>> _externalProvider;
    private readonly Func<string, string?>? _safetyScreen;
    private readonly int _maxDailyProviderCalls;
    private readonly ConcurrentDictionary<ClusterBotChatAskId, (ClusterBotChatAdmission Admission, ClusterBotChatReply? Reply, ClusterBotChatMuteAdvisory? Mute)> _completedAsks = new();
    private int _providerCallCount;
    private volatile bool _isAvailable = true;

    public RemoteBotChatServiceHost(
        Func<ClusterBotChatAsk, CancellationToken, ValueTask<string>> externalProvider,
        Func<string, string?>? safetyScreen = null,
        int maxDailyProviderCalls = 1000)
    {
        ArgumentNullException.ThrowIfNull(externalProvider);
        if (maxDailyProviderCalls <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDailyProviderCalls), "maxDailyProviderCalls must be positive");
        }

        _externalProvider = externalProvider;
        _safetyScreen = safetyScreen;
        _maxDailyProviderCalls = maxDailyProviderCalls;
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set => _isAvailable = value;
    }

    /// <summary>Total number of outbound provider invocations performed by this service instance.</summary>
    public int ProviderCallCount => Volatile.Read(ref _providerCallCount);

    public async ValueTask<(ClusterBotChatAdmission Admission, ClusterBotChatReply? Reply, ClusterBotChatMuteAdvisory? Mute)> AskAsync(
        ClusterBotChatAsk ask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (!_isAvailable)
        {
            throw new IOException("remote bot chat service is unavailable");
        }

        if (_completedAsks.TryGetValue(ask.AskId, out var existing))
        {
            var duplicateAdmission = new ClusterBotChatAdmission(
                ask.AskId,
                ClusterBotChatAdmissionOutcome.Duplicate,
                "duplicate ask_id");
            return (duplicateAdmission, existing.Reply, existing.Mute);
        }

        // Safety screening always runs BEFORE any external provider is contacted (CLUSTERING_DESIGN.md:423-425).
        string? violation = _safetyScreen?.Invoke(ask.Text);
        if (!string.IsNullOrEmpty(violation))
        {
            var declined = new ClusterBotChatAdmission(ask.AskId, ClusterBotChatAdmissionOutcome.Declined, violation);
            var mute = new ClusterBotChatMuteAdvisory(ask.SenderAccountId, ask.SenderName, Minutes: 10, Reason: violation);
            _completedAsks.TryAdd(ask.AskId, (declined, null, mute));
            return (declined, null, mute);
        }

        int callNum = Interlocked.Increment(ref _providerCallCount);
        if (callNum > _maxDailyProviderCalls)
        {
            Interlocked.Decrement(ref _providerCallCount);
            var capped = new ClusterBotChatAdmission(
                ask.AskId,
                ClusterBotChatAdmissionOutcome.Declined,
                "realm daily provider cap reached");
            _completedAsks.TryAdd(ask.AskId, (capped, null, null));
            return (capped, null, null);
        }

        string replyText = await _externalProvider(ask, cancellationToken).ConfigureAwait(false);
        var admission = new ClusterBotChatAdmission(ask.AskId, ClusterBotChatAdmissionOutcome.Accepted, null);
        var reply = new ClusterBotChatReply(
            ask.AskId,
            ask.BotId,
            ask.BotGuid,
            ask.AskId.WorldIncarnation,
            ask.SenderName,
            ask.Channel,
            replyText,
            Provider: "remote-llm");

        var record = (admission, (ClusterBotChatReply?)reply, (ClusterBotChatMuteAdvisory?)null);
        _completedAsks.TryAdd(ask.AskId, record);
        return record;
    }
}

/// <summary>
/// Worker-side coordinator for playerbot chat (<c>docs/CLUSTERING_DESIGN.md</c> §8 Playerbot chat, §10 M15.6).
/// Routes asks to <see cref="IClusterBotChatService"/> when available, falls back cleanly to built-in template
/// replies when the service is down (sending nothing to external providers), guarantees at-most-once reply delivery,
/// and drops replies for bots that left the world or changed incarnation.
/// </summary>
public sealed class LocalOrRemoteBotChatCoordinator
{
    private readonly IClusterBotChatService? _remoteService;
    private readonly Func<ClusterBotChatAsk, string> _builtInFallbackProvider;
    private readonly Func<string, string?>? _localSafetyScreen;
    private readonly ConcurrentDictionary<int, (ulong BotGuid, Guid WorldIncarnation)> _onlineBots = new();
    private readonly ConcurrentDictionary<ClusterBotChatAskId, byte> _spokenAskIds = new();

    public LocalOrRemoteBotChatCoordinator(
        IClusterBotChatService? remoteService,
        Func<ClusterBotChatAsk, string> builtInFallbackProvider,
        Func<string, string?>? localSafetyScreen = null)
    {
        ArgumentNullException.ThrowIfNull(builtInFallbackProvider);
        _remoteService = remoteService;
        _builtInFallbackProvider = builtInFallbackProvider;
        _localSafetyScreen = localSafetyScreen;
    }

    public void RegisterBotOnline(int botId, ulong botGuid, Guid worldIncarnation)
    {
        _onlineBots[botId] = (botGuid, worldIncarnation);
    }

    public void UnregisterBot(int botId)
    {
        _onlineBots.TryRemove(botId, out _);
    }

    public async ValueTask<(ClusterBotChatAdmission Admission, ClusterBotChatReply? DeliveredReply, ClusterBotChatMuteAdvisory? Mute)> ProcessAskAsync(
        ClusterBotChatAsk ask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ask);

        // Local screening runs before any remote or fallback provider so unscreened text never leaves the realm.
        string? localViolation = _localSafetyScreen?.Invoke(ask.Text);
        if (!string.IsNullOrEmpty(localViolation))
        {
            return (
                new ClusterBotChatAdmission(ask.AskId, ClusterBotChatAdmissionOutcome.Declined, localViolation),
                null,
                new ClusterBotChatMuteAdvisory(ask.SenderAccountId, ask.SenderName, Minutes: 10, Reason: localViolation));
        }

        ClusterBotChatAdmission admission;
        ClusterBotChatReply? candidateReply;
        ClusterBotChatMuteAdvisory? mute;

        if (_remoteService is not null)
        {
            try
            {
                (admission, candidateReply, mute) = await _remoteService.AskAsync(ask, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                // Service is down: send nothing to external providers and fall back to built-in template replies.
                admission = new ClusterBotChatAdmission(
                    ask.AskId,
                    ClusterBotChatAdmissionOutcome.ServiceUnavailableFallback,
                    ex.Message);
                candidateReply = new ClusterBotChatReply(
                    ask.AskId,
                    ask.BotId,
                    ask.BotGuid,
                    ask.AskId.WorldIncarnation,
                    ask.SenderName,
                    ask.Channel,
                    _builtInFallbackProvider(ask),
                    Provider: "builtin");
                mute = null;
            }
        }
        else
        {
            admission = new ClusterBotChatAdmission(
                ask.AskId,
                ClusterBotChatAdmissionOutcome.ServiceUnavailableFallback,
                "local builtin mode");
            candidateReply = new ClusterBotChatReply(
                ask.AskId,
                ask.BotId,
                ask.BotGuid,
                ask.AskId.WorldIncarnation,
                ask.SenderName,
                ask.Channel,
                _builtInFallbackProvider(ask),
                Provider: "builtin");
            mute = null;
        }

        if (candidateReply is null || admission.Outcome == ClusterBotChatAdmissionOutcome.Duplicate)
        {
            return (admission, null, mute);
        }

        // Drop reply if the bot left the world or changed incarnation while the ask was in flight.
        if (!_onlineBots.TryGetValue(candidateReply.BotId, out (ulong BotGuid, Guid WorldIncarnation) online)
            || online.BotGuid != candidateReply.BotGuid
            || online.WorldIncarnation != candidateReply.WorldIncarnation)
        {
            return (admission, null, mute);
        }

        // At-most-once speaking guarantee per AskId on the worker.
        if (!_spokenAskIds.TryAdd(ask.AskId, 0))
        {
            return (
                new ClusterBotChatAdmission(ask.AskId, ClusterBotChatAdmissionOutcome.Duplicate, "already spoken on worker"),
                null,
                mute);
        }

        return (admission, candidateReply, mute);
    }
}

/// <summary>
/// Worker-side client for the optional remote/local playerbot planner (<c>docs/CLUSTERING_DESIGN.md</c> §8 Playerbot planning, §10 M15.6).
/// Enforces the closed-candidate contract (at most 16 candidates), discards late, stale-epoch, stale-incarnation, or unoffered
/// candidate choices, and falls back to the deterministic bot brain whenever a proposal is discarded or unavailable.
/// </summary>
public sealed class ClusterBotPlannerClient
{
    public const int MaxCandidateCount = 16;

    private readonly IClusterBotPlannerService? _plannerService;
    private readonly Func<ClusterBotPlanRequest, int> _deterministicBrainFallback;
    private readonly Func<DateTimeOffset> _clock;

    public ClusterBotPlannerClient(
        IClusterBotPlannerService? plannerService,
        Func<ClusterBotPlanRequest, int> deterministicBrainFallback,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(deterministicBrainFallback);
        _plannerService = plannerService;
        _deterministicBrainFallback = deterministicBrainFallback;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<(int ChosenCandidateId, bool UsedRemoteChoice, string? DiscardReason)> EvaluatePlanAsync(
        ClusterBotPlanRequest request,
        Guid activeWorkerIncarnation,
        long activeOwnerEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CandidateIds is null || request.CandidateIds.Count == 0)
        {
            throw new ArgumentException("CandidateIds must contain at least one candidate", nameof(request));
        }

        if (request.CandidateIds.Count > MaxCandidateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"CandidateIds count ({request.CandidateIds.Count}) exceeds MaxCandidateCount ({MaxCandidateCount})");
        }

        if (_plannerService is null)
        {
            return (_deterministicBrainFallback(request), false, "no planner service configured");
        }

        ClusterBotPlanChoice choice;
        try
        {
            choice = await _plannerService.SelectPlanAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (_deterministicBrainFallback(request), false, $"planner service error: {ex.Message}");
        }

        if (!string.Equals(choice.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            return (_deterministicBrainFallback(request), false, $"mismatched request_id '{choice.RequestId}' != '{request.RequestId}'");
        }

        if (choice.WorkerIncarnation != activeWorkerIncarnation)
        {
            return (
                _deterministicBrainFallback(request),
                false,
                $"stale worker incarnation {choice.WorkerIncarnation} != active {activeWorkerIncarnation}");
        }

        if (choice.OwnerEpoch != activeOwnerEpoch)
        {
            return (
                _deterministicBrainFallback(request),
                false,
                $"stale owner epoch {choice.OwnerEpoch} != active {activeOwnerEpoch}");
        }

        DateTimeOffset now = _clock();
        if (choice.RespondedAtUtc > request.DeadlineUtc || now > request.DeadlineUtc)
        {
            return (
                _deterministicBrainFallback(request),
                false,
                $"late plan choice (deadline={request.DeadlineUtc:O}, responded={choice.RespondedAtUtc:O}, now={now:O})");
        }

        if (choice.SelectedCandidateId is not int selectedId)
        {
            return (_deterministicBrainFallback(request), false, "planner returned no candidate");
        }

        bool wasOffered = false;
        for (int i = 0; i < request.CandidateIds.Count; i++)
        {
            if (request.CandidateIds[i] == selectedId)
            {
                wasOffered = true;
                break;
            }
        }

        if (!wasOffered)
        {
            return (
                _deterministicBrainFallback(request),
                false,
                $"planner selected unoffered candidate_id {selectedId}");
        }

        return (selectedId, true, null);
    }
}
