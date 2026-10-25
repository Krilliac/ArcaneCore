using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>What a web ticket resolves to right now.</summary>
internal enum FeedTicketStatus : byte
{
    /// <summary>The ticket belongs to a logged-in account that may act.</summary>
    Valid,

    /// <summary>No such ticket (never issued, revoked, or logged out).</summary>
    Unknown,

    /// <summary>The ticket's session has ended (idle or absolute limit).</summary>
    Expired,

    /// <summary>The ticket's account is banned or no longer active.</summary>
    Banned,
}

/// <summary>A ticket's current standing: the account and its security as the world's account store has them now.</summary>
/// <param name="Status">Whether the ticket may act.</param>
/// <param name="AccountId">The account id (meaningful when <paramref name="Status"/> is <see cref="FeedTicketStatus.Valid"/>).</param>
/// <param name="Account">The account name (upper case), the actor name's tail: <c>atlas-web:&lt;ACCOUNT&gt;</c>.</param>
/// <param name="Security">The account's own security level, before the public ceiling is applied.</param>
internal sealed record FeedTicketLookup(FeedTicketStatus Status, int AccountId, string Account, AccountSecurity Security);

/// <summary>
/// The web ticket service the login lane implements (docs/areas/atlas-actions.md): maps the opaque ticket a web actor presents to its account.
/// The action channel calls it at most once per ticket per 30 seconds and caches a valid answer for that long, so a demotion or a ban
/// applies within 30 seconds. Implementations hold the tickets (never the channel); any thread.
/// <para>
/// The implementation also owns the audit of the ticket's own life: it appends the <c>login</c> (successful and failed), <c>logout</c>, <c>kick</c> and
/// <c>revoke</c> events of docs/areas/atlas-actions.md, "Audit", through <see cref="MapFeedWebAudit.AppendAsync"/> when it issues, ends or revokes a ticket.
/// The action channel sees only the tickets presented to it, so it writes the <c>action</c> and <c>refusals</c> events and nothing else.
/// </para>
/// </summary>
internal interface IFeedActorTickets
{
    /// <summary>The standing of <paramref name="ticket"/> now. Never throws for an unknown ticket (that is <see cref="FeedTicketStatus.Unknown"/>).</summary>
    ValueTask<FeedTicketLookup> ResolveAsync(string ticket, CancellationToken cancellationToken);
}
