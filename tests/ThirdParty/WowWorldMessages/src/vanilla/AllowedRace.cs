namespace WowWorldMessages.Vanilla;

[Flags]
public enum AllowedRace : uint {
    All = 0,
    Human = 1,
    Orc = 2,
    Dwarf = 4,
    NightElf = 8,
    Undead = 16,
    Tauren = 32,
    Gnome = 64,
    Troll = 128,
    Goblin = 256,
}

