namespace WowWorldMessages.Vanilla;

public enum BuyResult : byte {
    CantFindItem = 0,
    ItemAlreadySold = 1,
    NotEnoughMoney = 2,
    SellerDontLikeYou = 4,
    DistanceTooFar = 5,
    ItemSoldOut = 7,
    CantCarryMore = 8,
    RankRequire = 11,
    ReputationRequire = 12,
}

