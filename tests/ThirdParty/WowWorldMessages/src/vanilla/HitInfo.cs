namespace WowWorldMessages.Vanilla;

public enum HitInfo : uint {
    NormalSwing = 0,
    Unk1 = 1,
    AffectsVictim = 2,
    LeftSwing = 4,
    EarlyCriticalHit = 8,
    Miss = 16,
    Absorb = 32,
    Resist = 64,
    CriticalHit = 128,
    Unk9 = 256,
    Unk10 = 8192,
    Glancing = 16384,
    Crushing = 32768,
    NoAction = 65536,
    SwingNoHitSound = 524288,
}

