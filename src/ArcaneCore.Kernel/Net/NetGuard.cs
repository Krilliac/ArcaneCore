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
    private readonly LogGate _saturated;
    private readonly LogGate _frameTimeout;
    private readonly LogGate _unauthenticatedTimeout;
    private long _refusedConnections;
    private long _refusedAuthAttempts;
    private long _saturations;

    /// <param name="options">The <c>Net:Protection</c> section.</param>
    /// <param name="daemonMaxConnections">The daemon's own global cap (Auth:/World:MaxConnections), read at each admission.</param>
    /// <param name="daemonMaxPerIp">The daemon's own per-address cap (Auth:/World:MaxConnectionsPerIp), read at each admission.</param>
    /// <param name="logger">Where the rate-limited refusal lines go.</param>
    /// <param name="clock">Monotonic milliseconds; tests inject a fake.</param>
    public NetGuard(NetProtectionOptions options, Func<int> daemonMaxConnections, Func<int> daemonMaxPerIp, ILogger logger, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(daemonMaxConnections);
        ArgumentNullException.ThrowIfNull(daemonMaxPerIp);
        Options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Func<long> ticks = clock ?? Clock.Milliseconds;

        Limiter = new ConnectionLimiter(daemonMaxConnections, () => EffectivePerIpCap(daemonMaxPerIp(), options.MaxConnectionsPerIp));
        Table = new IpRateTable(Math.Max(1, options.MaxTrackedAddresses), options.AddressIdleEviction > TimeSpan.Zero ? options.AddressIdleEviction : TimeSpan.FromMinutes(10), ticks);
        Table.Configure(RateBucket.Connections, Math.Max(0, options.ConnectionBurstPerIp), Math.Max(0, options.ConnectionsPerMinutePerIp));
        Table.Configure(RateBucket.AuthFailures, Math.Max(0, options.AuthFailureBurstPerIp), Math.Max(0, options.AuthFailuresPerMinutePerIp));

        TimeSpan interval = options.LogInterval < TimeSpan.Zero ? TimeSpan.Zero : options.LogInterval;
        _refusedCap = new LogGate(interval, ticks);
        _refusedRate = new LogGate(interval, ticks);
        _refusedAuth = new LogGate(interval, ticks);
        _saturated = new LogGate(interval, ticks);
        _frameTimeout = new LogGate(interval, ticks);
        _unauthenticatedTimeout = new LogGate(interval, ticks);
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

    /// <summary>Refusals caused by a full address table so far.</summary>
    public long Saturations => Interlocked.Read(ref _saturations);

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

        RateVerdict verdict = Table.TryTake(IpKey.From(address), RateBucket.Connections);
        if (verdict == RateVerdict.Allowed)
        {
            return lease;
        }

        lease.Dispose();
        Interlocked.Increment(ref _refusedConnections);
        if (verdict == RateVerdict.Saturated)
        {
            Interlocked.Increment(ref _saturations);
            if (_saturated.TryEnter(out int suppressed))
            {
                _logger.LogWarning("[{Address}] connection refused: the per-address table is full ({Capacity} slots, Net:Protection:MaxTrackedAddresses; {Suppressed} more refusal(s) since the last line)", address, Table.Capacity, suppressed);
            }
        }
        else if (_refusedRate.TryEnter(out int suppressed))
        {
            _logger.LogWarning("[{Address}] connection refused: connection rate exceeded ({Suppressed} more refusal(s) since the last line)", address, suppressed);
        }

        return null;
    }

    /// <summary>
    /// May a client at <paramref name="address"/> attempt to authenticate? False when its failure
    /// budget is spent (or the table is full), already logged; the caller closes the connection
    /// before any lookup. A connection without an IP address (a test harness, a Unix socket) is
    /// never limited: there is no key to count against.
    /// </summary>
    public bool AllowsAuthAttempt(in IpKey? address)
    {
        if (address is not { } key || !Table.IsEnabled(RateBucket.AuthFailures))
        {
            return true;
        }

        RateVerdict verdict = Table.Peek(key, RateBucket.AuthFailures);
        if (verdict == RateVerdict.Allowed)
        {
            return true;
        }

        Interlocked.Increment(ref _refusedAuthAttempts);
        if (verdict == RateVerdict.Saturated)
        {
            Interlocked.Increment(ref _saturations);
            if (_saturated.TryEnter(out int suppressed))
            {
                _logger.LogWarning("authentication attempt refused: the per-address table is full ({Capacity} slots, Net:Protection:MaxTrackedAddresses; {Suppressed} more refusal(s) since the last line)", Table.Capacity, suppressed);
            }
        }
        else if (_refusedAuth.TryEnter(out int suppressed))
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
