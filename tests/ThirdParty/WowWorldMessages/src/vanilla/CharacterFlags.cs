namespace WowWorldMessages.Vanilla;

[Flags]
public enum CharacterFlags : uint {
    None = 0,
    LockedForTransfer = 4,
    HideHelm = 1024,
    HideCloak = 2048,
    Ghost = 8192,
    Rename = 16384,
}

