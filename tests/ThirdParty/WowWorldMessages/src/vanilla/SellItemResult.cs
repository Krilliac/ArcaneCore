namespace WowWorldMessages.Vanilla;

public enum SellItemResult : byte {
    CantFindItem = 1,
    CantSellItem = 2,
    CantFindVendor = 3,
    YouDontOwnThatItem = 4,
    Unk = 5,
    OnlyEmptyBag = 6,
}

