using System.Net;
using System.Net.Sockets;

namespace ArcaneCore.World.Ops.Accounts;

/// <summary>
/// The in-memory failure bookkeeping of the web login (docs/areas/atlas-public.md, "Login"): no persisted per-account lockout exists in this
/// codebase (the realm's protection is the per-address burst), so the web login keeps its own. Failures are counted per
/// (account name, client network), the network being the IPv4 /24 or the IPv6 /64: <see cref="NetworkLimit"/> failures in
/// <see cref="Window"/> from one network lock that account for that network only, so a stranger who knows a staff account's name cannot lock the
/// owner out from another network. A soft ceiling over all networks, <see cref="GlobalSoftLimit"/> failures in the window, adds
/// <see cref="SoftDelay"/> to each further attempt instead of refusing it. Account names are keyed upper-case and whether the account exists is
/// never consulted, so the counters are no oracle. Memory is bounded by <c>maxTracked</c> keys: expired ones go first, then the key with the fewest
/// failures (oldest among equals) that is not yet at its limit, so a flood of invented names evicts other one-off names and never a standing lockout or a
/// near-limit count; when every key is at its limit a new key is not tracked.
/// Thread-safe: one lock; every operation is O(failures in the window).
/// </summary>
internal sealed class WebLoginLockout(TimeProvider time, int maxTracked = WebLoginLockout.DefaultMaxTracked)
{
    /// <summary>Failures from one network that lock one account for that network.</summary>
    public const int NetworkLimit = 5;

    /// <summary>Failures from all networks after which each further attempt on the account is delayed.</summary>
    public const int GlobalSoftLimit = 30;

    /// <summary>The default bound on tracked keys.</summary>
    public const int DefaultMaxTracked = 20_000;

    /// <summary>How long a failure counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The delay added to an attempt on an account over the soft ceiling.</summary>
    public static readonly TimeSpan SoftDelay = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Account, string Network), Queue<long>> _byNetwork = [];
    private readonly Dictionary<string, Queue<long>> _byAccount = [];
    private readonly int _maxTracked = Math.Max(16, maxTracked);

    /// <summary>Keys tracked now (tests).</summary>
    public int Tracked
    {
        get
        {
            lock (_gate)
            {
                return _byNetwork.Count + _byAccount.Count;
            }
        }
    }

    /// <summary>The network a client address belongs to for the counters: its IPv4 /24 or IPv6 /64 as text; <c>unknown</c> for a missing or unparseable address.</summary>
    public static string NetworkOf(string? clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp) || !IPAddress.TryParse(clientIp.Trim(), out IPAddress? address))
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out int written))
        {
            return "unknown";
        }

        return address.AddressFamily == AddressFamily.InterNetwork
            ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24"
            : new IPAddress(bytes[..8].ToArray().Concat(new byte[8]).ToArray()) + "/64";
    }

    /// <summary>Whether <paramref name="account"/> is locked for <paramref name="network"/> now.</summary>
    public bool IsLocked(string account, string network)
    {
        long now = Now;
        lock (_gate)
        {
            return _byNetwork.TryGetValue((Key(account), network), out Queue<long>? failures) && Count(failures, now) >= NetworkLimit;
        }
    }

    /// <summary>The extra wait for an attempt on <paramref name="account"/>: <see cref="SoftDelay"/> over the soft ceiling, else zero.</summary>
    public TimeSpan DelayFor(string account)
    {
        long now = Now;
        lock (_gate)
        {
            return _byAccount.TryGetValue(Key(account), out Queue<long>? failures) && Count(failures, now) >= GlobalSoftLimit ? SoftDelay : TimeSpan.Zero;
        }
    }

    /// <summary>Count one failed attempt on <paramref name="account"/> from <paramref name="network"/>.</summary>
    public void RecordFailure(string account, string network)
    {
        long now = Now;
        string key = Key(account);
        lock (_gate)
        {
            bool networkRoom = true;
            bool accountRoom = true;
            if (_byNetwork.Count + _byAccount.Count >= _maxTracked)
            {
                MakeRoom(now, out networkRoom, out accountRoom);
            }

            if (networkRoom || _byNetwork.ContainsKey((key, network)))
            {
                Enqueue(_byNetwork, (key, network), now);
            }

            if (accountRoom || _byAccount.ContainsKey(key))
            {
                Enqueue(_byAccount, key, now);
            }
        }
    }

    /// <summary>A successful login from <paramref name="network"/>: forget that network's failures for the account (the all-network count keeps ageing out on its own).</summary>
    public void RecordSuccess(string account, string network)
    {
        lock (_gate)
        {
            _byNetwork.Remove((Key(account), network));
        }
    }

    private static string Key(string account) => account.Trim().ToUpperInvariant();

    private long Now => time.GetUtcNow().UtcTicks;

    private static int Count(Queue<long> failures, long now)
    {
        long cutoff = now - Window.Ticks;
        while (failures.Count > 0 && failures.Peek() <= cutoff)
        {
            failures.Dequeue();
        }

        return failures.Count;
    }

    private static void Enqueue<TKey>(Dictionary<TKey, Queue<long>> table, TKey key, long now)
        where TKey : notnull
    {
        if (!table.TryGetValue(key, out Queue<long>? failures))
        {
            table[key] = failures = new Queue<long>(4);
        }

        Count(failures, now);
        failures.Enqueue(now);
    }

    /// <summary>
    /// Under the gate: drop keys whose failures have all expired; when none had, evict from each table the key with the fewest failures (oldest last
    /// failure among equals) that is still under its limit. A key at its limit (a lockout, the soft ceiling) is never evicted, so the outputs say
    /// whether each table has room for a new key.
    /// </summary>
    private void MakeRoom(long now, out bool networkRoom, out bool accountRoom)
    {
        List<(string, string)> staleNetwork = [.. _byNetwork.Where(p => Count(p.Value, now) == 0).Select(p => p.Key)];
        foreach ((string, string) key in staleNetwork)
        {
            _byNetwork.Remove(key);
        }

        List<string> staleAccount = [.. _byAccount.Where(p => Count(p.Value, now) == 0).Select(p => p.Key)];
        foreach (string key in staleAccount)
        {
            _byAccount.Remove(key);
        }

        networkRoom = true;
        accountRoom = true;
        if (_byNetwork.Count + _byAccount.Count < _maxTracked)
        {
            return;
        }

        networkRoom = EvictWeakest(_byNetwork, NetworkLimit);
        accountRoom = EvictWeakest(_byAccount, GlobalSoftLimit);
    }

    private static bool EvictWeakest<TKey>(Dictionary<TKey, Queue<long>> table, int limit)
        where TKey : notnull
    {
        bool found = false;
        TKey weakest = default!;
        int weakestCount = int.MaxValue;
        long weakestLast = long.MaxValue;
        foreach (KeyValuePair<TKey, Queue<long>> pair in table)
        {
            int count = pair.Value.Count;
            if (count >= limit)
            {
                continue;
            }

            long last = pair.Value.Last();
            if (count < weakestCount || (count == weakestCount && last < weakestLast))
            {
                found = true;
                weakest = pair.Key;
                weakestCount = count;
                weakestLast = last;
            }
        }

        return found && table.Remove(weakest);
    }
}
