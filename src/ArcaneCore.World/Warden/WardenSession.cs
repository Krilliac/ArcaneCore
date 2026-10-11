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
/// sent once more, then skipped. Failures to a request the server made are strikes; the <see cref="WardenOptions.MaxProtocolFailures"/>th
/// strike kicks even under Log, and a valid reply forgives one. Unsolicited packets are never strikes. Repeated Log verdicts are reported
/// at most once per <see cref="WardenOptions.ProtocolReportIntervalSeconds"/>, then as one count. A failed scan uses its own action or
/// <see cref="WardenOptions.Action"/>.
/// Thread-safe: the packet arrives on the session task, the update on the world thread; every member runs under one lock.
/// </summary>
public sealed class WardenSession
{
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
        foreach (WardenCheckOptions check in configured ?? options.Checks)
        {
            if (WardenCheck.TryCreate(check, out WardenCheck? valid) is null)
            {
                checks.Add(valid!);
            }
        }

        _checks = checks.Count > 0 ? checks : [WardenCheck.Timing];
    }

    public WardenState State { get; private set; } = WardenState.NotStarted;

    /// <summary>The session's own clock in milliseconds (advanced by <see cref="Update"/>).</summary>
    public long ClockMs { get; private set; }

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
                    ScheduleScan();
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

    private void RequestScans()
    {
        int count = Math.Min(_options.ScansPerRequest, _checks.Count);
        var batch = new List<WardenCheck>(count);
        for (int i = 0; i < count; i++)
        {
            batch.Add(_checks[_nextCheck]);
            _nextCheck = (_nextCheck + 1) % _checks.Count;
        }

        _pending = batch;
        SendPlain(WardenCodec.CheckRequest(batch, () => (uint)_random.NextInt64(0, 1L << 32)));
        Await(WardenState.AwaitingCheckResult);
    }

    private void HandleCheckResult(byte[] plain)
    {
        IReadOnlyList<WardenCheck> pending = _pending!;
        List<WardenCheck>? failed = WardenCodec.CheckResult(plain, pending);
        if (failed is null)
        {
            Fail("malformed scan reply or checksum"); // vmangos Warden.cpp:587-594
            return;
        }

        _pending = null;
        _batchRetried = false;
        Forgive();
        State = WardenState.Ready;
        ScheduleScan();
        foreach (WardenCheck check in failed)
        {
            string what = check.Source.Comment.Length > 0 ? check.Source.Comment : check.Kind.ToString();
            _report(new WardenVerdict($"failed scan {check.Id} ({what})", check.Source.Action ?? _options.Action, false, check.Id));
        }
    }

    private void ScheduleScan()
    {
        uint min = _options.ScanIntervalMinSeconds, max = _options.ScanIntervalMaxSeconds;
        _nextScanMs = ClockMs + (1000L * _random.NextInt64(min, (long)max + 1));
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
        if (failedState != WardenState.Ready && ++_strikes >= _options.MaxProtocolFailures && action == WardenAction.Log)
        {
            action = WardenAction.Kick;
            reason = $"{reason}; {_strikes} protocol failures without a valid reply";
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
            // Re-run a failed batch once so one bad reply does not skip its checks; a second failure advances past it.
            if (_batchRetried)
            {
                _batchRetried = false;
            }
            else
            {
                _nextCheck = (_nextCheck - _pending!.Count + _checks.Count) % _checks.Count;
                _batchRetried = true;
            }

            _pending = null;
            State = WardenState.Ready;
            ScheduleScan();
        }
    }

    /// <summary>A valid reply forgives one strike, so a client alternating good and bad replies still reaches the limit.</summary>
    private void Forgive()
    {
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
