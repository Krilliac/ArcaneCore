namespace ArcaneCore.Game.Combat;

/// <summary>Weapon attack slots (vmangos SharedDefines.h WeaponAttackType).</summary>
public enum WeaponAttackType
{
    BaseAttack = 0,
    OffAttack = 1,
    RangedAttack = 2,
}

/// <summary>
/// Server-side outcome of a melee roll (vmangos DamageStructs.h MeleeHitOutcome). Never sent
/// to the client; <see cref="VictimState"/> and <see cref="HitInfo"/> are.
/// </summary>
public enum MeleeHitOutcome
{
    Evade = 0,
    Miss = 1,
    Dodge = 2,
    Block = 3,
    Parry = 4,
    Glancing = 5,
    Crit = 6,
    Crushing = 7,
    Normal = 8,
    Resist = 9,
}

/// <summary>SMSG_ATTACKERSTATEUPDATE victim state (vmangos DamageStructs.h VictimState, "client side enum").</summary>
public enum VictimState : uint
{
    Unaffected = 0,
    Normal = 1,
    Dodge = 2,
    Parry = 3,
    Interrupt = 4,
    Blocks = 5,
    Evades = 6,
    IsImmune = 7,
    Deflects = 8,
}

/// <summary>
/// SMSG_ATTACKERSTATEUPDATE hit-info flags for builds &gt; 1.9.4 (vmangos DamageStructs.h
/// HitInfo). gtker/wow_messages smsg_attackerstateupdate.wowm (1.12) agrees on every value it
/// lists (AFFECTS_VICTIM, LEFT_SWING, MISS, ABSORB, RESIST, CRITICAL_HIT, GLANCING, CRUSHING,
/// NO_ACTION, SWING_NO_HIT_SOUND) but does not list the ROLLED_*, BLOCK, BLOOD_SPURT and PVP
/// bits, which vmangos documents from sniffs; vmangos is followed.
/// </summary>
[Flags]
public enum HitInfo : uint
{
    None = 0x00000000,
    Debug = 0x00000001,
    AffectsVictim = 0x00000002,
    LeftSwing = 0x00000004,
    Miss = 0x00000010,
    Absorb = 0x00000020,
    Resist = 0x00000040,
    CriticalHit = 0x00000080,
    RolledDodge = 0x00000100,
    RolledParry = 0x00000200,
    RolledBlock = 0x00000400,
    Block = 0x00000800,
    SuppressMissText = 0x00001000,
    BloodSpurt = 0x00002000,
    Glancing = 0x00004000,
    Crushing = 0x00008000,
    NoAction = 0x00010000,
    Pvp = 0x00040000,
    SwingNoHitSound = 0x00080000,
}

/// <summary>Unit death states (vmangos UnitDefines.h DeathState).</summary>
public enum DeathState
{
    /// <summary>Show as alive.</summary>
    Alive = 0,

    /// <summary>Temporary state at death; creatures become <see cref="Corpse"/> at once, players at their next update.</summary>
    JustDied = 1,

    /// <summary>Corpse; for a player the spirit is still in the body.</summary>
    Corpse = 2,

    /// <summary>Creature: despawned. Player: spirit released (ghost).</summary>
    Dead = 3,

    /// <summary>Temporary state at resurrection.</summary>
    JustAlived = 4,
}

/// <summary>Why an auto-attack swing cannot happen (vmangos UnitDefines.h AutoAttackCheckResult).</summary>
public enum AttackCheckResult
{
    Ok = 0,
    NotInRange = 1,
    BadFacing = 2,
    CantAttack = 3,
    Dead = 4,
    FriendlyTarget = 5,
}

/// <summary>Corpse kinds (vmangos Corpse.h CorpseType).</summary>
public enum CorpseType
{
    Bones = 0,
    ResurrectablePve = 1,
    ResurrectablePvp = 2,
}
