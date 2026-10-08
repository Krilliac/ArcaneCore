namespace WowWorldMessages.Vanilla;

[Flags]
public enum UpdateFlag : byte {
    None = 0,
    Self = 1,
    Transport = 2,
    MeleeAttacking = 4,
    HighGuid = 8,
    All = 16,
    Living = 32,
    HasPosition = 64,
}

