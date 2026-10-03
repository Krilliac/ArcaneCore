namespace ArcaneCore.Game.Spells;

/// <summary>
/// SpellCastTargets mask bits (vmangos SharedDefines.h SpellCastTargetFlags; cmangos-classic
/// SpellDefines.h; gtker/wow_messages common.wowm SpellCastTargetFlags — all three agree for 1.12).
/// </summary>
[Flags]
public enum SpellCastTargetFlags : ushort
{
    Self = 0x0000,
    Unused1 = 0x0001,
    Unit = 0x0002,
    UnitRaid = 0x0004,
    UnitParty = 0x0008,
    Item = 0x0010,
    SourceLocation = 0x0020,
    DestLocation = 0x0040,
    UnitEnemy = 0x0080,
    UnitAlly = 0x0100,
    CorpseEnemy = 0x0200,
    UnitDead = 0x0400,
    GameObject = 0x0800,
    TradeItem = 0x1000,
    String = 0x2000,
    Locked = 0x4000,
    CorpseAlly = 0x8000,
}

/// <summary>
/// Spell schools (vmangos SharedDefines.h SpellSchools; Spell.dbc field 1 holds the school
/// index in 1.12, not a mask).
/// </summary>
public enum SpellSchool : byte
{
    Normal = 0,
    Holy = 1,
    Fire = 2,
    Nature = 3,
    Frost = 4,
    Shadow = 5,
    Arcane = 6,
}

/// <summary>Spell.dbc DmgClass (vmangos SpellDefines.h SpellDmgClass).</summary>
public enum SpellDamageClass : byte
{
    None = 0,
    Magic = 1,
    Melee = 2,
    Ranged = 3,
}

/// <summary>SMSG_SPELL_GO miss reasons for build 5875 (vmangos SpellDefines.h SpellMissInfo, &gt; 1.5.1 branch).</summary>
public enum SpellMissInfo : byte
{
    None = 0,
    Miss = 1,
    Resist = 2,
    Dodge = 3,
    Parry = 4,
    Block = 5,
    Evade = 6,
    Immune = 7,
    Immune2 = 8,
    Deflect = 9,
    Absorb = 10,
    Reflect = 11,
}

/// <summary>SMSG_SPELL_START/GO cast flags (vmangos Spell.h SpellCastFlags).</summary>
[Flags]
public enum SpellCastFlags : ushort
{
    None = 0x0000,
    HiddenCombatLog = 0x0001,
    Unknown2 = 0x0002,
    Ammo = 0x0020,
    Unknown9 = 0x0100,
}

/// <summary>Spell.dbc Attributes bits used by the spell system (vmangos SpellDefines.h SpellAttributes).</summary>
[Flags]
public enum SpellAttributes : uint
{
    None = 0,
    UsesRangedSlot = 0x00000002,
    IsAbility = 0x00000010,
    IsTradeskill = 0x00000020,
    Passive = 0x00000040,
    DoNotDisplay = 0x00000080,
    OnNextSwing = 0x00000400,
    AllowCastWhileDead = 0x00800000,
    CooldownOnEvent = 0x02000000,
    AuraIsDebuff = 0x04000000,
    AllowWhileSitting = 0x08000000,
    NoAuraCancel = 0x80000000,
}

/// <summary>Spell.dbc AttributesEx bits (vmangos SpellDefines.h SpellAttributesEx).</summary>
[Flags]
public enum SpellAttributesEx : uint
{
    None = 0,
    UseAllMana = 0x00000002,
    IsChanneled = 0x00000004,
    IsSelfChanneled = 0x00000040,
}

/// <summary>Spell.dbc AttributesEx2 bits (vmangos SpellDefines.h SpellAttributesEx2).</summary>
[Flags]
public enum SpellAttributesEx2 : uint
{
    None = 0,
    DoNotReportSpellFailure = 0x00000080,
}

/// <summary>Spell.dbc InterruptFlags (vmangos SpellDefines.h SpellInterruptFlags).</summary>
[Flags]
public enum SpellInterruptFlags : uint
{
    None = 0,
    Movement = 0x01,
    DamagePushback = 0x02,
    Stun = 0x04,
    Combat = 0x08,
    DamageCancels = 0x10,
}

/// <summary>Spell.dbc AuraInterruptFlags (cmangos-classic SpellDefines.h SpellAuraInterruptFlags).</summary>
[Flags]
public enum SpellAuraInterruptFlags : uint
{
    None = 0,
    HostileAction = 0x00000001,
    Damage = 0x00000002,
    Action = 0x00000004,
    Moving = 0x00000008,
    Turning = 0x00000010,
    StandingCancels = 0x00040000,
    LeaveWorld = 0x00080000,
    NonPeriodicDamage = 0x01000000,
}

/// <summary>Spell.dbc EffectImplicitTargetA/B values handled here (cmangos-classic SpellTargetDefines.h Targets).</summary>
public enum SpellImplicitTarget : uint
{
    None = 0,
    UnitCaster = 1,
    UnitEnemyNearCaster = 2,
    UnitFriendNearCaster = 3,
    UnitNearCaster = 4,
    UnitCasterPet = 5,
    UnitEnemy = 6,
    LocationCasterHomeBind = 9,
    EnumUnitsEnemyAoeAtSrcLoc = 15,
    EnumUnitsEnemyAoeAtDestLoc = 16,
    LocationDatabase = 17,
    LocationCasterDest = 18,
    UnitFriend = 21,
    LocationCasterSrc = 22,
    GameObject = 23,
    Unit = 25,
    UnitParty = 35,
}

/// <summary>SMSG_CAST_RESULT status byte (vmangos SpellDefines.h SpellCastResultStatus).</summary>
public enum SpellCastResultStatus : byte
{
    Success = 0,
    Failure = 2,
}

/// <summary>Rarely used Spell.dbc constants.</summary>
public static class SpellConstants
{
    /// <summary>Effects per spell (Spell.dbc MAX_EFFECT_INDEX).</summary>
    public const int MaxEffects = 3;

    /// <summary>StartRecoveryCategory of the shared global cooldown (vmangos Player::AddGCD: 133).</summary>
    public const uint GlobalCooldownCategory = 133;

    /// <summary>SpellRange.dbc index 1 "Self Only" (vmangos SpellDefines.h SPELL_RANGE_IDX_SELF_ONLY).</summary>
    public const uint RangeIndexSelfOnly = 1;

    /// <summary>SpellRange.dbc index 2 "Combat Range" (vmangos SPELL_RANGE_IDX_COMBAT).</summary>
    public const uint RangeIndexCombat = 2;

    /// <summary>Melee reach used for combat-range spells (vmangos ATTACK_DISTANCE = 5.0f).</summary>
    public const float AttackDistance = 5.0f;

    /// <summary>Default combat reach of a unit (vmangos DEFAULT_COMBAT_REACH = 1.5f).</summary>
    public const float DefaultCombatReach = 1.5f;

    /// <summary>
    /// Range leeway added to a player's spell range (vmangos Spell::CheckRange: 1.25 yd when
    /// casting starts ("strict"), 6.25 yd when the spell lands).
    /// </summary>
    public const float PlayerStrictRangeLeeway = 1.25f;

    public const float PlayerLandingRangeLeeway = 6.25f;

    /// <summary>Distance a caster may drift before a movement-interruptible cast is cancelled (vmangos Spell::update: 0.5 yd... see Spell).</summary>
    public const float MovementCancelThreshold = 0.5f;
}
