namespace WowWorldMessages.Vanilla;

[Flags]
public enum AllowedClass : uint {
    All = 0,
    Warrior = 1,
    Paladin = 2,
    Hunter = 4,
    Rogue = 8,
    Priest = 16,
    Shaman = 64,
    Mage = 128,
    Warlock = 256,
    Druid = 1024,
}

