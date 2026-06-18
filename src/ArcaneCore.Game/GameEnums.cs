namespace ArcaneCore.Game;

/// <summary>Playable races (ChrRaces ids, build 5875).</summary>
public enum Race : byte
{
    Human = 1,
    Orc = 2,
    Dwarf = 3,
    NightElf = 4,
    Undead = 5,
    Tauren = 6,
    Gnome = 7,
    Troll = 8,
}

/// <summary>Playable classes (ChrClasses ids, build 5875).</summary>
public enum Class : byte
{
    Warrior = 1,
    Paladin = 2,
    Hunter = 3,
    Rogue = 4,
    Priest = 5,
    Shaman = 7,
    Mage = 8,
    Warlock = 9,
    Druid = 11,
}

public enum Gender : byte
{
    Male = 0,
    Female = 1,
}

/// <summary>UNIT_FIELD_BYTES_0 power-type byte values.</summary>
public enum PowerType : byte
{
    Mana = 0,
    Rage = 1,
    Focus = 2,
    Energy = 3,
}

/// <summary>Object type ids (the OBJECT_FIELD_TYPE / typeId values; vmangos ObjectGuid.h).</summary>
public static class TypeId
{
    public const byte Object = 0;
    public const byte Player = 4;
}

/// <summary>Object type mask bits (OBJECT_FIELD_TYPE value; vmangos ObjectGuid.h).</summary>
public static class TypeMask
{
    public const uint Object = 0x0001;
    public const uint Unit = 0x0008;
    public const uint Player = 0x0010;

    /// <summary>The OBJECT_FIELD_TYPE value a player advertises (= 0x19).</summary>
    public const uint PlayerObject = Object | Unit | Player;
}
