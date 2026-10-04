namespace ArcaneCore.Kernel.Gm;

/// <summary>
/// A chat mute of an account (vmangos <c>account.mutetime</c>, set by <c>.mute</c>; ArcaneCore keeps it in its own table
/// with who set it and why). Times are unix seconds. <see cref="MutedBySecurity"/> is the stored account security of the
/// staff member who muted, so an offline account can be unmuted without loading the target's own security.
/// </summary>
public sealed record AccountMuteRecord(int AccountId, long MutedUntil, long MutedAt, string MutedBy, byte MutedBySecurity, string Reason);

/// <summary>Size limits shared by the world and the storage columns.</summary>
public static class GmAuditLimits
{
    /// <summary>Longest ticket or response text stored, in characters.</summary>
    public const int MaxTextLength = 1024;
}

/// <summary>Lifecycle of a <see cref="GmTicketRecord"/>.</summary>
public enum GmTicketStatus : byte
{
    /// <summary>Waiting for, or being handled by, staff.</summary>
    Open = 0,

    /// <summary>Closed by staff (kept as history; players see no ticket).</summary>
    Closed = 1,
}

/// <summary>
/// A GM ticket (vmangos <c>character_ticket</c>, one open ticket per character). <see cref="Category"/> and the position are
/// what the client sent with <c>CMSG_GMTICKET_CREATE</c>; <see cref="Response"/> is the staff answer.
/// </summary>
public sealed record GmTicketRecord(
    int Id, int CharacterId, string Text, byte Category, uint MapId, float X, float Y, float Z,
    long CreatedAt, long UpdatedAt, GmTicketStatus Status, string Response, string ClosedBy, long ClosedAt);

/// <summary>
/// Persistence seam of the GM audit lane: account mutes and GM tickets (characters database,
/// docs/integration/gm-audit-lane.md). The world keeps the working set in memory and writes through
/// <c>GmAuditWriteQueue</c>; this store only loads at startup and applies single, absolute writes.
/// </summary>
public interface IGmAuditStore
{
    /// <summary>The mutes that are still in force at <paramref name="nowUnix"/>.</summary>
    Task<IReadOnlyList<AccountMuteRecord>> LoadActiveMutesAsync(long nowUnix, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the account's mute row.</summary>
    Task SaveMuteAsync(AccountMuteRecord mute, CancellationToken cancellationToken = default);

    /// <summary>Remove the account's mute row (no-op when there is none).</summary>
    Task DeleteMuteAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>Every ticket that is still open.</summary>
    Task<IReadOnlyList<GmTicketRecord>> LoadOpenTicketsAsync(CancellationToken cancellationToken = default);

    /// <summary>The highest ticket id ever stored (open or closed), 0 when there is none, so new ids never reuse one.</summary>
    Task<int> GetMaxTicketIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the ticket row. A ticket whose character no longer exists is ignored (deleted while queued).</summary>
    Task SaveTicketAsync(GmTicketRecord ticket, CancellationToken cancellationToken = default);

    /// <summary>Remove the ticket row (no-op when there is none).</summary>
    Task DeleteTicketAsync(int ticketId, CancellationToken cancellationToken = default);
}
