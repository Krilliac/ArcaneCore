using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Ops.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The web ticket service (<see cref="IFeedActorTickets"/>, docs/areas/atlas-public.md): the world keeps <c>ticket -> (account, issued)</c>; the
/// public Atlas listener holds only the opaque ticket. A ticket is 32 random bytes in base64url; the table is keyed by the SHA-256 of the ticket, so
/// a memory or log dump of the table does not yield a usable ticket. Every <see cref="ResolveAsync"/> re-reads the account (its security, status
/// column, ban rows) through the world's own stores, so a demotion or a ban applies on the next resolve (the action channel caches a valid answer
/// for 30 s). Atlas cannot raise a level: nothing it states about a ticket is believed, only the account store.
/// <para>
/// A ticket ends when it is logged out, kicked, revoked with its account, older than <see cref="AbsoluteLifetime"/>, or its account is gone, banned
/// or demoted below <see cref="MapFeedActionsOptions.WebLoginMinSecurity"/>. Tickets are bounded: <see cref="MaxPerAccount"/> per account and
/// <see cref="MaxTickets"/> in all (the oldest goes first). The service also appends the <c>login</c>, <c>logout</c>, <c>kick</c> and <c>revoke</c> events of the
/// durable audit when it issues, ends or revokes a ticket (<see cref="MapFeedWebAudit"/>). Thread-safe.
/// </para>
/// </summary>
internal sealed class MapFeedActorTicketStore : IFeedActorTickets
{
    /// <summary>The logger category of the login and ticket lines.</summary>
    public const string LogCategory = "ArcaneCore.Ops.MapFeed.Login";

    /// <summary>How long a ticket lives from its issue (the <c>expiresIn</c> a login answers).</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);

    /// <summary>Tickets one account may hold at once (the oldest is dropped for a new one).</summary>
    public const int MaxPerAccount = 8;

    /// <summary>Tickets held in all.</summary>
    public const int MaxTickets = 10_000;

    private sealed record Entry(int AccountId, string Account, DateTimeOffset Issued, string? ClientIp)
    {
        public long LastUseTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);
    private readonly Lock _issueGate = new();
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopes;
    private readonly MapFeedActionsOptions _options;
    private readonly MapFeedWebAudit _audit;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public MapFeedActorTicketStore(IServiceProvider services, IOptions<MapFeedActionsOptions> options, MapFeedWebAudit audit, ILoggerFactory loggers)
    {
        _services = services;
        _scopes = services.GetRequiredService<IServiceScopeFactory>();
        _options = options.Value;
        _audit = audit;
        _logger = loggers.CreateLogger(LogCategory);
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    /// <summary>Tickets held now.</summary>
    public int Count => _tickets.Count;

    /// <summary>The opaque ticket text for a stored key (the table key is a hash, so this is only for tests that issue and then look up).</summary>
    private static string KeyOf(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    /// <summary>
    /// Issue a ticket for <paramref name="account"/>, which the caller has just verified. Does not audit (the login handler audits the whole login).
    /// </summary>
    public string Issue(Account account, string? clientIp)
    {
        ArgumentNullException.ThrowIfNull(account);
        string ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        DateTimeOffset now = _time.GetUtcNow();
        lock (_issueGate)
        {
            string name = account.Username.ToUpperInvariant();
            KeyValuePair<string, Entry>[] mine = [.. _tickets.Where(p => p.Value.AccountId == account.Id).OrderBy(p => p.Value.Issued)];
            for (int i = 0; i <= mine.Length - MaxPerAccount; i++)
            {
                _tickets.TryRemove(mine[i].Key, out _);
            }

            while (_tickets.Count >= MaxTickets)
            {
                KeyValuePair<string, Entry> oldest = _tickets.MinBy(p => p.Value.Issued);
                if (oldest.Key is null || !_tickets.TryRemove(oldest.Key, out _))
                {
                    break;
                }
            }

            _tickets[KeyOf(ticket)] = new Entry(account.Id, name, now, clientIp) { LastUseTicks = now.UtcTicks };
        }

        return ticket;
    }

    /// <inheritdoc />
    public async ValueTask<FeedTicketLookup> ResolveAsync(string ticket, CancellationToken cancellationToken)
    {
        string key = KeyOf(ticket);
        if (!_tickets.TryGetValue(key, out Entry? entry))
        {
            return Lookup(FeedTicketStatus.Unknown);
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (now - entry.Issued > AbsoluteLifetime)
        {
            _tickets.TryRemove(key, out _);
            return Lookup(FeedTicketStatus.Expired);
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        IAccountStore? accounts = scope.ServiceProvider.GetService<IAccountStore>();
        Account? account = accounts is null ? null : await accounts.FindByUsernameAsync(entry.Account, cancellationToken).ConfigureAwait(false);
        if (account is null || account.Id != entry.AccountId)
        {
            _tickets.TryRemove(key, out _);
            return Lookup(FeedTicketStatus.Banned);
        }

        if (!await AccountPasswordCheck.IsStillActiveAsync(scope.ServiceProvider.GetService<IBanStore>(), account, _time, cancellationToken).ConfigureAwait(false))
        {
            _tickets.TryRemove(key, out _);
            _logger.LogInformation("web ticket of {Account} ended: the account is banned or no longer active", entry.Account);
            return Lookup(FeedTicketStatus.Banned);
        }

        if (account.Security < _options.WebLoginMinSecurity)
        {
            _tickets.TryRemove(key, out _);
            _logger.LogInformation("web ticket of {Account} ended: the account is now {Level}, below the web login floor {Floor}", entry.Account, account.Security, _options.WebLoginMinSecurity);
            return Lookup(FeedTicketStatus.Unknown);
        }

        Volatile.Write(ref entry.LastUseTicks, now.UtcTicks);
        return new FeedTicketLookup(FeedTicketStatus.Valid, account.Id, entry.Account, account.Security);
    }

    /// <summary>Whether <paramref name="ticket"/> is held (tests and the characters op: no account re-read).</summary>
    public bool TryGet(string ticket, out int accountId, out string account)
    {
        if (_tickets.TryGetValue(KeyOf(ticket), out Entry? entry) && _time.GetUtcNow() - entry.Issued <= AbsoluteLifetime)
        {
            accountId = entry.AccountId;
            account = entry.Account;
            return true;
        }

        accountId = 0;
        account = string.Empty;
        return false;
    }

    /// <summary>End one ticket (<paramref name="reason"/> is <c>logout</c> or <c>kick</c>, the audit event). False when it was not held.</summary>
    public async ValueTask<bool> EndAsync(string ticket, string reason, string? clientIp)
    {
        string key = KeyOf(ticket);
        if (!_tickets.TryRemove(key, out Entry? entry))
        {
            return false;
        }

        _services.GetService<MapFeedActions>()?.ForgetTickets(ticket, null);
        _logger.LogInformation("web ticket of {Account} ended: {Reason}", entry.Account, reason);
        await _audit.AppendAsync(new WebAuditRecord(reason, "atlas-web:" + entry.Account, entry.AccountId, ticket, clientIp ?? entry.ClientIp, null, 0, "ok", null)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Drop every ticket of <paramref name="account"/> (the Local-only revoke). Returns how many were dropped. The audit record names the Local
    /// <paramref name="actor"/>.
    /// </summary>
    public async ValueTask<int> RevokeAccountAsync(string account, string actor)
    {
        string name = account.Trim().ToUpperInvariant();
        int dropped = 0;
        int accountId = 0;
        foreach (KeyValuePair<string, Entry> pair in _tickets)
        {
            if (string.Equals(pair.Value.Account, name, StringComparison.Ordinal) && _tickets.TryRemove(pair.Key, out Entry? entry))
            {
                dropped++;
                accountId = entry.AccountId;
            }
        }

        _services.GetService<MapFeedActions>()?.ForgetTickets(null, name);
        _logger.LogInformation("web tickets of {Account} revoked by {Actor}: {Count} dropped", name, actor, dropped);
        await _audit.AppendAsync(new WebAuditRecord("revoke", actor, accountId == 0 ? null : accountId, null, null, name, dropped, "ok", null)).ConfigureAwait(false);
        return dropped;
    }

    private static FeedTicketLookup Lookup(FeedTicketStatus status) => new(status, 0, string.Empty, AccountSecurity.Player);
}
