namespace ArcaneCore.Kernel.Economy;

/// <summary>A persisted auction house bot custody row (characters <c>ahbot_custody</c>). Times are Unix seconds.</summary>
/// <param name="IdemKey">The row's idempotency key; its economy operation id is derived from it.</param>
/// <param name="Kind">0 gold, 1 item (MaNGOS Zero CustodyKind).</param>
/// <param name="Role">0 listing, 1 buyout, 2 bid.</param>
/// <param name="State">0 reserved, 1 terminal ok, 2 terminal back (MaNGOS Zero CustodyState).</param>
public sealed record AuctionBotCustodyRecord(
    string IdemKey,
    byte Kind,
    byte Role,
    byte State,
    uint HouseId,
    uint AuctionId,
    uint ItemGuid,
    uint ItemEntry,
    uint ItemCount,
    uint Amount,
    long CreatedTime,
    long ResolvedTime);

/// <summary>The durable custody ledger of the auction house bot (MaNGOS Zero CustodyStore over <c>ahbot_custody</c>).</summary>
public interface IAuctionBotCustodyStore
{
    Task<IReadOnlyList<AuctionBotCustodyRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or replace <paramref name="rows"/> and delete <paramref name="deletedKeys"/>, in one transaction.</summary>
    Task SaveAsync(IReadOnlyCollection<AuctionBotCustodyRecord> rows, IReadOnlyCollection<string> deletedKeys, CancellationToken cancellationToken = default);
}

/// <summary>
/// One <c>ahbot_items</c> row (cMaNGOS AuctionHouseBot ItemData / ahbot_items): <paramref name="Value"/> 0 never lists or buys the item, any
/// other value is its buyout per item; <paramref name="AddChance"/> is the percent chance per sell pass to list it whatever the filters,
/// in a stack between <paramref name="MinAmount"/> and <paramref name="MaxAmount"/>.
/// </summary>
public sealed record AuctionBotItemOverride(uint Item, uint Value, uint AddChance, uint MinAmount, uint MaxAmount);

/// <summary>The world <c>ahbot_items</c> table (cMaNGOS AuctionHouseBot::SetItemData writes it from <c>.ahbot items</c>).</summary>
public interface IAuctionBotItemStore
{
    Task<IReadOnlyList<AuctionBotItemOverride>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the row of <c>value.Item</c>.</summary>
    Task SaveAsync(AuctionBotItemOverride value, CancellationToken cancellationToken = default);

    /// <summary>Delete the row of <paramref name="item"/>; false when there was none.</summary>
    Task<bool> DeleteAsync(uint item, CancellationToken cancellationToken = default);
}
