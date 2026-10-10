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
/// reply is <see cref="WardenOptions.ProtocolAction"/> (never above Kick), a failed scan its own action or <see cref="WardenOptions.Action"/>.
/// Thread-safe: the packet arrives on the session task, the update on the world thread.
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

            if (State == WardenState.Ready)
            {
                Fail("unsolicited reply");
                return;
            }

            byte[] plain = encrypted.ToArray();
            _crypto.DecryptFromClient(plain);
            if (plain.Length == 0)
            {
                Fail("empty reply");
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
                        Fail(command == WardenClientCommand.ModuleFailed ? "the client failed to load the module" : $"unexpected command {command}");
                    }

                    return;

                case WardenState.AwaitingHash:
                    if (command != WardenClientCommand.HashResult
                        || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(plain.AsSpan(1), WardenModuleProfile.ClientKeySeedHash))
                    {
                        Fail("failed challenge response"); // vmangos Warden::HandleChallengeResponse
                        return;
                    }

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
            switch (State)
            {
                case WardenState.NotStarted or WardenState.Failed:
                    return;
                case WardenState.Ready:
                    if (ClockMs >= _nextScanMs)
                    {
                        RequestScans();
                    }

                    return;
                default:
                    if (ClockMs >= _deadlineMs)
                    {
                        Fail($"no reply in {_options.ResponseTimeoutSeconds} s ({State})"); // vmangos Warden::Update client response timeout
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
        _pending = null;
        List<WardenCheck>? failed = WardenCodec.CheckResult(plain, pending);
        if (failed is null)
        {
            Fail("malformed scan reply or checksum"); // vmangos Warden.cpp:587-594
            return;
        }

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
        State = WardenState.Failed;
        _pending = null;
        WardenAction action = _options.ProtocolAction > WardenAction.Kick ? WardenAction.Kick : _options.ProtocolAction;
        _report(new WardenVerdict(reason, action, true, null));
    }

    private void SendPlain(byte[] plain)
    {
        _crypto.EncryptToClient(plain);
        _send(plain);
    }
}
