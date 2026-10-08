namespace WowWorldMessages.Vanilla;

public enum TransferAbortReason : byte {
    None = 0,
    IsFull = 1,
    NotFound = 2,
    TooManyInstances = 3,
    ZoneIsInCombat = 5,
}

