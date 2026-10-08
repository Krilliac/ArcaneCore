using System.Net;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Net;

/// <summary>
/// The transport protections of one listener in one object (docs/ops/netguard.md): the connection
/// caps (<see cref="ConnectionLimiter"/>, extended with the shared per-address cap), the per-address
/// connection-rate and authentication-failure budgets (<see cref="IpRateTable"/>) and one
/// <see cref="LogGate"/> per kind of refusal, so every refusal is one closed socket and at most one
/// log line per interval. Nothing here throws on the accept or read path: a refusal is a null lease
/// or a false, and a log line is written only when its gate admits it.
/// <para>
/// Ownership: one instance per listener (<c>LogonServer</c>, <c>WorldServer</c>), created when the
/// listener starts and shared with every session it admits. Thread-safe. The fast paths
/// (<see cref="TryAdmit"/>, <see cref="AllowsAuthAttempt"/>, <see cref="RecordAuthFailure"/>)
/// allocate only the lease object of an admitted connection; a refusal allocates nothing unless
/// its log line is admitted.
/// </para>
/// </summary>
public sealed class NetGuard
{
    private readonly ILogger _logger;
    private readonly LogGate _refusedCap;
    private readonly LogGate _refusedRate;
    private readonly LogGate _refusedAuth;
    private readonly LogGate _tableFull;
    private readonly LogGate _frameTimeout;
    private readonly LogGate _unauthenticatedTimeout;
    private readonly LogGate _packetDropped;
    private readonly LogGate _packetFlood;
    private long _refusedConnections;
    private long _refusedAuthAttempts;
    private long _droppedPackets;
    private long _floodDisconnects;
    private readonly IPNetwork[] _exempt;

    /// <param name="options">The <c>Net:Protection</c> section.</param>
    /// <param name="daemonMaxConnections">The daemon's own global cap (Auth:/World:MaxConnections), read at each admission.</param>
    /// <param name="daemonMaxPerIp">The daemon's own per-address cap (Auth:/World:MaxConnectionsPerIp), read at each admission.</param>
    /// <param name="logger">Where the rate-limited refusal lines go.</param>
    /// <param name="clock">Monotonic milliseconds; tests inject a fake.</param>
    /// <param name="bindAddress">
    /// The address the listener is bound to: a loopback bind exempts loopback clients from the per-address limits
    /// (<see cref="NetProtectionOptions.ExemptLoopbackOnLoopbackBind"/>). Null: not known, no loopback exemption.
    /// </param>
    public NetGuard(NetProtectionOptions options, Func<int> daemonMaxConnections, Func<int> daemonMaxPerIp, ILogger logger, Func<long>? clock = null,
        IPAddress? bindAddress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(daemonMaxConnections);
        ArgumentNullException.ThrowIfNull(daemonMaxPerIp);
        Options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Func<long> ticks = clock ?? Clock.Milliseconds;

        ExemptLoopback = options.ExemptLoopbackOnLoopbackBind && bindAddress is not null && IPAddress.IsLoopback(Normalize(bindAddress));
        _exempt = ParseExemptions(options.ExemptAddresses, _logger);
        // An exempt address is free of the shared cap only: the daemon's own cap, when an operator set one, still applies.
        Limiter = new ConnectionLimiter(daemonMaxConnections,
            address => IsExempt(address) ? Math.Max(0, daemonMaxPerIp()) : EffectivePerIpCap(daemonMaxPerIp(), options.MaxConnectionsPerIp));
        Table = new IpRateTable(Math.Max(1, options.MaxTrackedAddresses), options.AddressIdleEviction > TimeSpan.Zero ? options.AddressIdleEviction : TimeSpan.FromMinutes(10), ticks);
        Table.Configure(RateBucket.Connections, Math.Max(0, options.ConnectionBurstPerIp), Math.Max(0, options.ConnectionsPerMinutePerIp));
        Table.Configure(RateBucket.AuthFailures, Math.Max(0, options.AuthFailureBurstPerIp), Math.Max(0, options.AuthFailuresPerMinutePerIp));

        TimeSpan interval = options.LogInterval < TimeSpan.Zero ? TimeSpan.Zero : options.LogInterval;
        _refusedCap = new LogGate(interval, ticks);
        _refusedRate = new LogGate(interval, ticks);
        _refusedAuth = new LogGate(interval, ticks);
        _tableFull = new LogGate(interval, ticks);
        _frameTimeout = new LogGate(interval, ticks);
        _unauthenticatedTimeout = new LogGate(interval, ticks);
        _packetDropped = new LogGate(interval, ticks);
        _packetFlood = new LogGate(interval, ticks);
    }

    /// <summary>Whether loopback clients are exempt from the per-address limits (a loopback-bound listener, <see cref="NetProtectionOptions.ExemptLoopbackOnLoopbackBind"/>).</summary>
    public bool ExemptLoopback { get; }

    /// <summary>The parsed <see cref="NetProtectionOptions.ExemptAddresses"/> networks.</summary>
    public IReadOnlyList<IPNetwork> ExemptNetworks => _exempt;

    /// <summary>
    /// Whether <paramref name="address"/> is exempt from the shared per-address connection cap
    /// (<see cref="NetProtectionOptions.MaxConnectionsPerIp"/>). Nothing else is lifted: the daemon's own per-address cap, the
    /// global cap, the connection rate and the authentication failure budget still apply.
    /// </summary>
    public bool IsExempt(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress key = Normalize(address);
        if (ExemptLoopback && IPAddress.IsLoopback(key))
        {
            return true;
        }

        foreach (IPNetwork network in _exempt)
        {
            if (network.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// "a.b.c.d", "::1" or a CIDR network ("10.0.0.0/8"). False for anything else; used by the listener (an entry that does not
    /// parse exempts nothing) and by check-config.
    /// </summary>
    public static bool TryParseExemption(string? text, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            return IPNetwork.TryParse(trimmed, out network);
        }

        if (!IPAddress.TryParse(trimmed, out IPAddress? address))
        {
            return false;
        }

        address = Normalize(address);
        network = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    private static IPNetwork[] ParseExemptions(string[]? entries, ILogger logger)
    {
        var networks = new List<IPNetwork>();
        foreach (string entry in entries ?? [])
        {
            if (TryParseExemption(entry, out IPNetwork network))
            {
                networks.Add(network);
            }
            else
            {
                logger.LogWarning("Net:Protection:ExemptAddresses entry '{Entry}' is not an address or CIDR network; it exempts nothing", entry);
            }
        }

        return [.. networks];
    }

    /// <summary>World packets dropped by a connection's packet budgets (<see cref="OpcodeRateLimiter"/>) so far.</summary>
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    /// <summary>World connections closed as a packet flood so far.</summary>
    public long FloodDisconnects => Interlocked.Read(ref _floodDisconnects);

    /// <summary>
    /// The packet budgets of one new world connection (Net:Protection:World*), or null when every budget is 0. Each
    /// connection owns its limiter (it is not thread-safe); the options are read when the connection starts.
    /// </summary>
    public static OpcodeRateLimiter? CreatePacketLimiter(NetProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limiter = new OpcodeRateLimiter(options.WorldOpcodeBurst, options.WorldOpcodeRefillPerSecond, options.WorldPacketsPerSecond, options.WorldFloodPacketsPerSecond);
        return limiter.IsEnabled ? limiter : null;
    }

    /// <summary>A packet was over its connection's budget and was dropped; one rate-limited line per interval.</summary>
    public void ReportPacketDropped(string endpoint, string opcode)
    {
        Interlocked.Increment(ref _droppedPackets);
        if (_packetDropped.TryEnter(out int suppressed))
        {
            _logger.LogWarning("[{Endpoint}] {Opcode} over the connection's packet budget; dropped ({Suppressed} more dropped since the last line)", endpoint, opcode, suppressed);
        }
    }

    /// <summary>A connection sent more packets in one second than Net:Protection:WorldFloodPacketsPerSecond; the caller closes it.</summary>
    public void ReportPacketFlood(string endpoint)
    {
        Interlocked.Increment(ref _floodDisconnects);
        if (_packetFlood.TryEnter(out int suppressed))
        {
            _logger.LogWarning("[{Endpoint}] packet flood over {Limit} packets per second; disconnecting ({Suppressed} more since the last line)", endpoint, Options.WorldFloodPacketsPerSecond, suppressed);
        }
    }

    public NetProtectionOptions Options { get; }

    /// <summary>The connection caps (global and per address).</summary>
    public ConnectionLimiter Limiter { get; }

    /// <summary>The per-address budgets.</summary>
    public IpRateTable Table { get; }

    /// <summary>Connections refused by a cap or the connection rate so far.</summary>
    public long RefusedConnections => Interlocked.Read(ref _refusedConnections);

    /// <summary>Authentication attempts refused by the failure budget so far.</summary>
    public long RefusedAuthAttempts => Interlocked.Read(ref _refusedAuthAttempts);

    /// <summary>
    /// Times the per-address table was full for a newcomer and forgot its least recently seen
    /// address to track the newcomer instead (<see cref="IpRateTable.ForcedEvictions"/>). Never a
    /// refusal: a full table measures less, the connection caps are the fail-closed limit.
    /// </summary>
    public long ForcedEvictions => Table.ForcedEvictions;

    /// <summary>
    /// The per-address connection cap in force: the daemon's own and the shared one when both are
    /// set take the lower; 0 on either side means that side has no cap.
    /// </summary>
    public static int EffectivePerIpCap(int daemon, int shared)
    {
        if (daemon <= 0)
        {
            return Math.Max(0, shared);
        }

        return shared <= 0 ? daemon : Math.Min(daemon, shared);
    }

    /// <summary>
    /// Admit a connection from <paramref name="address"/>: the caps first, then the connection-rate
    /// budget. Returns the lease to dispose when the connection ends, or null when the connection
    /// must be closed (already logged, rate-limited).
    /// </summary>
    public IDisposable? TryAdmit(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IDisposable? lease = Limiter.TryAcquire(address);
        if (lease is null)
        {
            Interlocked.Increment(ref _refusedConnections);
            if (_refusedCap.TryEnter(out int suppressed))
            {
                _logger.LogWarning("[{Address}] connection refused: connection cap reached ({Suppressed} more refusal(s) since the last line)", address, suppressed);
            }

            return null;
        }

        if (!Table.IsEnabled(RateBucket.Connections))
        {
            return lease;
        }

        long forcedBefore = Table.ForcedEvictions;
        RateVerdict verdict = Table.TryTake(IpKey.From(address), RateBucket.Connections);
        NoteTableFull(forcedBefore);
        if (verdict == RateVerdict.Allowed)
        {
            return lease;
        }

        lease.Dispose();
        Interlocked.Increment(ref _refusedConnections);
        if (_refusedRate.TryEnter(out int suppressedByRate))
        {
            _logger.LogWarning("[{Address}] connection refused: connection rate exceeded ({Suppressed} more refusal(s) since the last line)", address, suppressedByRate);
        }

        return null;
    }

    /// <summary>
    /// One rate-limited line when the table had to forget an address to track a newcomer (the table
    /// is full of addresses seen within <see cref="NetProtectionOptions.AddressIdleEviction"/>): the
    /// operator sees that the budgets are measuring less and can raise MaxTrackedAddresses. Not a refusal.
    /// </summary>
    private void NoteTableFull(long forcedBefore)
    {
        if (Table.ForcedEvictions > forcedBefore && _tableFull.TryEnter(out int suppressed))
        {
            _logger.LogWarning("the per-address table is full ({Capacity} slots, Net:Protection:MaxTrackedAddresses): forgot the least recently seen address to track a new one; nothing was refused ({Suppressed} more since the last line)", Table.Capacity, suppressed);
        }
    }

    /// <summary>
    /// May a client at <paramref name="address"/> attempt to authenticate? False when its failure
    /// budget is spent, already logged; the caller closes the connection
    /// before any lookup. A connection without an IP address (a test harness, a Unix socket) is
    /// never limited: there is no key to count against.
    /// </summary>
    public bool AllowsAuthAttempt(in IpKey? address)
    {
        if (address is not { } key || !Table.IsEnabled(RateBucket.AuthFailures))
        {
            return true;
        }

        long forcedBefore = Table.ForcedEvictions;
        RateVerdict verdict = Table.Peek(key, RateBucket.AuthFailures);
        NoteTableFull(forcedBefore);
        if (verdict == RateVerdict.Allowed)
        {
            return true;
        }

        Interlocked.Increment(ref _refusedAuthAttempts);
        if (_refusedAuth.TryEnter(out int suppressed))
        {
            _logger.LogWarning("authentication attempt refused: too many failed attempts from one address ({Suppressed} more refusal(s) since the last line)", suppressed);
        }

        return false;
    }

    /// <summary>Charge one failed authentication attempt to <paramref name="address"/>.</summary>
    public void RecordAuthFailure(in IpKey? address)
    {
        if (address is { } key && Table.IsEnabled(RateBucket.AuthFailures))
        {
            Table.TryTake(key, RateBucket.AuthFailures);
        }
    }

    /// <summary>A frame was not completed within <see cref="NetProtectionOptions.FrameReadTimeout"/>; the caller closes the connection.</summary>
    public void ReportFrameTimeout(string endpoint)
    {
        if (_frameTimeout.TryEnter(out int suppressed))
        {
            _logger.LogWarning("[{Endpoint}] frame not completed within {Timeout}; closing ({Suppressed} more since the last line)", endpoint, Options.FrameReadTimeout, suppressed);
        }
    }

    /// <summary>A connection stayed unauthenticated past its lifetime; the caller closes it.</summary>
    public void ReportUnauthenticatedTimeout(string endpoint, TimeSpan lifetime)
    {
        if (_unauthenticatedTimeout.TryEnter(out int suppressed))
        {
            _logger.LogInformation("[{Endpoint}] not authenticated within {Lifetime}; closing ({Suppressed} more since the last line)", endpoint, lifetime, suppressed);
        }
    }
}
