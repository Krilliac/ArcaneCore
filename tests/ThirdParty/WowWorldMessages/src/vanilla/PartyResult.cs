namespace WowWorldMessages.Vanilla;

public enum PartyResult : byte {
    Success = 0,
    BadPlayerName = 1,
    TargetNotInGroup = 2,
    GroupFull = 3,
    AlreadyInGroup = 4,
    NotInGroup = 5,
    NotLeader = 6,
    PlayerWrongFaction = 7,
    IgnoringYou = 8,
}

