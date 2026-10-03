namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// Which engine authored a template's <c>ExtraFlags</c> / <c>flags_extra</c> column. The two
/// references assign different meanings to the same bits (bit 0x01 is INSTANCE_BIND in cmangos and
/// NO_LEASH_EVADE in vmangos; 0x20 is RUN_DURING_WANDER versus NO_MOVEMENT_PAUSE), so the importer
/// records the dialect and <see cref="CreatureBehaviour.Normalize"/> decodes it once. Game code reads
/// <see cref="CreatureBehaviourFlags"/>, never raw dialect bits.
/// </summary>
public enum CreatureExtraFlagsDialect : byte
{
    /// <summary>Rows written before the dialect was recorded: only the bits both engines agree on are decoded.</summary>
    Unknown = 0,

    /// <summary>cmangos-classic <c>CreatureFlagsExtra</c> (src/game/Entities/Creature.h:49-72).</summary>
    CMangos = 1,

    /// <summary>vmangos <c>CreatureFlagsExtra</c> (src/game/Objects/CreatureDefines.h:157-176).</summary>
    VMangos = 2,
}

/// <summary>Which engine's EventAI table layout the loaded rows use (the numbering of events, actions and flags).</summary>
public enum EventAiDialect : byte
{
    /// <summary>cmangos-classic <c>creature_ai_scripts</c> (what classic-db carries). The only dialect imported.</summary>
    CMangos = 0,

    /// <summary>vmangos <c>creature_ai_events</c> + generic script commands. Reserved: those rows are refused with a warning.</summary>
    VMangos = 1,
}

/// <summary>
/// The creature behaviour switches the AI and movement code consult, normalized from the template's
/// dialect-specific <c>ExtraFlags</c> and the dialect-neutral <c>StaticFlags1/2</c>.
/// Only behaviour-relevant bits are decoded; the others (MMAP forcing, spawn counting, AoI size and
/// the like) are intentionally not represented.
/// </summary>
[Flags]
public enum CreatureBehaviourFlags : uint
{
    None = 0,

    /// <summary>No proximity aggro. cmangos NO_AGGRO_ON_SIGHT / vmangos NO_AGGRO, bit 0x02 in both.</summary>
    NoAggro = 1u << 0,

    NoParry = 1u << 1,

    NoBlock = 1u << 2,

    Invisible = 1u << 3,

    Guard = 1u << 4,

    /// <summary>cmangos INSTANCE_BIND (0x01): killing the creature binds the killer to the instance.</summary>
    InstanceBind = 1u << 5,

    /// <summary>cmangos NO_PARRY_HASTEN (0x08): no counter-attack haste on parry.</summary>
    NoParryHasten = 1u << 6,

    /// <summary>cmangos RUN_DURING_WANDER (0x20): 15 percent of random-movement legs are run.</summary>
    RunDuringWander = 1u << 7,

    /// <summary>cmangos AGGRO_ZONE (0x200) / vmangos static flag 2 FORCE_RAID_COMBAT (0x02): enters combat with the zone on aggro.</summary>
    AggroZone = 1u << 8,

    /// <summary>cmangos NO_CALL_ASSIST (0x800): does not call for assistance on aggro.</summary>
    NoCallAssist = 1u << 9,

    /// <summary>cmangos ACTIVE (0x1000): its grid stays loaded.</summary>
    Active = 1u << 10,

    /// <summary>cmangos WALK_IN_WATER (0x8000).</summary>
    WalkInWater = 1u << 11,

    /// <summary>cmangos CIVILIAN (0x10000) or the template's <c>Civilian</c> column.</summary>
    Civilian = 1u << 12,

    /// <summary>cmangos NO_MELEE (0x20000): the creature cannot melee.</summary>
    NoMelee = 1u << 13,

    /// <summary>vmangos NO_LEASH_EVADE (0x01): does not evade because the target ran away.</summary>
    NoLeashEvade = 1u << 14,

    /// <summary>vmangos NO_UNREACHABLE_EVADE (0x08): does not evade because the target is unreachable.</summary>
    NoUnreachableEvade = 1u << 15,

    /// <summary>vmangos NO_MOVEMENT_PAUSE (0x20): keeps walking when a player talks to it.</summary>
    NoMovementPause = 1u << 16,

    /// <summary>vmangos ALWAYS_RUN (0x40): uses run speed out of combat.</summary>
    AlwaysRun = 1u << 17,

    /// <summary>vmangos NO_THREAT_LIST (0x800).</summary>
    NoThreatList = 1u << 18,

    /// <summary>vmangos KEEP_POSITIVE_AURAS_ON_EVADE (0x1000).</summary>
    KeepPositiveAurasOnEvade = 1u << 19,

    /// <summary>vmangos NO_ASSIST (0x10000): does not join when nearby creatures aggro.</summary>
    NoAssist = 1u << 20,

    /// <summary>vmangos NO_TARGET (0x20000): passive, acquires no targets.</summary>
    NoTarget = 1u << 21,

    /// <summary>Static flag SESSILE (0x100): cannot move (both engines).</summary>
    Sessile = 1u << 22,

    /// <summary>Static flag IGNORE_COMBAT (0x02000000): react state passive (both engines).</summary>
    IgnoreCombat = 1u << 23,

    /// <summary>Static flag ONLY_ATTACK_PVP_ENABLING (0x04000000): no proximity aggro on players that are not PvP flagged.</summary>
    OnlyAttackPvpEnabling = 1u << 24,

    /// <summary>Static flag CALLS_GUARDS (0x08000000): summons a guard when an opposite-faction player is near.</summary>
    CallsGuards = 1u << 25,

    /// <summary>Static flag NO_AUTOMATIC_REGEN (0x400): no health or mana regeneration.</summary>
    NoAutomaticRegen = 1u << 26,

    /// <summary>
    /// Static flag NO_MELEE_FLEE (0x00100000; vmangos NO_MELEE, original comment "Flee"): prevents melee
    /// and makes the creature flee when it enters combat (cmangos CreatureDefines.h:49).
    /// </summary>
    NoMeleeFlee = 1u << 27,
}

/// <summary>Decodes the dialect-specific template flag columns into <see cref="CreatureBehaviourFlags"/>.</summary>
public static class CreatureBehaviour
{
    // Static flags are identical in both references:
    // cmangos Entities/CreatureDefines.h:27-61 (CreatureStaticFlags, StaticFlags2 at :65), vmangos Objects/CreatureDefines.h:98-140.
    private const uint StaticSessile = 0x00000100;
    private const uint StaticNoAutomaticRegen = 0x00000400;
    private const uint StaticNoMeleeFlee = 0x00100000;
    private const uint StaticIgnoreCombat = 0x02000000;
    private const uint StaticOnlyAttackPvpEnabling = 0x04000000;
    private const uint StaticCallsGuards = 0x08000000;
    private const uint Static2ForceRaidCombat = 0x00000002;

    public static CreatureBehaviourFlags Normalize(
        CreatureExtraFlagsDialect dialect, uint extraFlags, uint staticFlags1, uint staticFlags2, bool civilian)
    {
        CreatureBehaviourFlags flags = CreatureBehaviourFlags.None;

        // Bits with the same meaning in both references: NO_AGGRO 0x02, NO_PARRY 0x04, NO_BLOCK 0x10,
        // INVISIBLE 0x80, GUARD 0x400 (cmangos Creature.h:50-59, vmangos CreatureDefines.h:158-167).
        Set(ref flags, extraFlags, 0x02, CreatureBehaviourFlags.NoAggro);
        Set(ref flags, extraFlags, 0x04, CreatureBehaviourFlags.NoParry);
        Set(ref flags, extraFlags, 0x10, CreatureBehaviourFlags.NoBlock);
        Set(ref flags, extraFlags, 0x80, CreatureBehaviourFlags.Invisible);
        Set(ref flags, extraFlags, 0x400, CreatureBehaviourFlags.Guard);

        switch (dialect)
        {
            case CreatureExtraFlagsDialect.CMangos:
                // cmangos-classic src/game/Entities/Creature.h:49-72.
                Set(ref flags, extraFlags, 0x00000001, CreatureBehaviourFlags.InstanceBind);
                Set(ref flags, extraFlags, 0x00000008, CreatureBehaviourFlags.NoParryHasten);
                Set(ref flags, extraFlags, 0x00000020, CreatureBehaviourFlags.RunDuringWander);
                Set(ref flags, extraFlags, 0x00000200, CreatureBehaviourFlags.AggroZone);
                Set(ref flags, extraFlags, 0x00000800, CreatureBehaviourFlags.NoCallAssist);
                Set(ref flags, extraFlags, 0x00001000, CreatureBehaviourFlags.Active);
                Set(ref flags, extraFlags, 0x00008000, CreatureBehaviourFlags.WalkInWater);
                Set(ref flags, extraFlags, 0x00010000, CreatureBehaviourFlags.Civilian);
                Set(ref flags, extraFlags, 0x00020000, CreatureBehaviourFlags.NoMelee);
                break;

            case CreatureExtraFlagsDialect.VMangos:
                // vmangos src/game/Objects/CreatureDefines.h:157-176.
                Set(ref flags, extraFlags, 0x00000001, CreatureBehaviourFlags.NoLeashEvade);
                Set(ref flags, extraFlags, 0x00000008, CreatureBehaviourFlags.NoUnreachableEvade);
                Set(ref flags, extraFlags, 0x00000020, CreatureBehaviourFlags.NoMovementPause);
                Set(ref flags, extraFlags, 0x00000040, CreatureBehaviourFlags.AlwaysRun);
                Set(ref flags, extraFlags, 0x00000800, CreatureBehaviourFlags.NoThreatList);
                Set(ref flags, extraFlags, 0x00001000, CreatureBehaviourFlags.KeepPositiveAurasOnEvade);
                Set(ref flags, extraFlags, 0x00010000, CreatureBehaviourFlags.NoAssist);
                Set(ref flags, extraFlags, 0x00020000, CreatureBehaviourFlags.NoTarget);
                break;
        }

        Set(ref flags, staticFlags1, StaticSessile, CreatureBehaviourFlags.Sessile);
        Set(ref flags, staticFlags1, StaticNoAutomaticRegen, CreatureBehaviourFlags.NoAutomaticRegen);
        Set(ref flags, staticFlags1, StaticNoMeleeFlee, CreatureBehaviourFlags.NoMeleeFlee);
        Set(ref flags, staticFlags1, StaticIgnoreCombat, CreatureBehaviourFlags.IgnoreCombat);
        Set(ref flags, staticFlags1, StaticOnlyAttackPvpEnabling, CreatureBehaviourFlags.OnlyAttackPvpEnabling);
        Set(ref flags, staticFlags1, StaticCallsGuards, CreatureBehaviourFlags.CallsGuards);
        Set(ref flags, staticFlags2, Static2ForceRaidCombat, CreatureBehaviourFlags.AggroZone);

        if (civilian)
        {
            flags |= CreatureBehaviourFlags.Civilian;
        }

        return flags;
    }

    private static void Set(ref CreatureBehaviourFlags flags, uint source, uint bit, CreatureBehaviourFlags flag)
    {
        if ((source & bit) != 0)
        {
            flags |= flag;
        }
    }
}
