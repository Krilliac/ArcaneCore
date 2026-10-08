namespace WowWorldMessages.Vanilla;

[Flags]
public enum ItemFlag : uint {
    None = 0,
    NoPickup = 1,
    Conjured = 2,
    Lootable = 4,
    Deprecated = 16,
    Indestructible = 32,
    PlayerCast = 64,
    NoEquipCooldown = 128,
    IntBonusInstead = 256,
    Wrapper = 512,
    IgnoreBagSpace = 1024,
    PartyLoot = 2048,
    Charter = 8192,
    HasText = 16384,
    NoDisenchant = 32768,
    RealDuration = 65536,
    NoCreator = 131072,
}

