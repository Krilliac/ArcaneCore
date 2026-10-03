namespace ArcaneCore.Game.Spells;

// Combat-mechanics enums and attribute bits (hand-written, not generated). Every value is verbatim from
// vmangos at the SUPPORTED_CLIENT_BUILD = 1.12.1 branch (src/shared/Progression.h:36); the file:line of
// each block is on its summary. The legacy bit enums in SpellDefines.cs are intentionally untouched: the
// combat bits live in the *Combat enums below so the two sets can never disagree about a member name.

/// <summary>Shapeshift forms stored in UNIT_FIELD_BYTES_1 byte 2 (vmangos SharedDefines.h:1421-1441, SpellShapeshiftForm.dbc checked for 1.12.1).</summary>
public enum ShapeshiftForm : byte
{
    None = 0x00,
    Cat = 0x01,
    Tree = 0x02,
    Travel = 0x03,
    Aqua = 0x04,
    Bear = 0x05,
    Ambient = 0x06,
    Ghoul = 0x07,
    DireBear = 0x08,
    CreatureBear = 0x0E,
    CreatureCat = 0x0F,
    GhostWolf = 0x10,
    BattleStance = 0x11,
    DefensiveStance = 0x12,
    BerserkerStance = 0x13,
    Shadow = 0x1C,
    Stealth = 0x1E,
    Moonkin = 0x1F,
    SpiritOfRedemption = 0x20,
}

/// <summary>SpellShapeshiftForm.dbc flags1 (vmangos SharedDefines.h:1467-1476).</summary>
[Flags]
public enum ShapeshiftFlags : uint
{
    None = 0,

    /// <summary>Form allows normal player activity; a "stance" does not make spells act as shapeshifted (SpellEntry.cpp:1058).</summary>
    Stance = 0x00000001,
    NotToggleable = 0x00000002,
    PersistOnDeath = 0x00000004,
    CanInteractNpc = 0x00000008,
    DontUseWeapon = 0x00000010,
    AgilityAttackBonus = 0x00000020,
    CanUseEquippedItems = 0x00000040,
}

/// <summary>
/// Spell aura states, the Spell.dbc CasterAuraState / TargetAuraState values (vmangos SpellDefines.h:642-656).
/// 9-11 are vmangos "custom" states that are not based on spell data.
/// </summary>
public enum AuraState : uint
{
    None = 0,
    Defense = 1,
    Healthless20Percent = 2,
    Berserking = 3,
    Frozen = 4,
    Judgement = 5,
    HunterParry = 7,
    RogueAttackFromStealth = 8,
    Healthless15Percent = 9,
    Healthless10Percent = 10,
    Healthless5Percent = 11,
}

/// <summary>Spell.dbc procFlags (vmangos SpellDefines.h:1043-1081).</summary>
[Flags]
public enum ProcFlags : uint
{
    None = 0x00000000,
    Heartbeat = 0x00000001,
    Kill = 0x00000002,
    DealMeleeSwing = 0x00000004,
    TakeMeleeSwing = 0x00000008,
    DealMeleeAbility = 0x00000010,
    TakeMeleeAbility = 0x00000020,
    DealRangedAttack = 0x00000040,
    TakeRangedAttack = 0x00000080,
    DealRangedAbility = 0x00000100,
    TakeRangedAbility = 0x00000200,
    DealHelpfulAbility = 0x00000400,
    TakeHelpfulAbility = 0x00000800,
    DealHarmfulAbility = 0x00001000,
    TakeHarmfulAbility = 0x00002000,
    DealHelpfulSpell = 0x00004000,
    TakeHelpfulSpell = 0x00008000,
    DealHarmfulSpell = 0x00010000,
    TakeHarmfulSpell = 0x00020000,
    DealHarmfulPeriodic = 0x00040000,
    TakeHarmfulPeriodic = 0x00080000,
    TakenAnyDamage = 0x00100000,
    OnTrapActivation = 0x00200000,
    MainHandWeaponSwing = 0x00400000,
    OffHandWeaponSwing = 0x00800000,
}

/// <summary>spell_proc_event.procEx (vmangos SpellDefines.h:1099-1120).</summary>
[Flags]
public enum ProcFlagsEx : uint
{
    None = 0x0000000,
    NormalHit = 0x0000001,
    CriticalHit = 0x0000002,
    Miss = 0x0000004,
    Resist = 0x0000008,
    Dodge = 0x0000010,
    Parry = 0x0000020,
    Block = 0x0000040,
    Evade = 0x0000080,
    Immune = 0x0000100,
    Deflect = 0x0000200,
    Absorb = 0x0000400,
    Reflect = 0x0000800,
    Interrupt = 0x0001000,
    TriggerAlways = 0x0010000,
    NoPeriodic = 0x0020000,
    PeriodicPositive = 0x0040000,
    CastEnd = 0x0080000,
}

/// <summary>Spell modifier operations, the EffectMiscValue of aura 107/108 (vmangos SpellDefines.h:602-631; value 13 is unused).</summary>
public enum SpellModOp
{
    Damage = 0,
    Duration = 1,
    Threat = 2,
    AttackPower = 3,
    Charges = 4,
    Range = 5,
    Radius = 6,
    CriticalChance = 7,
    AllEffects = 8,
    NotLoseCastingTime = 9,
    CastingTime = 10,
    Cooldown = 11,
    Speed = 12,
    Cost = 14,
    CritDamageBonus = 15,
    ResistMissChance = 16,
    JumpTargets = 17,
    ChanceOfSuccess = 18,
    ActivationTime = 19,
    EffectPastFirst = 20,
    GlobalCooldown = 21,
    Dot = 22,
    Haste = 23,
    SpellBonusDamage = 24,
    MultipleValue = 27,
    ResistDispelChance = 28,
    Max = 29,
}

/// <summary>Combat-relevant Spell.dbc Attributes bits not in <see cref="SpellAttributes"/> (vmangos SpellDefines.h:830-863).</summary>
[Flags]
public enum SpellAttributesCombat : uint
{
    None = 0,

    /// <summary>SPELL_ATTR_ON_NEXT_SWING_NO_DAMAGE (bit 2): the swing spell (Heroic Strike, Cleave) deals its damage through the spell log, not the attacker-state packet.</summary>
    OnNextSwingNoDamage = 0x00000004,

    /// <summary>SPELL_ATTR_ON_NEXT_SWING (bit 10); equals <see cref="SpellAttributes.OnNextSwing"/>.</summary>
    OnNextSwing = 0x00000400,

    /// <summary>SPELL_ATTR_NOT_SHAPESHIFT (bit 16): cannot be cast while shapeshifted.</summary>
    NotShapeshift = 0x00010000,

    /// <summary>SPELL_ATTR_NO_ACTIVE_DEFENSE (bit 21): cannot be dodged, parried or blocked.</summary>
    NoActiveDefense = 0x00200000,

    /// <summary>SPELL_ATTR_NOT_IN_COMBAT_ONLY_PEACEFUL (bit 28): cannot be used in combat.</summary>
    NotInCombatOnlyPeaceful = 0x10000000,
}

/// <summary>Combat-relevant Spell.dbc AttributesEx bits (vmangos SpellDefines.h:866-900).</summary>
[Flags]
public enum SpellAttributesExCombat : uint
{
    None = 0,

    /// <summary>SPELL_ATTR_EX_FINISHING_MOVE_DAMAGE (bit 20): uses combo points.</summary>
    FinishingMoveDamage = 0x00100000,

    /// <summary>SPELL_ATTR_EX_FINISHING_MOVE_DURATION (bit 22): uses combo points.</summary>
    FinishingMoveDuration = 0x00400000,

    /// <summary>SPELL_ATTR_EX_DISCOUNT_POWER_ON_MISS (bit 27): power is refunded on a dodge/parry/miss.</summary>
    DiscountPowerOnMiss = 0x08000000,

    /// <summary>SPELL_ATTR_EX_COMBO_ON_BLOCK (bit 30): Overpower.</summary>
    ComboOnBlock = 0x40000000,
}

/// <summary>Combat-relevant Spell.dbc AttributesEx2 bits (vmangos SpellDefines.h:903-935).</summary>
[Flags]
public enum SpellAttributesEx2Combat : uint
{
    None = 0,

    /// <summary>SPELL_ATTR_EX2_DO_NOT_RESET_COMBAT_TIMERS (bit 17): does not reset the auto-attack swing timers.</summary>
    DoNotResetCombatTimers = 0x00020000,

    /// <summary>SPELL_ATTR_EX2_ALLOW_WHILE_NOT_SHAPESHIFTED (bit 19).</summary>
    AllowWhileNotShapeshifted = 0x00080000,
}

/// <summary>Combat-relevant Spell.dbc AttributesEx3 bits (vmangos SpellDefines.h:938-975).</summary>
[Flags]
public enum SpellAttributesEx3Combat : uint
{
    None = 0,

    /// <summary>SPELL_ATTR_EX3_COMPLETELY_BLOCKED (bit 3): all effects are prevented on a block.</summary>
    CompletelyBlocked = 0x00000008,

    /// <summary>SPELL_ATTR_EX3_REQUIRES_MAIN_HAND_WEAPON (bit 10).</summary>
    RequiresMainHandWeapon = 0x00000400,

    /// <summary>SPELL_ATTR_EX3_ALWAYS_HIT (bit 18).</summary>
    AlwaysHit = 0x00040000,

    /// <summary>SPELL_ATTR_EX3_REQUIRES_OFFHAND_WEAPON (bit 24).</summary>
    RequiresOffhandWeapon = 0x01000000,
}
