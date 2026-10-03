namespace ArcaneCore.Game.Loot;

/// <summary>
/// A group loot vote (vmangos Group.h RollVote, wow_messages loot_common.wowm RollVote for 1.12).
/// The client only ever sends 0..2 (MAX_ROLL_FROM_CLIENT = 3).
/// </summary>
public enum RollVote : byte
{
    Pass = 0,
    Need = 1,
    Greed = 2,
}

/// <summary>
/// The error code of the error form of SMSG_LOOT_RESPONSE (vmangos LootMgr.h LootError at 87-99;
/// wow_messages smsg_loot_response.wowm LootMethodError). The texts are the client's own.
/// </summary>
public enum LootError : byte
{
    /// <summary>You don't have permission to loot that corpse.</summary>
    DidntKill = 0,

    /// <summary>You are too far away to loot that corpse.</summary>
    TooFar = 4,

    /// <summary>You must be facing the corpse to loot it.</summary>
    BadFacing = 5,

    /// <summary>Someone is already looting that corpse.</summary>
    Locked = 6,

    /// <summary>You need to be standing up to loot something!</summary>
    NotStanding = 8,

    /// <summary>You can't loot anything while stunned!</summary>
    Stunned = 9,

    /// <summary>Player not found.</summary>
    PlayerNotFound = 10,

    /// <summary>Maximum play time exceeded.</summary>
    PlayTimeExceeded = 11,

    /// <summary>That player's inventory is full.</summary>
    MasterInventoryFull = 12,

    /// <summary>Player has too many of that item already.</summary>
    MasterUniqueItem = 13,

    /// <summary>Can't assign item to that player.</summary>
    MasterOther = 14,

    /// <summary>Your target has already had its pockets picked.</summary>
    AlreadyPickpocketed = 15,

    /// <summary>You can't do that while shapeshifted.</summary>
    NotWhileShapeshifted = 16,
}
