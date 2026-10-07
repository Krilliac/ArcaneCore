namespace ArcaneCore.Game.Pets;

/// <summary>What a summoned creature is to its owner (docs/integration/pets.md). Totems are not here: the shaman lane's TotemSystem owns them.</summary>
public enum SummonKind : byte
{
    /// <summary>vmangos Pet(GUARDIAN_PET): HIGHGUID_PET, owner/creator links, not in UNIT_FIELD_SUMMON (EffectSummonGuardian).</summary>
    Guardian,

    /// <summary>vmangos Pet(SUMMON_PET): HIGHGUID_PET, the owner's UNIT_FIELD_SUMMON pet (EffectSummon).</summary>
    Pet,

    /// <summary>vmangos Pet(MINI_PET): HIGHGUID_PET, the owner's single non-combat companion (EffectSummonCritter).</summary>
    MiniPet,

    /// <summary>
    /// vmangos TemporarySummon created by SPELL_EFFECT_SUMMON_WILD: HIGHGUID_UNIT, no owner links, template
    /// faction, despawns after the spell duration (TEMPSUMMON_TIMED_DEATH_AND_DEAD_DESPAWN) or at its death.
    /// </summary>
    Wild,
}

/// <summary>Pet constants (vmangos Pet.h:134-137, DBCEnums.h:44).</summary>
public static class PetConstants
{
    /// <summary>vmangos PET_FOLLOW_DIST.</summary>
    public const float FollowDistance = 2.0f;

    /// <summary>vmangos PET_FOLLOW_ANGLE (pi/2): the default follow angle.</summary>
    public const float FollowAngle = MathF.PI / 2.0f;

    /// <summary>vmangos MINI_PET_FOLLOW_ANGLE (pi, Pet.h:136): the mini pet follows behind its owner.</summary>
    public const float MiniPetFollowAngle = MathF.PI;

    /// <summary>vmangos MINI_PET_SUMMON_ANGLE (pi/4).</summary>
    public const float MiniPetSummonAngle = MathF.PI / 4.0f;

    /// <summary>vmangos CREATURE_MAX_LEVEL.</summary>
    public const int MaxCreatureLevel = 63;

    /// <summary>vmangos Pet::SetDeathState(CORPSE): how long a dead pet, guardian or mini pet stays before it is unsummoned (Pet.cpp:652).</summary>
    public const uint CorpseDecayMs = 15_000;

    /// <summary>vmangos Pet::SetDeathState(CORPSE): a dead hunter pet stays an hour (Pet.cpp:649-650).</summary>
    public const uint HunterPetCorpseDecayMs = 3_600_000;
}

/// <summary>vmangos TotemSlot (SharedDefines.h:1727-1735).</summary>
public static class TotemSlots
{
    public const int Fire = 0;
    public const int Earth = 1;
    public const int Water = 2;
    public const int Air = 3;

    /// <summary>vmangos MAX_TOTEM_SLOT.</summary>
    public const int Count = 4;

    /// <summary>vmangos TOTEM_SLOT_NONE (custom value for no slot): SPELL_EFFECT_SUMMON_TOTEM.</summary>
    public const int None = 255;
}

/// <summary>
/// Pet tuning, bound from the <c>Pets</c> configuration section. Every default is the vmangos
/// value; a deviation is opt-in and documented in docs/integration/pets.md.
/// </summary>
public sealed class PetOptions
{
    public const string SectionName = "Pets";

    /// <summary>
    /// vmangos Pet::Update: a pet farther than this from its owner is unsummoned
    /// (<c>IsWithinDistInMap(owner, 120.0f)</c>, Pet.cpp:662-690).
    /// </summary>
    public float PetLeashDistance { get; set; } = 120.0f;

    /// <summary>
    /// vmangos Spell::EffectSummonGuardian: a non-player caster stops summoning an entry once it
    /// already has more than this many guardians of it (SpellEffects.cpp:2806). Retail 15.
    /// </summary>
    public int MaxNpcGuardiansPerEntry { get; set; } = 15;
}
