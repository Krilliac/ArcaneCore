namespace WowWorldMessages.Vanilla;

[Flags]
public enum GroupMemberOnlineStatus : byte {
    Offline = 0,
    Online = 1,
    Pvp = 2,
    Dead = 4,
    Ghost = 8,
    PvpFfa = 16,
    ZoneOut = 32,
    Afk = 64,
    Dnd = 128,
}

