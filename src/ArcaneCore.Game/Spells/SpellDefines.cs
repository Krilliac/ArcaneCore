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
    /// <summary>vmangos SPELL_ATTR_ALLOW_WHILE_MOUNTED (SpellDefines.h:854).</summary>
    AllowWhileMounted = 0x01000000,
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

    /// <summary>vmangos SPELL_ATTR_EX_NO_AUTOCAST_AI (SpellDefines.h:887).</summary>
    NoAutocastAi = 0x00020000,

    /// <summary>vmangos SPELL_ATTR_EX_CANT_TARGET_SELF (AoE and chain selection skip the caster).</summary>
    CantTargetSelf = 0x00080000,
}

/// <summary>Spell.dbc AttributesEx2 bits (vmangos SpellDefines.h SpellAttributesEx2).</summary>
[Flags]
public enum SpellAttributesEx2 : uint
{
    None = 0,

    /// <summary>vmangos SPELL_ATTR_EX2_ALLOW_DEAD_TARGET (SpellDefines.h:906): can target a dead unit or corpse.</summary>
    AllowDeadTarget = 0x00000001,

    /// <summary>vmangos SPELL_ATTR_EX2_IGNORE_LINE_OF_SIGHT.</summary>
    IgnoreLineOfSight = 0x00000004,
    DoNotReportSpellFailure = 0x00000080,

    /// <summary>vmangos SPELL_ATTR_EX2_CANT_CRIT.</summary>
    CantCrit = 0x20000000,
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

    /// <summary>AURA_INTERRUPT_DISMOUNT_CANCELS (vmangos SpellDefines.h:583): removed when the unit dismounts.</summary>
    DismountCancels = 0x00000040,

    /// <summary>AURA_INTERRUPT_UNDER_WATER_CANCELS (vmangos SpellDefines.h:584): removed by entering water.</summary>
    UnderWaterCancels = 0x00000080,

    /// <summary>AURA_INTERRUPT_ABOVE_WATER_CANCELS (vmangos SpellDefines.h:585): removed by leaving water.</summary>
    AboveWaterCancels = 0x00000100,

    /// <summary>AURA_INTERRUPT_MOUNT_CANCELS (vmangos SpellDefines.h:594): removed when the unit mounts.</summary>
    MountCancels = 0x00020000,
    StandingCancels = 0x00040000,
    LeaveWorld = 0x00080000,
    NonPeriodicDamage = 0x01000000,
}

/// <summary>
/// Spell.dbc ChannelInterruptFlags bits that differ from the aura interrupt meaning
/// (vmangos SpellDefines.h SpellChannelInterruptFlags: CHANNEL_FLAG_DAMAGE 0x0002,
/// CHANNEL_FLAG_MOVEMENT 0x0008, CHANNEL_FLAG_TURNING 0x0010, CHANNEL_FLAG_DAMAGE2 0x0080,
/// CHANNEL_FLAG_DELAY 0x4000).
/// </summary>
public static class SpellChannelInterruptFlags
{
    public const uint Damage = 0x0002;
    public const uint Damage2 = 0x0080;
    public const uint Delay = 0x4000;
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
    EnumUnitsPartyWithinCasterRange = 20,
    UnitFriend = 21,
    LocationCasterSrc = 22,
    GameObject = 23,
    EnumUnitsEnemyInCone24 = 24,
    Unit = 25,

    /// <summary>TARGET_GAMEOBJECT_ITEM: the game object or item named in the cast's explicit target block (Pick Lock, vmangos / cmangos value 26).</summary>
    GameObjectItem = 26,
    EnumUnitsFriendAoeAtSrcLoc = 30,
    EnumUnitsFriendAoeAtDestLoc = 31,
    EnumUnitsPartyAoeAtSrcLoc = 33,
    EnumUnitsPartyAoeAtDestLoc = 34,
    UnitParty = 35,
    EnumUnitsEnemyWithinCasterRange = 36,
    UnitFriendAndParty = 37,
    UnitFriendChainHeal = 45,
    LocationCasterTargetPosition = 53,

    /// <summary>
    /// TARGET_LOCATION_CASTER_FISHING_SPOT (cmangos / vmangos value 39, Spell.cpp:2859): the fishing spells' only target,
    /// the caster carries the effect (the bobber position is the TRANS_DOOR effect's own business).
    /// </summary>
    LocationCasterFishingSpot = 39,
    EnumUnitsEnemyInCone54 = 54,
    EnumUnitsRaidWithinCasterRange = 56,

    /// <summary>A single raid member (vmangos TARGET_SINGLE_FRIEND_2 / cmangos TARGET_UNIT_RAID).</summary>
    UnitRaid = 57,
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

    /// <summary>Jump radius between chain targets (vmangos Spell.h CHAIN_SPELL_JUMP_RADIUS = 10 yd).</summary>
    public const float ChainJumpRadius = 10.0f;

    /// <summary>
    /// Total arc of a frontal cone target (TrinityCore SpellInfo default cone angle M_PI/2;
    /// vmangos PUSH_IN_FRONT uses its own arcs — recorded in docs/integration/spells-persistence.md).
    /// </summary>
    public const float ConeArc = MathF.PI / 2.0f;

    /// <summary>Eye height added to both ends of a line-of-sight query (vmangos WorldObject::IsWithinLOSInMap: + 2.0 yd).</summary>
    public const float LineOfSightHeight = 2.0f;

    /// <summary>Spell.dbc PreventionType SPELL_PREVENTION_TYPE_SILENCE (interruptible by SPELL_EFFECT_INTERRUPT_CAST).</summary>
    public const uint PreventionTypeSilence = 1;

    /// <summary>Spell.dbc PreventionType SPELL_PREVENTION_TYPE_PACIFY (vmangos SpellDefines.h:234-236): blocked while pacified.</summary>
    public const uint PreventionTypePacify = 2;
}
