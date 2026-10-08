namespace WowWorldMessages.Vanilla;

[Flags]
public enum FactionFlag : byte {
    Visible = 1,
    AtWar = 2,
    Hidden = 4,
    InvisibleForced = 8,
    PeaceForced = 16,
    Inactive = 32,
    Rival = 64,
}

