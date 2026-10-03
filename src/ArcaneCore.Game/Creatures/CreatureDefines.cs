namespace ArcaneCore.Game.Creatures;

/// <summary>Creature life cycle (the subset of vmangos DeathState this area drives).</summary>
public enum CreatureDeathState
{
    /// <summary>Spawned and alive (vmangos ALIVE).</summary>
    Alive,

    /// <summary>Dead, the corpse is visible and decaying (vmangos CORPSE).</summary>
    Corpse,

    /// <summary>Corpse removed; invisible until the respawn time (vmangos DEAD).</summary>
    Dead,
}

/// <summary>vmangos/cmangos MovementGeneratorType values stored in <c>creature.MovementType</c>.</summary>
public enum CreatureMovementType : byte
{
    Idle = 0,
    Random = 1,
    Waypoint = 2,
}

/// <summary>vmangos CreatureEliteType (creature_template.Rank).</summary>
public enum CreatureRank : uint
{
    Normal = 0,
    Elite = 1,
    RareElite = 2,
    WorldBoss = 3,
    Rare = 4,
}

/// <summary>Spline flags for build 5875 (vmangos MoveSplineFlag.h).</summary>
[Flags]
public enum SplineFlags : uint
{
    None = 0x00000000,
    Done = 0x00000001,
    Falling = 0x00000002,
    Runmode = 0x00000100,
    Flying = 0x00000200,
    NoSpline = 0x00000400,
    FinalPoint = 0x00010000,
    FinalTarget = 0x00020000,
    FinalAngle = 0x00040000,
    Cyclic = 0x00100000,
    EnterCycle = 0x00200000,
    Frozen = 0x00400000,

    /// <summary>Flags never written into SMSG_MONSTER_MOVE (vmangos Mask_No_Monster_Move).</summary>
    MaskNoMonsterMove = FinalPoint | FinalTarget | FinalAngle | Done,
}

/// <summary>SMSG_MONSTER_MOVE move types (vmangos packet_builder.cpp MonsterMoveType; gtker MonsterMoveType agrees).</summary>
public enum MonsterMoveType : byte
{
    Normal = 0,
    Stop = 1,
    FacingSpot = 2,
    FacingTarget = 3,
    FacingAngle = 4,
}

/// <summary>
/// Creature tuning, bound from the <c>Creatures</c> configuration section. Defaults are
/// vmangos' and cmangos-classic's World.cpp defaults unless stated.
/// </summary>
public sealed class CreatureOptions
{
    public const string SectionName = "Creatures";

    /// <summary>Corpse.Decay.NORMAL (s): vmangos + cmangos-classic 300.</summary>
    public uint CorpseDecayNormalSeconds { get; set; } = 300;

    /// <summary>Corpse.Decay.RARE (s): 900.</summary>
    public uint CorpseDecayRareSeconds { get; set; } = 900;

    /// <summary>Corpse.Decay.ELITE (s): 600.</summary>
    public uint CorpseDecayEliteSeconds { get; set; } = 600;

    /// <summary>Corpse.Decay.RAREELITE (s): 1200.</summary>
    public uint CorpseDecayRareEliteSeconds { get; set; } = 1200;

    /// <summary>Corpse.Decay.WORLDBOSS (s): 3600.</summary>
    public uint CorpseDecayWorldBossSeconds { get; set; } = 3600;

    /// <summary>Random and waypoint movement; off leaves every creature idle at its spawn point.</summary>
    public bool MovementEnabled { get; set; } = true;

    /// <summary>Rate.Creature.Aggro: multiplies every aggro radius (0 disables aggro on sight).</summary>
    public float AggroRate { get; set; } = 1.0f;

    /// <summary>CreatureFamilyAssistanceRadius (yd): same-faction creatures this close to a creature entering combat join it.</summary>
    public float AssistanceRadius { get; set; } = 10.0f;

    /// <summary>CreatureFamilyAssistanceDelay (ms) before those helpers attack.</summary>
    public uint AssistanceDelayMs { get; set; } = 1500;

    /// <summary>CreatureFamilyFleeAssistanceRadius (yd): how far a fleeing creature looks for help.</summary>
    public float FleeAssistanceRadius { get; set; } = 30.0f;

    /// <summary>CreatureFamilyFleeDelay (ms): timed flight when no helper is found.</summary>
    public uint FleeDelayMs { get; set; } = 7000;

    /// <summary>ThreatRadius (yd): a victim farther than this from where combat began is dropped (leash); none in instances.</summary>
    public float ThreatRadius { get; set; } = 60.0f;

    /// <summary>FactionTemplate.dbc for creature hostility when no catalog is registered (empty = nobody aggroes on sight).</summary>
    public string? FactionTemplateDbcPath { get; set; }
}

/// <summary>
/// Ground height lookup used to place random-movement points. This is a seam for the
/// grid/map/terrain area: until a terrain provider is registered, creatures keep their spawn
/// height (<see cref="NoTerrainHeight"/>).
/// </summary>
public interface ICreatureHeightProvider
{
    /// <summary>Ground height at (x, y) near <paramref name="z"/>, or null when unknown.</summary>
    float? GetHeight(uint mapId, float x, float y, float z);
}

/// <summary>The default height provider: no terrain data, so heights are unknown.</summary>
public sealed class NoTerrainHeight : ICreatureHeightProvider
{
    public static readonly NoTerrainHeight Instance = new();

    public float? GetHeight(uint mapId, float x, float y, float z) => null;
}
