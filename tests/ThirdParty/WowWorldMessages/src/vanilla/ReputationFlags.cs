namespace WowWorldMessages.Vanilla;

[Flags]
public enum ReputationFlags : byte {
    VisibleToClient = 1,
    EnableAtWar = 2,
    HideInClient = 4,
    ForceHideInClient = 8,
    ForceAtPeace = 16,
    FactionInactive = 32,
}

