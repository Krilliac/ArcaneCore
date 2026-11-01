using System.Security.Cryptography;

namespace ArcaneCore.World.Warden;

/// <summary>Where a session's Warden is (MaNGOS Zero WardenProtocol.h WardenState).</summary>
public enum WardenState
{
    NotStarted,
    AwaitingModuleStatus,
    AwaitingTransferResult,
    AwaitingHash,
    Ready,
    AwaitingCheckResult,
    Failed,
}

/// <summary>A failure Warden reports for a session: a protocol break or a failed scan, and the action it calls for.</summary>
public sealed record WardenVerdict(string Reason, WardenAction Action, bool IsProtocolFailure, uint? CheckId);

/// <summary>
/// One session's Warden (MaNGOS Zero src/game/Warden/WardenServer.cpp). MODULE_USE goes out first; a client that lacks the module gets it
/// in MODULE_CACHE chunks (once); a loaded module answers HASH_REQUEST with the expected 20 bytes, after which both RC4 directions switch
/// to the module's keys and MODULE_INITIALIZE installs the client callbacks. From then on a CHEAT_CHECKS_REQUEST goes out at a random gap
/// and every scan of the reply is checked (vmangos Warden::HandlePacket). Every awaited reply has a deadline; a protocol break or a late
/// reply is <see cref="WardenOptions.ProtocolAction"/>, capped at Kick (vmangos uses WARDEN_ACTION_KICK for every protocol penalty).
/// Under Log a failure sends nothing back: the handshake keeps waiting for its single outstanding request, and a failed scan batch is
/// sent once more, and a failed retry kicks (no batch is ever skipped). Failures to a request the server made are strikes; the
/// <see cref="WardenOptions.MaxProtocolFailures"/>th strike kicks even under Log, and a valid reply forgives one. Unsolicited packets are
/// never strikes. Repeated Log verdicts are reported at most once per <see cref="WardenOptions.ProtocolReportIntervalSeconds"/>, then as
/// one count; <see cref="Flush"/> reports the held count when the session ends. A failed scan uses its own action or
/// <see cref="WardenOptions.Action"/>.
/// Login-time scans (<see cref="WardenCheckOptions.InitialLogin"/>, and the scripted scans under <see cref="WardenOptions.ScriptedScans"/>)
/// go out right after MODULE_INITIALIZE, and the follow-ups a scripted scan queues go out as soon as its reply is handled (vmangos
/// Warden::RequestScans, EnqueueScans). Under <see cref="WardenOptions.ConfirmFailedScans"/> a failed scan is rescanned alone before it
/// counts (MaNGOS Zero WardenEnforcementPolicy).
/// Thread-safe: the packet arrives on the session task, the update on the world thread; every member runs under one lock.
/// </summary>
public sealed class WardenSession
{
    /// <summary>The most reply bytes one request may ask for (vmangos Warden.hpp MaxReply); applied to queued scans.</summary>
    private const int MaxReply = 256;

    private readonly object _lock = new();
    private readonly WardenCryptoContext _crypto;
    private readonly WardenOptions _options;
    private readonly IReadOnlyList<WardenCheck> _checks;
    private readonly Action<byte[]> _send;
    private readonly Action<WardenVerdict> _report;
    private readonly Random _random;
    private readonly byte[] _module;
    private IReadOnlyList<WardenCheck>? _pending;
    private long _deadlineMs;
    private long _nextScanMs;
    private int _nextCheck;
    private bool _transferred;
    private bool _batchRetried;
    private int _strikes;
    private long _quietUntilMs;
    private int _suppressed;
    private string _lastSuppressed = string.Empty;

    // Login-time scans (InitialLogin), the follow-up queue (initial and scripted scans, vmangos m_enqueuedScans) and the isolated
    // confirmation rescans (MaNGOS Zero WardenCheckPlanner m_confirmationQueue). Requests take confirmations first, then the queue, then
    // the rotation, and a queue or confirmation goes out as soon as the previous reply is handled.
    private readonly IReadOnlyList<WardenCheck> _initial;
    private readonly List<WardenCheck> _queue = [];
    private readonly List<WardenCheck> _confirmations = [];
    private readonly HashSet<uint> _confirming = [];
    private readonly HashSet<(uint CheckId, WardenCheckOutcome Outcome)> _audited = [];
    private readonly WardenScriptedScans? _scripted;
    private BatchSource _pendingSource;
    private int _pendingRotationCount;
    private int _batchesSinceHookCheck;

    private enum BatchSource
    {
        Rotation,
        Queue,
        Confirmation,
    }

    /// <summary>Create the session's Warden. <paramref name="sessionKey"/> is the 40-byte login key K; it is not retained.</summary>
    /// <param name="checks">The scans to run; null runs <c>options.Checks</c>.</param>
    public WardenSession(ReadOnlySpan<byte> sessionKey, WardenOptions options, Action<byte[]> send, Action<WardenVerdict> report, Random? random = null, IReadOnlyList<WardenCheckOptions>? checks = null)
        : this(sessionKey, options, send, report, random, checks, WardenModuleProfile.Module ?? throw new InvalidOperationException("the build 5875 Warden module resource is missing or corrupt"))
    {
    }

    private WardenSession(ReadOnlySpan<byte> sessionKey, WardenOptions options, Action<byte[]> send, Action<WardenVerdict> report, Random? random, IReadOnlyList<WardenCheckOptions>? configured, byte[] module)
    {
        _crypto = new WardenCryptoContext(sessionKey);
        _options = options;
        _send = send;
        _report = report;
        _random = random ?? new Random();
        _module = module;
        var checks = new List<WardenCheck>();
        var initial = new List<WardenCheck>();
        foreach (WardenCheckOptions check in configured ?? options.Checks)
        {
            if (WardenCheck.TryCreate(check, out WardenCheck? valid) is null)
            {
                // vmangos WardenScanMgr::GetRandomScans: an InitialLogin scan is sent only when login scans are asked for, never in the rotation.
                (check.InitialLogin ? initial : checks).Add(valid!);
            }
        }

        _initial = initial;
        _checks = checks.Count > 0 ? checks : [WardenCheck.Timing];
        if (options.ScriptedScans)
        {
            _scripted = new WardenScriptedScans(_queue.Add, reason => _report(new WardenVerdict(reason, WardenAction.Log, false, null)));
        }

        _nextCheck = _random.Next(_checks.Count); // a random start in the rotation, so a short session does not always run the same leading checks
    }

    public WardenState State { get; private set; } = WardenState.NotStarted;

    /// <summary>The session's own clock in milliseconds (advanced by <see cref="Update"/>).</summary>
    public long ClockMs { get; private set; }

    /// <summary>The scripted scans' per-session state when <see cref="WardenOptions.ScriptedScans"/> is on, else null (diagnostics, tests).</summary>
    internal WardenScriptedScans? Scripted => _scripted;

    /// <summary>Scans waiting to be sent ahead of the rotation: confirmation rescans and queued login or scripted scans (diagnostics, tests).</summary>
    internal int QueuedScans
    {
        get
        {
            lock (_lock)
            {
                return _confirmations.Count + _queue.Count;
            }
        }
    }

    /// <summary>Protocol verdicts held back by the report interval and not yet reported as a count (diagnostics, tests).</summary>
    public int HeldVerdicts
    {
        get
        {
            lock (_lock)
            {
                return _suppressed;
            }
        }
    }

    /// <summary>Report the held verdicts as one count now (the session is closing, or the server is stopping). Idempotent.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            ReportSuppressed();
        }
    }

    /// <summary>MODULE_USE (MaNGOS Zero WardenServer::Start).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (State != WardenState.NotStarted)
            {
                return;
            }

            SendPlain(WardenCodec.ModuleUse(_module.Length));
            Await(WardenState.AwaitingModuleStatus);
        }
    }

    /// <summary>A CMSG_WARDEN_DATA body (still encrypted).</summary>
    public void Handle(ReadOnlySpan<byte> encrypted)
    {
        lock (_lock)
        {
            if (State is WardenState.NotStarted or WardenState.Failed)
            {
                return; // nothing solicited yet: the stream is not advanced (MaNGOS Zero HandleEncrypted)
            }

            byte[] plain = encrypted.ToArray();
            _crypto.DecryptFromClient(plain);
            if (plain.Length == 0)
            {
                Fail("empty reply");
                return;
            }

            if (State == WardenState.Ready)
            {
                Fail("unsolicited reply");
                return;
            }

            if (State == WardenState.AwaitingCheckResult)
            {
                HandleCheckResult(plain);
                return;
            }

            var command = (WardenClientCommand)plain[0];
            int expected = command == WardenClientCommand.HashResult ? 21 : 1;
            if (command is not (WardenClientCommand.ModuleMissing or WardenClientCommand.ModuleOk or WardenClientCommand.HashResult or WardenClientCommand.ModuleFailed)
                || plain.Length != expected)
            {
                Fail($"unexpected command {plain[0]} ({plain.Length} bytes)");
                return;
            }

            switch (State)
            {
                case WardenState.AwaitingModuleStatus:
                case WardenState.AwaitingTransferResult:
                    if (command == WardenClientCommand.ModuleOk)
                    {
                        Forgive();
                        SendPlain(WardenCodec.HashRequest());
                        Await(WardenState.AwaitingHash);
                    }
                    else if (command == WardenClientCommand.ModuleMissing && !_transferred)
                    {
                        // One bounded transfer (MaNGOS Zero WardenLimits.maxTransfers 1): a second miss is a load failure.
                        _transferred = true;
                        for (int offset = 0; offset < _module.Length; offset += _options.ChunkSize)
                        {
                            SendPlain(WardenCodec.ModuleCache(_module.AsSpan(offset, Math.Min(_options.ChunkSize, _module.Length - offset))));
                        }

                        Await(WardenState.AwaitingTransferResult);
                    }
                    else
                    {
                        // A miss after the transfer is a failed load too (vmangos Warden.cpp:546-552 kicks); the module is not offered again.
                        Fail(command == WardenClientCommand.ModuleFailed ? "the client failed to load the module"
                            : command == WardenClientCommand.ModuleMissing ? "the client still lacks the module after the transfer"
                            : $"unexpected command {command}");
                    }

                    return;

                case WardenState.AwaitingHash:
                    if (command != WardenClientCommand.HashResult
                        || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(plain.AsSpan(1), WardenModuleProfile.ClientKeySeedHash))
                    {
                        Fail("failed challenge response"); // vmangos Warden::HandleChallengeResponse
                        return;
                    }

                    Forgive();
                    _crypto.InstallModuleKeys(WardenModuleProfile.ClientKeySeed, WardenModuleProfile.ServerKeySeed);
                    SendPlain(WardenCodec.ModuleInitialize()); // the first body under the module's keys; the module does not acknowledge it
                    State = WardenState.Ready;

                    // vmangos Warden::HandlePacket HASH_RESULT: after InitializeClient, RequestScans(SelectScans(InitialLogin)) at once.
                    // Every login scan is queued (vmangos keeps a random NumScans of them); they go out in back-to-back requests.
                    _queue.AddRange(_initial);
                    if (_scripted is not null)
                    {
                        _queue.AddRange(_scripted.InitialLoginScans());
                    }

                    if (_queue.Count > 0)
                    {
                        RequestScans();
                    }
                    else
                    {
                        ScheduleScan(first: true);
                    }

                    return;
            }
        }
    }

    /// <summary>Advance the clock: deadlines expire, and a ready session sends its next scan request when due.</summary>
    public void Update(uint diffMs)
    {
        lock (_lock)
        {
            ClockMs += diffMs;
            if (State is WardenState.NotStarted or WardenState.Failed)
            {
                return;
            }

            if (_suppressed > 0 && ClockMs >= _quietUntilMs)
            {
                ReportSuppressed();
                _quietUntilMs = ClockMs + (1000L * _options.ProtocolReportIntervalSeconds);
            }

            switch (State)
            {
                case WardenState.Ready:
                    if (ClockMs >= _nextScanMs)
                    {
                        RequestScans();
                    }

                    return;
                default:
                    if (ClockMs >= _deadlineMs)
                    {
                        WardenState timedOut = State;
                        Fail($"no reply in {_options.ResponseTimeoutSeconds} s ({timedOut})"); // vmangos Warden::Update client response timeout
                        if (State == timedOut)
                        {
                            // A handshake stage under Log keeps waiting for its one outstanding request; nothing is re-sent.
                            Await(timedOut);
                        }
                    }

                    return;
            }
        }
    }

    /// <summary>
    /// Send the next request: one confirmation rescan alone (MaNGOS Zero CheckPlanPurpose::Confirmation), else the queued scans (up to
    /// ScansPerRequest and the module's 256-byte reply limit, vmangos Warden::RequestScans / MaxReply), else the rotation, which also
    /// carries the scripted EndScene hook check once per pass.
    /// </summary>
    private void RequestScans()
    {
        var batch = new List<WardenCheck>();
        _pendingRotationCount = 0;
        if (_confirmations.Count > 0)
        {
            _pendingSource = BatchSource.Confirmation;
            batch.Add(_confirmations[0]);
            _confirmations.RemoveAt(0);
        }
        else if (_queue.Count > 0)
        {
            _pendingSource = BatchSource.Queue;
            int reply = 0;
            while (_queue.Count > 0 && batch.Count < _options.ScansPerRequest && (batch.Count == 0 || reply + _queue[0].MaxReplySize <= MaxReply))
            {
                reply += _queue[0].MaxReplySize;
                batch.Add(_queue[0]);
                _queue.RemoveAt(0);
            }
        }
        else
        {
            _pendingSource = BatchSource.Rotation;
            WardenCheck? hook = TakeHookCheck();
            int count = Math.Min(_options.ScansPerRequest - (hook is null ? 0 : 1), _checks.Count);
            for (int i = 0; i < count; i++)
            {
                batch.Add(_checks[_nextCheck]);
                _nextCheck = (_nextCheck + 1) % _checks.Count;
            }

            _pendingRotationCount = count;
            if (hook is not null)
            {
                batch.Add(hook);
            }
        }

        _pending = batch;
        // The scan seeds key the page and driver HMACs the client must answer, so they come from the cryptographic generator: a
        // System.Random sequence can be recovered from the seeds it already sent, letting a cheat precompute the next batch's answers.
        SendPlain(WardenCodec.CheckRequest(batch, static () => (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue)));
        Await(WardenState.AwaitingCheckResult);
    }

    private void HandleCheckResult(byte[] plain)
    {
        IReadOnlyList<WardenCheck> pending = _pending!;
        List<(WardenCheck Check, WardenCheckOutcome Outcome)>? outcomes = WardenCodec.CheckOutcomes(plain, pending);
        if (outcomes is null)
        {
            Fail("malformed scan reply or checksum"); // vmangos Warden.cpp:587-594
            return;
        }

        bool confirmation = _pendingSource == BatchSource.Confirmation;
        _pending = null;
        _batchRetried = false;
        Forgive();
        State = WardenState.Ready;

        // The whole reply is valid: scripted scans may now store what they read and queue their next stage (vmangos ReadScanResults).
        foreach ((WardenCheck check, _) in outcomes)
        {
            check.ApplyScript();
        }

        foreach ((WardenCheck check, WardenCheckOutcome outcome) in outcomes)
        {
            Judge(check, outcome, confirmation);
        }

        // Follow-ups and confirmations are requested at once (vmangos ReadScanResults: RequestScans({}); MaNGOS Zero planner: confirmations
        // are immediate); otherwise the next rotation request waits for its gap.
        if (_confirmations.Count > 0 || _queue.Count > 0)
        {
            RequestScans();
        }
        else
        {
            ScheduleScan();
        }
    }

    /// <summary>
    /// Report one scan's outcome. Without <see cref="WardenOptions.ConfirmFailedScans"/> a failed scan takes its action at once (vmangos
    /// Warden::ReadScanResults ApplyPenalty). With it, the MaNGOS Zero evidence policy (WardenEnforcementPolicy::EvaluateBatch) applies: an
    /// eligible failure is queued for one isolated rescan (once per check while pending, and not again for an outcome already audited), the
    /// rescan's outcome is classified (cleared, audit, incident), and a failure that cannot be confirmed acts only if its class is actionable.
    /// </summary>
    private void Judge(WardenCheck check, WardenCheckOutcome outcome, bool confirmation)
    {
        string what = check.Source.Comment.Length > 0 ? check.Source.Comment : check.Kind.ToString();
        string detail = check.FailureDetail is { } d ? $": {d}" : string.Empty;
        WardenAction action = check.Source.Action ?? _options.Action;
        if (!_options.ConfirmFailedScans)
        {
            if (WardenEvidence.IsFailure(outcome))
            {
                _report(new WardenVerdict($"failed scan {check.Id} ({what}){detail}", action, false, check.Id));
            }

            return;
        }

        if (confirmation)
        {
            _confirming.Remove(check.Id);
            switch (WardenEvidence.ClassifyConfirmed(check.EvidenceClass, outcome))
            {
                case WardenConfirmedDisposition.Cleared:
                    _report(new WardenVerdict($"scan {check.Id} ({what}) passed its confirmation rescan; no finding", WardenAction.Log, false, check.Id));
                    break;
                case WardenConfirmedDisposition.Incident:
                    _report(new WardenVerdict($"failed scan {check.Id} ({what}){detail}; confirmed by an isolated rescan", action, false, check.Id));
                    break;
                default:
                    _audited.Add((check.Id, outcome));
                    _report(new WardenVerdict($"scan {check.Id} ({what}) confirmed {outcome}{detail}; {check.EvidenceClass} evidence is logged only", WardenAction.Log, false, check.Id));
                    break;
            }

            return;
        }

        if (!WardenEvidence.IsFailure(outcome))
        {
            return;
        }

        if (WardenEvidence.NeedsConfirmation(check, outcome))
        {
            if (!_audited.Contains((check.Id, outcome)) && _confirming.Add(check.Id))
            {
                _confirmations.Add(check);
            }

            return;
        }

        // Not confirmable (timing, scripted): actionable evidence acts at once, health and corroboration evidence is logged.
        _report(new WardenVerdict($"failed scan {check.Id} ({what}){detail}", WardenEvidence.IsActionable(check.EvidenceClass) ? action : WardenAction.Log, false, check.Id));
    }

    /// <summary>
    /// The scripted EndScene hook check for a rotation request, once per pass through the rotation (so it runs about as often as any one
    /// configured check; vmangos draws it from the same random pool), or null when EndScene is not known.
    /// </summary>
    private WardenCheck? TakeHookCheck()
    {
        if (_scripted?.HookCheck is not { } hook)
        {
            return null;
        }

        int batchesPerPass = Math.Max(1, (_checks.Count + _options.ScansPerRequest - 1) / _options.ScansPerRequest);
        if (++_batchesSinceHookCheck < batchesPerPass)
        {
            return null;
        }

        _batchesSinceHookCheck = 0;
        return hook;
    }

    /// <summary>Put a failed request's scans back where they came from, so the retry sends the same scans.</summary>
    private void RequeuePending()
    {
        switch (_pendingSource)
        {
            case BatchSource.Rotation:
                _nextCheck = (_nextCheck - _pendingRotationCount + _checks.Count) % _checks.Count; // the hook check rides the next pass
                break;
            case BatchSource.Queue:
                _queue.InsertRange(0, _pending!);
                break;
            case BatchSource.Confirmation:
                _confirmations.InsertRange(0, _pending!);
                break;
        }
    }

    /// <summary>
    /// Pick the next scan time. The first scan after the handshake lands at a random point of its gap (at least one second out, so the
    /// client has processed MODULE_INITIALIZE), so sessions that log in together, or run with a fixed interval, do not all scan on the
    /// same tick.
    /// </summary>
    private void ScheduleScan(bool first = false)
    {
        uint min = _options.ScanIntervalMinSeconds, max = _options.ScanIntervalMaxSeconds;
        long gapMs = 1000L * _random.NextInt64(min, (long)max + 1);
        _nextScanMs = ClockMs + (first ? _random.NextInt64(Math.Min(1000L, gapMs), gapMs + 1) : gapMs);
    }

    private void Await(WardenState state)
    {
        State = state;
        _deadlineMs = ClockMs + (1000L * _options.ResponseTimeoutSeconds);
    }

    private void Fail(string reason)
    {
        WardenState failedState = State;

        // Never above Kick: a late reply from a lagging client must not ban its account (vmangos Warden.cpp:166-173, 583, 589, 664).
        WardenAction action = _options.ProtocolAction == WardenAction.Log ? WardenAction.Log : WardenAction.Kick;

        // A failure to answer what the server asked is a strike; an unsolicited packet in Ready is not (it cannot stop scans).
        if (failedState != WardenState.Ready)
        {
            _strikes++;
        }

        // The retry of a failed scan batch failing again is the only way to make the server drop that batch, so it kicks: a protocol
        // failure is a client-side fault (a bad configured check shows up as a failed scan, not here), and vmangos kicks on the first
        // bad checksum (Warden.cpp:583, 589). Skipping chosen checks therefore always ends the session.
        bool retryFailed = failedState == WardenState.AwaitingCheckResult && _batchRetried;
        if (action == WardenAction.Log && (retryFailed || _strikes >= _options.MaxProtocolFailures))
        {
            action = WardenAction.Kick;
            reason = retryFailed ? $"{reason}; the retried scan batch failed again" : $"{reason}; {_strikes} protocol failures without a valid reply";
        }

        if (action != WardenAction.Log)
        {
            _pending = null;
            State = WardenState.Failed;
            ReportSuppressed();
            _report(new WardenVerdict(reason, action, true, null));
            return;
        }

        ReportThrottled(reason);

        // A Log verdict keeps the client online, so it must also leave a live Warden cycle, without answering the client with a packet.
        // The received body was already decrypted; both RC4 directions remain aligned.
        // Ready: an unsolicited packet must not push the scheduled scan into the future.
        // Handshake stages: keep waiting for the one outstanding request and its deadline. Re-sending HASH_REQUEST under the old keys
        // would desynchronize RC4 with a client whose late reply already switched to the module keys.
        if (failedState == WardenState.AwaitingCheckResult)
        {
            // Re-run a failed batch once so one bad reply does not skip its checks (a failed retry has already kicked above).
            RequeuePending();
            _batchRetried = true;
            _pending = null;
            State = WardenState.Ready;
            ScheduleScan();
        }
    }

    /// <summary>
    /// A valid reply forgives one strike, so occasional lag never adds up to a kick. Forgiveness cannot buy a skipped check: a failed
    /// batch is always retried, and a failed retry kicks.
    /// </summary>
    private void Forgive()
    {
        // A valid reply ends the failure streak: report the held count now, ahead of any verdict from this reply. Unlike the Update path this
        // does not restart the report interval; a valid reply comes at most once per request the server made, so it cannot flood.
        ReportSuppressed();
        if (_strikes > 0)
        {
            _strikes--;
        }
    }

    /// <summary>Report a Log verdict, or count it while the previous one is less than an interval old.</summary>
    private void ReportThrottled(string reason)
    {
        if (ClockMs < _quietUntilMs)
        {
            _suppressed++;
            _lastSuppressed = reason;
            return;
        }

        ReportSuppressed();
        _report(new WardenVerdict(reason, WardenAction.Log, true, null));
        _quietUntilMs = ClockMs + (1000L * _options.ProtocolReportIntervalSeconds);
    }

    private void ReportSuppressed()
    {
        if (_suppressed == 0)
        {
            return;
        }

        _report(new WardenVerdict($"{_suppressed} more protocol failure(s) since the last report (last: {_lastSuppressed})", WardenAction.Log, true, null));
        _suppressed = 0;
    }

    private void SendPlain(byte[] plain)
    {
        _crypto.EncryptToClient(plain);
        _send(plain);
    }
}
