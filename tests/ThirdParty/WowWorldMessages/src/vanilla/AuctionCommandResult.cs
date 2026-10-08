namespace WowWorldMessages.Vanilla;

public enum AuctionCommandResult : uint {
    Ok = 0,
    ErrInventory = 1,
    ErrDatabase = 2,
    ErrNotEnoughMoney = 3,
    ErrItemNotFound = 4,
    ErrHigherBid = 5,
    ErrBidIncrement = 7,
    ErrBidOwn = 10,
    ErrRestrictedAccount = 13,
}

