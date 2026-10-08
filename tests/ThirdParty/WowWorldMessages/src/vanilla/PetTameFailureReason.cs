namespace WowWorldMessages.Vanilla;

public enum PetTameFailureReason : byte {
    InvalidCreature = 1,
    TooMany = 2,
    CreatureAlreadyOwned = 3,
    NotTameable = 4,
    AnotherSummonActive = 5,
    UnitsCantTame = 6,
    NoPetAvailable = 7,
    InternalError = 8,
    TooHighLevel = 9,
    Dead = 10,
    NotDead = 11,
    UnknownError = 12,
}

