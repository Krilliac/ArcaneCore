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
    Happiness = 4,
}

/// <summary>Object type ids (vmangos ObjectGuid.h TypeID).</summary>
public static class TypeId
{
    public const byte Object = 0;
    public const byte Item = 1;
    public const byte Container = 2;
    public const byte Unit = 3;
    public const byte Player = 4;
    public const byte GameObject = 5;
    public const byte DynamicObject = 6;
    public const byte Corpse = 7;
}

/// <summary>Object type mask bits — the OBJECT_FIELD_TYPE value (vmangos ObjectGuid.h TypeMask).</summary>
public static class TypeMask
{
    public const uint Object = 0x0001;
    public const uint Item = 0x0002;
    public const uint Container = 0x0004;
    public const uint Unit = 0x0008;
    public const uint Player = 0x0010;
    public const uint GameObject = 0x0020;
    public const uint DynamicObject = 0x0040;
    public const uint Corpse = 0x0080;

    /// <summary>The OBJECT_FIELD_TYPE value a player advertises (= 0x19).</summary>
    public const uint PlayerObject = Object | Unit | Player;
}

/// <summary>Update block types in SMSG_UPDATE_OBJECT for builds &gt; 1.8.4 (vmangos UpdateData.h).</summary>
public enum ObjectUpdateType : byte
{
    Values = 0,
    Movement = 1,
    CreateObject = 2,
    CreateObject2 = 3,
    OutOfRangeObjects = 4,
    NearObjects = 5,
}

/// <summary>Create-block update flags for builds &gt; 1.8.4 ("checked for 1.12.1", vmangos UpdateData.h).</summary>
[Flags]
public enum ObjectUpdateFlags : byte
{
    None = 0x00,
    Self = 0x01,
    Transport = 0x02,
    MeleeAttacking = 0x04,
    HighGuid = 0x08,
    All = 0x10,
    Living = 0x20,
    HasPosition = 0x40,
}

/// <summary>UNIT_FIELD_FLAGS bits (vmangos UnitDefines.h UnitFlags).</summary>
[Flags]
public enum UnitFlags : uint
{
    None = 0x00000000,
    ServerControlled = 0x00000001,
    Spawning = 0x00000002,
    RemoveClientControl = 0x00000004,
    PlayerControlled = 0x00000008,
    PetRename = 0x00000010,
    PetAbandon = 0x00000020,
    PlusMob = 0x00000040,
    NotAttackable1 = 0x00000080,
    ImmuneToPlayer = 0x00000100,
    ImmuneToNpc = 0x00000200,
    Looting = 0x00000400,
    PetInCombat = 0x00000800,
    Pvp = 0x00001000,
    Silenced = 0x00002000,
    UseSwimAnimation = 0x00008000,
    NonAttackable2 = 0x00010000,
    Pacified = 0x00020000,
    Stunned = 0x00040000,
    InCombat = 0x00080000,
    TaxiFlight = 0x00100000,
    Disarmed = 0x00200000,
    Confused = 0x00400000,
    Fleeing = 0x00800000,
    Possessed = 0x01000000,
    NotSelectable = 0x02000000,
    Skinnable = 0x04000000,
    AurasVisible = 0x08000000,
    PreventAnim = 0x20000000,
    Sheathe = 0x40000000,
    Immune = 0x80000000,
}

/// <summary>UNIT_FIELD_BYTES_1 byte 0 stand states (vmangos UnitDefines.h UnitStandStateType).</summary>
public enum StandState : byte
{
    Stand = 0,
    Sit = 1,
    SitChair = 2,
    Sleep = 3,
    SitLowChair = 4,
    SitMediumChair = 5,
    SitHighChair = 6,
    Dead = 7,
    Kneel = 8,
    Custom = 9,
}
