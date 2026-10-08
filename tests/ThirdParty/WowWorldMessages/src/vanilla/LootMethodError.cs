namespace WowWorldMessages.Vanilla;

public enum LootMethodError : byte {
    DidntKill = 0,
    TooFar = 4,
    BadFacing = 5,
    Locked = 6,
    Notstanding = 8,
    Stunned = 9,
    PlayerNotFound = 10,
    PlayTimeExceeded = 11,
    MasterInvFull = 12,
    MasterUniqueItem = 13,
    MasterOther = 14,
    AlreadyPickpocketed = 15,
    NotWhileShapeshifted = 16,
}

