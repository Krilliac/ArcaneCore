using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Warden;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Gateway;

/// <summary>
/// Warden on the gateway (<c>docs/CLUSTERING_DESIGN.md</c> section 8 Warden): the gateway holds the 40-byte session key and the client
/// stream, so it runs each session's <see cref="WardenSession"/> itself, answers CMSG_WARDEN_DATA locally and reports a failing
/// verdict to the worker over the link (a WardenVerdict frame), which applies the ban. One instance per gateway process.
/// <para>
/// Readiness: a gateway whose <c>Warden:Enabled</c> is true but whose options are invalid or whose module manifest is unusable
/// (<see cref="GatewayWardenManifestVerifier.EvaluateReadiness"/>) is not ready and refuses every login (AUTH_UNAVAILABLE), so a client
/// can never pick an unscanned gateway. Every gateway of a realm derives its manifest from the same options with
/// <see cref="GatewayWardenManifestVerifier.BuildManifest"/> and sends it as the posture of each verdict; the worker refuses a mismatch.
/// </para>
/// <para>
/// Limits: the gateway has no world database, so it runs <c>Warden:Checks</c> and the timing scan only (not the
/// <c>warden_checks</c> table); staff are not told of a verdict (the worker logs it).
/// </para>
/// Thread safety: immutable after construction; <see cref="Begin"/> may be called from any connection.
/// </summary>
public sealed class GatewayWarden
{
    private readonly ILogger _logger;

    /// <summary>Builds the gateway's Warden from the bound <c>Warden</c> options.</summary>
    public GatewayWarden(int realmId, WardenOptions options, ILogger logger)
        : this(GatewayWardenManifestVerifier.BuildManifest(realmId, options ?? throw new ArgumentNullException(nameof(options))), options.Validate(), options, logger)
    {
    }

    /// <summary>Test seam: a gateway Warden over an explicit manifest.</summary>
    internal GatewayWarden(GatewayWardenManifest manifest, IReadOnlyList<string> optionProblems, WardenOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(optionProblems);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Options = options;
        Verifier = new GatewayWardenManifestVerifier(manifest);
        if (manifest.Enabled && optionProblems.Count > 0)
        {
            IsReady = false;
            NotReadyReason = "invalid Warden options: " + string.Join(" ", optionProblems);
        }
        else
        {
            IsReady = Verifier.IsGatewayWardenReady;
            NotReadyReason = Verifier.FailureReason;
        }

        Posture = Verifier.ToPosture();
        if (manifest.Enabled)
        {
            if (options.LoadFromDatabase)
            {
                _logger.LogWarning("Warden:LoadFromDatabase is true but the gateway has no world database: only Warden:Checks and the timing scan run here, not the warden_checks table");
            }

            if (IsReady)
            {
                _logger.LogInformation("Warden runs on the gateway (scan action {Action}, protocol action {ProtocolAction}, {Checks} configured scan(s))",
                    options.Action, options.ProtocolAction, options.Checks.Count);
            }
            else
            {
                _logger.LogError("Warden is enabled but this gateway is not ready ({Reason}); it refuses logins until that is fixed", NotReadyReason);
            }
        }
    }

    /// <summary>The bound options (scan intervals, actions, exempt level).</summary>
    public WardenOptions Options { get; }

    /// <summary>The verifier over this gateway's own manifest.</summary>
    public GatewayWardenManifestVerifier Verifier { get; }

    /// <summary>This gateway's manifest.</summary>
    public GatewayWardenManifest Manifest => Verifier.Manifest;

    /// <summary>What this gateway sends with each verdict.</summary>
    public GatewayWardenPosture Posture { get; }

    /// <summary>Warden is enabled for this gateway's sessions.</summary>
    public bool IsEnabled => Manifest.Enabled;

    /// <summary>False when Warden is enabled and unusable (invalid options, module missing or failing its digest); a disabled Warden is ready.</summary>
    public bool IsReady { get; }

    /// <summary>Why <see cref="IsReady"/> is false (null when ready).</summary>
    public string? NotReadyReason { get; }

    /// <summary>
    /// Start Warden for a session that was just authenticated and opened on the worker, or null when it is not scanned (Warden off or
    /// not ready, a build other than <see cref="GatewayWardenManifestVerifier.WardenBuild"/>, a staff account at or above
    /// <see cref="WardenOptions.ExemptSecurity"/>). <paramref name="sessionKey"/> is copied into the crypto context, not retained.
    /// </summary>
    /// <param name="build">The client build.</param>
    /// <param name="security">The account's security level.</param>
    /// <param name="account">The account name, for log lines.</param>
    /// <param name="sessionKey">The 40-byte login key.</param>
    /// <param name="send">Sends one SMSG_WARDEN_DATA body to the client (thread-safe).</param>
    /// <param name="act">Called once per batch that calls for Kick or Ban, with the strongest action, the reasons in one line and the first such check's id.</param>
    internal GatewayWardenSession? Begin(uint build, AccountSecurity security, string account, ReadOnlySpan<byte> sessionKey, Action<byte[]> send, Action<WardenAction, string, uint?> act)
    {
        if (!IsEnabled || !IsReady || build != GatewayWardenManifestVerifier.WardenBuild || security >= Options.ExemptSecurity || sessionKey.Length != 40)
        {
            return null;
        }

        var session = new GatewayWardenSession(Options, account, sessionKey, send, act, _logger);
        session.Start();
        return session;
    }
}

/// <summary>
/// One client's Warden on the gateway: a <see cref="WardenSession"/> driven by the packets of the connection and a one-second timer
/// (the world drives it from its tick). Verdicts reported during one driver call are one batch, acted on once at the strongest action.
/// Thread-safe: the packet arrives on the connection's read loop, the update on a timer thread; both run under one drive gate.
/// </summary>
internal sealed class GatewayWardenSession : IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private readonly WardenSession _warden;
    private readonly string _account;
    private readonly Action<WardenAction, string, uint?> _act;
    private readonly ILogger _logger;
    private readonly Lock _drive = new();
    private readonly Lock _pendingGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _timer;
    private List<WardenVerdict>? _pending;
    private long _lastUpdateMs;
    private bool _disposed;

    internal GatewayWardenSession(WardenOptions options, string account, ReadOnlySpan<byte> sessionKey, Action<byte[]> send, Action<WardenAction, string, uint?> act, ILogger logger)
    {
        _account = account;
        _act = act;
        _logger = logger;
        _warden = new WardenSession(sessionKey, options, send, Report);
        _timer = new Timer(static state => ((GatewayWardenSession)state!).Tick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The wrapped session (diagnostics, tests).</summary>
    internal WardenSession Warden => _warden;

    /// <summary>MODULE_USE goes out and the update timer starts.</summary>
    internal void Start()
    {
        Drive(static (w, _) => w.Start(), 0u);
        lock (_drive)
        {
            if (!_disposed)
            {
                _lastUpdateMs = _clock.ElapsedMilliseconds;
                _timer.Change(UpdateInterval, UpdateInterval);
            }
        }
    }

    /// <summary>A CMSG_WARDEN_DATA body (still encrypted), from the connection's read loop.</summary>
    internal void Handle(byte[] payload) => Drive(static (w, body) => w.Handle(body), payload);

    private void Tick()
    {
        try
        {
            uint diff;
            lock (_drive)
            {
                if (_disposed)
                {
                    return;
                }

                long now = _clock.ElapsedMilliseconds;
                diff = (uint)Math.Clamp(now - _lastUpdateMs, 0L, (long)uint.MaxValue);
                _lastUpdateMs = now;
            }

            Drive(static (w, d) => w.Update(d), diff);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Warden update faulted for account {Account}", _account);
        }
    }

    private void Report(WardenVerdict verdict)
    {
        lock (_pendingGate)
        {
            (_pending ??= []).Add(verdict);
        }
    }

    private void Drive<TState>(Action<WardenSession, TState> call, TState state)
    {
        List<WardenVerdict>? batch;
        lock (_drive)
        {
            if (_disposed)
            {
                return;
            }

            call(_warden, state);
            batch = TakePending();
        }

        if (batch is not null)
        {
            OnVerdicts(batch);
        }
    }

    private List<WardenVerdict>? TakePending()
    {
        lock (_pendingGate)
        {
            List<WardenVerdict>? items = _pending;
            _pending = null;
            return items;
        }
    }

    /// <summary>Every verdict is logged; the batch is acted on once, at its strongest action (a Log batch only logs).</summary>
    private void OnVerdicts(List<WardenVerdict> batch)
    {
        WardenAction strongest = WardenAction.Log;
        List<WardenVerdict> acting = [];
        foreach (WardenVerdict verdict in batch)
        {
            _logger.LogWarning("Warden: account {Account}: {Reason}; action {Action}", _account, verdict.Reason, verdict.Action);
            if (verdict.Action != WardenAction.Log)
            {
                acting.Add(verdict);
                if (verdict.Action > strongest)
                {
                    strongest = verdict.Action;
                }
            }
        }

        if (acting.Count == 0)
        {
            return;
        }

        static string One(WardenVerdict v) => v.IsProtocolFailure ? $"{v.Reason} (protocol failure)" : v.Reason;
        string more = acting.Count > 3 ? $"; +{acting.Count - 3} more" : string.Empty;
        string reason = acting.Count == 1
            ? One(acting[0])
            : $"{acting.Count} failures: {string.Join("; ", acting.Take(3).Select(One))}{more}";
        _act(strongest, reason, acting.First(v => v.Action == strongest).CheckId);
    }

    /// <summary>Stop the timer and report the held protocol-verdict count (the connection closed). Idempotent.</summary>
    public void Dispose()
    {
        lock (_drive)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _warden.Flush();
        }

        _timer.Dispose();
        List<WardenVerdict>? held = TakePending();
        if (held is not null)
        {
            foreach (WardenVerdict verdict in held)
            {
                _logger.LogWarning("Warden: account {Account}: {Reason}; action {Action}", _account, verdict.Reason, verdict.Action);
            }
        }
    }
}
