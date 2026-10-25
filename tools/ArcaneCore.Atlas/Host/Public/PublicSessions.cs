using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>Who a public session is.</summary>
public enum PublicTier
{
    /// <summary>Anonymous visitor.</summary>
    Guest,

    /// <summary>Logged in with a game account.</summary>
    Account,
}

/// <summary>
/// One visitor's server-side state (design 0.4 steps 5 and 6). Holds no password or verifier. The cookie value is never stored: sessions are
/// found by the SHA-256 of it. Mutable fields are touched under <see cref="PublicSessions"/>' lock or are volatile; socket closers are registered
/// by the hub and called when the session ends.
/// </summary>
public sealed class PublicSession
{
    private readonly Lock _socketGate = new();
    private readonly List<Action<string>> _closers = [];
    private long _lastUse;
    private long _bytes;

    internal PublicSession(string hashKey, PublicTier tier, string ipKey, string clientIp, DateTimeOffset now, string? account, int security, string? ticket)
    {
        HashKey = hashKey;
        Ref = hashKey[..8];
        Tier = tier;
        IpKey = ipKey;
        ClientIp = clientIp;
        Created = now;
        _lastUse = now.UtcTicks;
        Account = account;
        Security = security;
        Ticket = ticket;
        Csrf = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        OpaqueKey = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>The SHA-256 of the cookie value, base64url (the lookup key; never sent to the browser).</summary>
    internal string HashKey { get; }

    /// <summary>Eight hex characters naming the session in logs and operator commands.</summary>
    public string Ref { get; }

    public PublicTier Tier { get; }

    /// <summary>The address key (IPv6 by /64) the session was created from; every per-address limit uses it.</summary>
    public string IpKey { get; }

    /// <summary>The client address text, for the operator's session list and the world login hint.</summary>
    public string ClientIp { get; }

    public DateTimeOffset Created { get; }

    /// <summary>The account name for an Account session (as the world answered), else null.</summary>
    public string? Account { get; }

    /// <summary>The account's security level for display; the world decides what it may do.</summary>
    public int Security { get; }

    /// <summary>The world's opaque ticket for an Account session, else null.</summary>
    internal string? Ticket { get; }

    /// <summary>The token a POST must send as <c>X-Atlas-CSRF</c> and a socket in its <c>auth</c> frame.</summary>
    public string Csrf { get; }

    /// <summary>The key the opaque entity ids are derived with (<c>HMAC-SHA256(key, guid)</c>); new on every login and logout.</summary>
    internal byte[] OpaqueKey { get; }

    internal DateTimeOffset LastUse => new(Interlocked.Read(ref _lastUse), TimeSpan.Zero);

    internal void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastUse, now.UtcTicks);

    /// <summary>Bytes sent to this session on its sockets.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    internal void AddBytes(long count) => Interlocked.Add(ref _bytes, count);

    /// <summary>Whether <paramref name="candidate"/> equals this session's CSRF token (constant time).</summary>
    public bool CsrfMatches(string? candidate)
        => candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(Csrf));

    /// <summary>The open sockets (tests and the operator's list).</summary>
    public int SocketCount
    {
        get
        {
            lock (_socketGate)
            {
                return _closers.Count;
            }
        }
    }

    /// <summary>Registers a callback that closes a socket with a reason; returns the disposable that unregisters it.</summary>
    internal IDisposable RegisterSocket(Action<string> close)
    {
        lock (_socketGate)
        {
            _closers.Add(close);
        }

        return new Unregister(this, close);
    }

    internal void CloseSockets(string reason)
    {
        Action<string>[] closers;
        lock (_socketGate)
        {
            closers = [.. _closers];
            _closers.Clear();
        }

        foreach (Action<string> close in closers)
        {
            try
            {
                close(reason);
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The socket is already gone.
            }
        }
    }

    private sealed class Unregister(PublicSession session, Action<string> close) : IDisposable
    {
        public void Dispose()
        {
            lock (session._socketGate)
            {
                session._closers.Remove(close);
            }
        }
    }
}

/// <summary>A session and the cookie value that names it (shown once, at creation).</summary>
/// <param name="Session">The session.</param>
/// <param name="Cookie">The cookie value: 32 random bytes, base64url.</param>
internal sealed record IssuedSession(PublicSession Session, string Cookie);

/// <summary>The sessions a logout or a kick ended, with what the world must be told.</summary>
/// <param name="Session">The ended session.</param>
internal sealed record EndedSession(PublicSession Session, string? Ticket);

/// <summary>
/// The public listener's sessions (design 0.4 steps 5 and 6, 0.9): cookie lookup by hash, idle and absolute expiry, a global and per-address cap on
/// guest sessions with least-recently-used eviction (never an Account session), rotation of id, CSRF token and opaque-id key at login and logout
/// (session fixation), and kick/revoke for the operator. Thread-safe (one lock; operations are O(sessions) only for eviction, O(1) otherwise).
/// </summary>
internal sealed class PublicSessions(PublicOptions options, TimeProvider time)
{
    private readonly Dictionary<string, PublicSession> _byHash = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Sessions held now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byHash.Count;
            }
        }
    }

    /// <summary>How many sessions of each tier exist.</summary>
    public (int Guests, int Accounts, int AccountsPrivileged) Counts()
    {
        lock (_gate)
        {
            int guests = _byHash.Values.Count(s => s.Tier == PublicTier.Guest);
            int privileged = _byHash.Values.Count(s => s.Tier == PublicTier.Account && s.Security >= (int)options.MinActionSecurity && s.Security > 0);
            return (guests, _byHash.Count - guests, privileged);
        }
    }

    /// <summary>
    /// Creates a guest session for a page load. At the per-address cap the address's idlest guest session is evicted; at the global cap the idlest
    /// guest session anywhere is. Returns null only when the cap is full of Account sessions (nothing may be evicted).
    /// </summary>
    public IssuedSession? CreateGuest(string ipKey, string clientIp)
    {
        lock (_gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            SweepLocked(now);
            if (_byHash.Values.Count(s => s.IpKey == ipKey && s.Tier == PublicTier.Guest) >= options.Guest.MaxSessionsPerIp
                && !EvictIdlestGuestLocked(s => s.IpKey == ipKey))
            {
                return null;
            }

            if (_byHash.Count >= options.Guest.MaxSessions && !EvictIdlestGuestLocked(_ => true))
            {
                return null;
            }

            return AddLocked(PublicTier.Guest, ipKey, clientIp, now, null, 0, null);
        }
    }

    /// <summary>The live session a cookie names (touching its idle clock), or null when it is unknown, idle-expired or past its absolute lifetime.</summary>
    public PublicSession? Find(string? cookie)
    {
        if (!TryHash(cookie, out string hash))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_byHash.TryGetValue(hash, out PublicSession? session))
            {
                return null;
            }

            DateTimeOffset now = time.GetUtcNow();
            if (Expired(session, now))
            {
                _byHash.Remove(hash);
                session.CloseSockets("expired");
                return null;
            }

            session.Touch(now);
            return session;
        }
    }

    /// <summary>
    /// A successful login: ends <paramref name="old"/> (its sockets close, its cookie stops working) and issues an Account session with a new id,
    /// CSRF token and opaque-id key. The old session's ticket, if any, is returned so the caller can end it in the world.
    /// </summary>
    public (IssuedSession Issued, EndedSession? Ended) Login(PublicSession? old, string ipKey, string clientIp, string account, int security, string ticket)
    {
        lock (_gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            EndedSession? ended = old is not null && _byHash.Remove(old.HashKey) ? new EndedSession(old, old.Ticket) : null;
            ended?.Session.CloseSockets("login");
            return (AddLocked(PublicTier.Account, ipKey, clientIp, now, account, security, ticket), ended);
        }
    }

    /// <summary>A logout: ends the session and issues a fresh guest session (new id, CSRF token and key).</summary>
    public (IssuedSession Issued, EndedSession Ended)? Logout(PublicSession session)
    {
        lock (_gate)
        {
            if (!_byHash.Remove(session.HashKey))
            {
                return null;
            }

            session.CloseSockets("logout");
            return (AddLocked(PublicTier.Guest, session.IpKey, session.ClientIp, time.GetUtcNow(), null, 0, null), new EndedSession(session, session.Ticket));
        }
    }

    /// <summary>Ends every session matching <paramref name="predicate"/> (operator kick, revoke); returns what ended. Sockets close with <paramref name="reason"/>.</summary>
    public IReadOnlyList<EndedSession> End(Func<PublicSession, bool> predicate, string reason)
    {
        lock (_gate)
        {
            List<PublicSession> matches = [.. _byHash.Values.Where(predicate)];
            foreach (PublicSession session in matches)
            {
                _byHash.Remove(session.HashKey);
                session.CloseSockets(reason);
            }

            return [.. matches.Select(s => new EndedSession(s, s.Ticket))];
        }
    }

    /// <summary>Removes idle-expired and over-age sessions; returns their tickets for the world.</summary>
    public IReadOnlyList<EndedSession> Sweep()
    {
        lock (_gate)
        {
            return SweepLocked(time.GetUtcNow());
        }
    }

    /// <summary>A copy of the sessions for the operator's list.</summary>
    public IReadOnlyList<PublicSession> Snapshot()
    {
        lock (_gate)
        {
            return [.. _byHash.Values.OrderBy(s => s.Created)];
        }
    }

    /// <summary>The session with this operator reference, or null.</summary>
    public PublicSession? FindByRef(string reference)
    {
        lock (_gate)
        {
            return _byHash.Values.FirstOrDefault(s => string.Equals(s.Ref, reference, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool Expired(PublicSession session, DateTimeOffset now)
        => now - session.LastUse > TimeSpan.FromMinutes(options.Session.IdleMinutes) || now - session.Created > TimeSpan.FromHours(options.Session.MaxHours);

    private List<EndedSession> SweepLocked(DateTimeOffset now)
    {
        List<PublicSession> expired = [.. _byHash.Values.Where(s => Expired(s, now))];
        foreach (PublicSession session in expired)
        {
            _byHash.Remove(session.HashKey);
            session.CloseSockets("expired");
        }

        return [.. expired.Select(s => new EndedSession(s, s.Ticket))];
    }

    private bool EvictIdlestGuestLocked(Func<PublicSession, bool> within)
    {
        PublicSession? idlest = null;
        foreach (PublicSession session in _byHash.Values)
        {
            if (session.Tier == PublicTier.Guest && within(session) && (idlest is null || session.LastUse < idlest.LastUse))
            {
                idlest = session;
            }
        }

        if (idlest is null)
        {
            return false;
        }

        _byHash.Remove(idlest.HashKey);
        idlest.CloseSockets("evicted");
        return true;
    }

    private IssuedSession AddLocked(PublicTier tier, string ipKey, string clientIp, DateTimeOffset now, string? account, int security, string? ticket)
    {
        string cookie = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        TryHash(cookie, out string hash);
        var session = new PublicSession(hash, tier, ipKey, clientIp, now, account, security, ticket);
        _byHash[hash] = session;
        return new IssuedSession(session, cookie);
    }

    private static bool TryHash(string? cookie, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrEmpty(cookie) || cookie.Length != 43 || !cookie.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(cookie))).ToLowerInvariant();
        return true;
    }
}
