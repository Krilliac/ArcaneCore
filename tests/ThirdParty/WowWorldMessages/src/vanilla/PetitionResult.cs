namespace WowWorldMessages.Vanilla;

public enum PetitionResult : uint {
    Ok = 0,
    AlreadySigned = 1,
    AlreadyInGuild = 2,
    CantSignOwn = 3,
    NeedMore = 4,
    NotServer = 5,
}

