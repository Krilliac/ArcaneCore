using System.Net;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Kernel.Net;

/// <summary>
/// Admission control for a TCP listener: a global connection cap and a per-IP cap, enforced
/// before any session, DI scope or database context exists. Both limits are hardening with no
/// vmangos equivalent (vmangos only exposes timeouts); 0 disables a limit. The defaults sit far
/// above anything a retail 1.12.1 client does (one connection per client).
/// </summary>
/// <param name="maxConnections">The global cap, read at each admission (0 = none).</param>
/// <param name="maxPerIpFor">The per-address cap of one (normalized) address, read at each admission (0 = none).</param>
public sealed class ConnectionLimiter(Func<int> maxConnections, Func<IPAddress, int> maxPerIpFor)
{
    /// <summary>The same per-address cap for every address.</summary>
    public ConnectionLimiter(Func<int> maxConnections, Func<int> maxPerIp)
        : this(maxConnections, _ => maxPerIp())
    {
    }

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, int> _perIp = [];
    private int _total;

    public ConnectionLimiter(int maxConnections, int maxPerIp)
        : this(() => maxConnections, () => maxPerIp)
    {
    }

    /// <summary>Connections currently admitted.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    /// <summary>
    /// Admit a connection from <paramref name="address"/>. Returns a lease to dispose when the
    /// connection ends, or null when a limit is reached (the caller closes the socket).
    /// </summary>
    public IDisposable? TryAcquire(IPAddress address)
    {
        // an IPv4 client reaching a dual-stack listener shows up as ::ffff:a.b.c.d
        IPAddress key = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        int maxTotal = maxConnections();
        int maxIp = maxPerIpFor(key);
        lock (_gate)
        {
            _perIp.TryGetValue(key, out int current);
            if ((maxTotal > 0 && _total >= maxTotal) || (maxIp > 0 && current >= maxIp))
            {
                return null;
            }

            _total++;
            _perIp[key] = current + 1;
        }

        return new Lease(this, key);
    }

    private void Release(IPAddress key)
    {
        lock (_gate)
        {
            // Every release pairs with one admitted lease (Lease.Dispose is guarded against a second
            // call), so the counters cannot go below zero; a negative total would silently disable the cap.
            if (Invariant.Check(_total > 0, "a connection lease was released without a matching admission (global count)"))
            {
                _total--;
            }

            if (Invariant.Check(_perIp.TryGetValue(key, out int current), $"a connection lease was released for {key} without a matching admission (per-address count)"))
            {
                if (current <= 1)
                {
                    _perIp.Remove(key);
                }
                else
                {
                    _perIp[key] = current - 1;
                }
            }
        }
    }

    private sealed class Lease(ConnectionLimiter owner, IPAddress key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(key);
            }
        }
    }
}
