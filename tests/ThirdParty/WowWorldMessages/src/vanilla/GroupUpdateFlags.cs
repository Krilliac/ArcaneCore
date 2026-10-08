namespace WowWorldMessages.Vanilla;

[Flags]
public enum GroupUpdateFlags : uint {
    None = 0,
    Status = 1,
    CurHp = 2,
    MaxHp = 4,
    PowerType = 8,
    CurPower = 16,
    MaxPower = 32,
    Level = 64,
    Zone = 128,
    Position = 256,
    Auras = 512,
    Auras2 = 1024,
    PetGuid = 2048,
    PetName = 4096,
    PetModelId = 8192,
    PetCurHp = 16384,
    PetMaxHp = 32768,
    PetPowerType = 65536,
    PetCurPower = 131072,
    PetMaxPower = 262144,
    PetAuras = 524288,
    PetAuras2 = 1048576,
    ModeOffline = 268435456,
}

