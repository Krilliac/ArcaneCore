namespace WowWorldMessages.Vanilla;

public enum QuestFailedReason : uint {
    DontHaveReq = 0,
    QuestFailedLowLevel = 1,
    QuestFailedReqs = 2,
    QuestFailedInventoryFull = 4,
    QuestFailedWrongRace = 6,
    QuestOnlyOneTimed = 12,
    QuestAlreadyOn = 13,
    QuestFailedDuplicateItem = 17,
    QuestFailedMissingItems = 20,
    QuestFailedNotEnoughMoney = 22,
}

