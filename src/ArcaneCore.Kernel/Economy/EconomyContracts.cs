using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Kernel.Economy;

/// <summary>Who sent a letter (vmangos MailMessageType; wow_messages 1.12 MailType).</summary>
public enum MailMessageType : byte
{
    Normal = 0,
    Auction = 2,
    Creature = 3,
    GameObject = 4,
    Item = 5,
}

/// <summary>Letter state bits (vmangos MailCheckMask).</summary>
[Flags]
public enum MailCheckMask : uint
{
    None = 0x00,
    Read = 0x01,
    Returned = 0x02,
    Copied = 0x04,
    CodPayment = 0x08,
    HasBody = 0x10,
}

/// <summary>
/// One letter (vmangos <c>mail</c> + <c>mail_items</c>; 1.12 letters carry at most one item).
/// <see cref="SenderId"/> is a character id for <see cref="MailMessageType.Normal"/>, the
/// auction house id for <see cref="MailMessageType.Auction"/>, else a creature/object entry.
/// An attached item stays in <c>item_instance</c> with owner 0 (escrow) until taken.
/// Times are Unix seconds.
/// </summary>
public sealed record MailRecord
{
    public uint Id { get; init; }
    public MailMessageType MessageType { get; init; }
    public uint Stationery { get; init; } = MailStationery.Default;
    public uint SenderId { get; init; }
    public int ReceiverId { get; init; }
    public string Subject { get; init; } = string.Empty;
    public uint ItemTextId { get; init; }
    public uint ItemGuid { get; init; }
    public uint ItemEntry { get; init; }
    public uint Money { get; init; }
    public uint Cod { get; init; }
    public MailCheckMask Checked { get; init; }
    public long DeliverTime { get; init; }
    public long ExpireTime { get; init; }

    public bool HasItem => ItemGuid != 0;
}

/// <summary>Stationery.dbc ids used by the server (vmangos MailStationery).</summary>
public static class MailStationery
{
    public const uint Test = 1;
    public const uint Default = 41;
    public const uint Gm = 61;
    public const uint Auction = 62;
}

/// <summary>
/// One auction (vmangos <c>auction</c>). The item stays in <c>item_instance</c> with owner 0
/// (escrow) while listed. <see cref="BidderId"/> 0 means no bid. Times are Unix seconds.
/// </summary>
public sealed record AuctionRecord
{
    public uint Id { get; init; }
    public uint HouseId { get; init; }
    public uint ItemGuid { get; init; }
    public uint ItemEntry { get; init; }
    public uint ItemCount { get; init; }
    public int SellerId { get; init; }
    public uint StartBid { get; init; }
    public uint Buyout { get; init; }
    public long ExpireTime { get; init; }
    public int BidderId { get; init; }
    public uint Bid { get; init; }
    public uint Deposit { get; init; }
}

/// <summary>A row change applied with its precondition inside one economy transaction.</summary>
public abstract record EconomyChange;

/// <summary>
/// The item leaves <paramref name="CharacterId"/>'s inventory into escrow (owner 0). The
/// participant's After inventory must not contain it; the durable row must belong to the character.
/// </summary>
public sealed record EscrowFromInventory(int CharacterId, ItemInstanceData Item) : EconomyChange;

/// <summary>An escrowed item enters <paramref name="CharacterId"/>'s inventory (the participant's After contains it).</summary>
public sealed record ReleaseFromEscrow(int CharacterId, uint ItemGuid) : EconomyChange;

/// <summary>An escrowed item is destroyed (an auction of a deleted owner, an expired letter with no living sender).</summary>
public sealed record DeleteEscrowItem(uint ItemGuid) : EconomyChange;

/// <summary>A brand-new item created straight into escrow (owner 0) to be attached to a letter of the same operation (server mail).</summary>
public sealed record CreateEscrowItem(ItemInstanceData Item) : EconomyChange;

/// <summary>
/// A brand-new item created straight into escrow (owner 0) for a listing that no character owned: the auction house
/// bot's stock. The GUID must not exist yet, and the same request must reference it exactly once (its
/// <see cref="InsertAuction"/>), so a retried or duplicated listing is refused instead of minting a second copy.
/// </summary>
public sealed record MintEscrowItem(ItemInstanceData Item) : EconomyChange;

/// <summary>
/// Server mail template <paramref name="TemplateId"/> was sent to <paramref name="CharacterId"/> (AzerothCore mail_server_character),
/// written in the same transaction as the template's letters. The store refuses the whole operation when the record already exists,
/// so a template's reward commits at most once per character: a retry after a crash, a lost acknowledgement or a second login is
/// refused instead of sending a second copy. The request must also insert at least one letter to that character.
/// </summary>
public sealed record RecordServerMailSent(int CharacterId, uint TemplateId) : EconomyChange;

/// <summary>A new letter; <paramref name="Body"/> creates <c>item_text</c> row <see cref="MailRecord.ItemTextId"/>.</summary>
public sealed record InsertMail(MailRecord Mail, string? Body, int RecipientCap = 0) : EconomyChange;

/// <summary>Replace a letter that must still equal <paramref name="Expected"/>.</summary>
public sealed record UpdateMail(MailRecord Expected, MailRecord Updated) : EconomyChange;

/// <summary>Delete a letter that must still equal <paramref name="Expected"/> (its text goes too unless copied into a letter item).</summary>
public sealed record DeleteMail(MailRecord Expected) : EconomyChange;

public sealed record InsertAuction(AuctionRecord Auction) : EconomyChange;

public sealed record UpdateAuction(AuctionRecord Expected, AuctionRecord Updated) : EconomyChange;

public sealed record DeleteAuction(AuctionRecord Expected) : EconomyChange;

/// <summary>
/// One online character whose money/inventory the operation changes. Before is the snapshot
/// the caller saved through the ordered queue immediately before the commit; After is the
/// complete state to publish. Both carry complete inventories.
/// </summary>
public sealed record EconomyParticipant(CharacterState Before, CharacterState After,
    IReadOnlyList<uint>? ConsumedItemGuids = null);

/// <summary>
/// One atomic economy operation. <see cref="OperationId"/> is recorded in the same transaction
/// (<c>economy_operation</c>) and is the idempotency key: a retry observes AlreadyCommitted
/// and applies nothing. Participants may be empty (system mail, expiry, offline returns).
/// </summary>
public sealed record EconomyCommitRequest(
    Guid OperationId,
    IReadOnlyList<EconomyParticipant> Participants,
    IReadOnlyList<EconomyChange> Changes);

public enum EconomyCommitResult
{
    Committed,
    AlreadyCommitted,

    /// <summary>A participant's durable money/inventory or a row precondition no longer matches; nothing was written.</summary>
    Conflict,

    CharacterMissing,
}

/// <summary>The highest ids in use, to seed in-memory allocators at startup.</summary>
public readonly record struct EconomyIdSeed(uint MaxMailId, uint MaxAuctionId, uint MaxItemTextId);

/// <summary>Which auctions an <see cref="AuctionSnapshot"/> covers: all, one by ID, or one seller's. Both set means both must match.</summary>
public readonly record struct AuctionSnapshotFilter(uint? AuctionId = null, int? SellerId = null);

/// <summary>
/// Auction rows and their owner-0 escrow items read in one transaction. An auction whose item is
/// not present in <see cref="Escrow"/> (or differs from the row) had no matching escrow at the
/// moment of the read; callers decide what that means.
/// </summary>
public sealed record AuctionSnapshot(IReadOnlyList<AuctionRecord> Auctions, IReadOnlyDictionary<uint, ItemInstanceData> Escrow);

/// <summary>
/// Persistence of mail, item text and auctions (characters schema, economy module). Commits are
/// serializable transactions in a dedicated context; failures propagate after rollback.
/// </summary>
public interface IEconomyStore
{
    Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether the operation's ledger row exists (resolves a lost commit acknowledgement).</summary>
    Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default);

    /// <summary>Letters whose expiry is at or before <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default);

    /// <summary>Letters sent by (Normal type) or to a character (character deletion).</summary>
    Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default);

    Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Escrowed (owner 0) items by GUID; missing GUIDs are absent from the result.</summary>
    Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default);

    /// <summary>
    /// The matching auction rows and their escrow items from one consistent snapshot, so a writer
    /// that releases an escrow item and deletes its auction cannot be observed half-applied.
    /// </summary>
    Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default);

    Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What a worker asks of the Economy lease holder (M15 S3d, <c>docs/CLUSTERING_DESIGN.md</c> section 8 mail and auctions). The holder owns the only auction cache,
/// the expiry scheduler and the fenced store writes; a worker that does not hold the lease reads through <see cref="MailList"/> and <see cref="AuctionList"/> and
/// sends every mailbox or auction write (send, return, take, COD payment, bid, buyout, cancel) as an <see cref="EconomyCommitRequest"/> through
/// <see cref="MailWrite"/> or <see cref="AuctionWrite"/>.
/// </summary>
public enum EconomyForwardKind : byte
{
    /// <summary>The letters of <see cref="EconomyForwardRequest.CharacterId"/> (all states; the worker filters by delivery and expiry time).</summary>
    MailList = 1,

    /// <summary>The live auctions of <see cref="EconomyForwardRequest.HouseId"/>, or only <see cref="EconomyForwardRequest.AuctionId"/> when it is not 0.</summary>
    AuctionList = 2,

    /// <summary>A commit whose changes touch letters (and escrow items) only.</summary>
    MailWrite = 3,

    /// <summary>A commit with at least one auction change (a listing, bid, buyout, cancel or settlement), possibly with letters for the parties.</summary>
    AuctionWrite = 4,
}

/// <summary>A letter with the escrowed item it carries.</summary>
public sealed record ForwardedMail(MailRecord Mail, ItemInstanceData? Item);

/// <summary>A listed auction with its escrowed item.</summary>
public sealed record ForwardedAuction(AuctionRecord Auction, ItemInstanceData Item);

/// <summary>
/// One request from a worker to the holder. <see cref="RequestId"/> correlates the answer; <see cref="WorkerId"/> names the asking worker (diagnostics).
/// For the write kinds <see cref="Commit"/> is required and its <see cref="EconomyCommitRequest.OperationId"/> is the idempotency key: the worker saves its
/// participants first (as a local economy operation does), then a retry of the same request after a lost answer is never a second write.
/// </summary>
public sealed record EconomyForwardRequest(
    Guid RequestId,
    string WorkerId,
    EconomyForwardKind Kind,
    int CharacterId = 0,
    uint HouseId = 0,
    uint AuctionId = 0,
    EconomyCommitRequest? Commit = null);

/// <summary>How the holder answered an <see cref="EconomyForwardRequest"/>.</summary>
public enum EconomyForwardStatus : byte
{
    /// <summary>Served; for a write, durably committed (or already committed under the same operation id).</summary>
    Ok = 0,

    /// <summary>This node does not hold the Economy lease: ask the node that does.</summary>
    NotHolder = 1,

    /// <summary>The economy store is off or the node is stopping.</summary>
    Disabled = 2,

    /// <summary>The request is malformed (wrong kind for the changes, missing commit, an insert without its escrow item).</summary>
    Invalid = 3,

    /// <summary>The holder's cache or the stored rows no longer match what the write expected; nothing was written. The worker re-reads and may retry.</summary>
    Conflict = 4,

    /// <summary>Another operation holds one of the auctions or letters; nothing was written.</summary>
    Busy = 5,

    /// <summary>Nothing was written (the store failed and the operation is known not to have committed).</summary>
    Failed = 6,

    /// <summary>The outcome could not be read back; retry the same request (same operation id) to learn it.</summary>
    Unknown = 7,
}

/// <summary>The holder's answer. <see cref="Mails"/> and <see cref="Auctions"/> are filled only for the matching read kind.</summary>
public sealed record EconomyForwardResult(
    Guid RequestId,
    EconomyForwardStatus Status,
    IReadOnlyList<ForwardedMail> Mails,
    IReadOnlyList<ForwardedAuction> Auctions)
{
    public bool IsOk => Status == EconomyForwardStatus.Ok;

    /// <summary>An answer with no rows.</summary>
    public static EconomyForwardResult Of(Guid requestId, EconomyForwardStatus status) => new(requestId, status, [], []);
}
