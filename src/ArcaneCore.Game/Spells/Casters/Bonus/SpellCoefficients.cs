namespace ArcaneCore.Game.Spells.Casters.Bonus;

/// <summary>
/// Which half of a spell a bonus is computed for (vmangos DamageEffectType, only the two kinds
/// that receive spell power): <see cref="SpellDirect"/> is SPELL_DIRECT_DAMAGE (spells and class
/// abilities - NOT DIRECT_DAMAGE, which is plain weapon damage and never gets spell power),
/// <see cref="OverTime"/> is DOT (periodic damage, healing and leech ticks).
/// </summary>
public enum SpellBonusKind
{
    SpellDirect = 1,
    OverTime = 2,
}

/// <summary>
/// The 1.12 spell power coefficient maths of vmangos, as pure functions of <see cref="SpellInfo"/>:
/// cast time used for the bonus, default coefficient, number of periodic ticks and the low level
/// penalty. Nothing here reads a unit; the entity side supplies the benefit (+damage/+healing).
/// Sources: D:\refs\vmangos\src\game\Spells\SpellEntry.cpp:517-634 (GetCastTimeForBonus,
/// CalculateDefaultCoefficient), :756-777 (GetAuraMaxTicks) and
/// D:\refs\vmangos\src\game\Objects\SpellCaster.cpp:1737-1812 (SpellBonusWithCoeffs, CalculateLevelPenalty).
/// </summary>
public static class SpellCoefficients
{
    /// <summary>vmangos SpellEntry::IsAreaEffectTarget (SpellEntry.h:382-402) by target number.</summary>
    private static readonly HashSet<int> AreaTargets =
    [
        7, 8,       // ENUM_UNITS_SCRIPT_AOE_AT_SRC/DEST_LOC
        15, 16,     // ENEMY_AOE_AT_SRC/DEST_LOC
        20,         // PARTY_WITHIN_CASTER_RANGE
        24,         // ENEMY_IN_CONE_24
        28,         // ENEMY_AOE_AT_DYNOBJ_LOC
        30, 31,     // FRIEND_AOE_AT_SRC/DEST_LOC
        33, 34,     // PARTY_AOE_AT_SRC/DEST_LOC
        37,         // UNIT_FRIEND_AND_PARTY
        52,         // GAMEOBJECTS_SCRIPT_AOE_AT_DEST_LOC
        56,         // RAID_WITHIN_CASTER_RANGE
        61,         // UNIT_RAID_AND_CLASS
    ];

    private const uint MaxBonusCastTime = 7000;
    private const uint MinBonusCastTime = 1500;
    private const uint DotCastTime = 3500;
    private const float FullBonusCastTime = 3500.0f;
    private const float DotReferenceDuration = 15000.0f;
    private const int MaxDotDuration = 30000;
    private const ushort DefaultTicks = 6;

    /// <summary>
    /// vmangos SpellEntry::GetCastTimeForBonus: the cast time (ms) the spell power share is scaled by.
    /// Channels use their duration, casts clamp to 1500..7000 ms, a DoT half of a non-channel uses 3500,
    /// a combined direct + periodic spell is split by <c>overTime/15000</c> against <c>castTime/3500</c>,
    /// area spells halve, leech spells halve, and every additional effect costs 5 percent. The integer
    /// truncation of every step is kept: the vmangos variable is a uint32.
    /// </summary>
    public static uint CastTimeForBonus(SpellInfo spell, SpellBonusKind kind)
    {
        ArgumentNullException.ThrowIfNull(spell);
        uint castingTime = !spell.IsChanneled ? (uint)spell.GetCastTime(0) : (uint)Math.Max(spell.GetDuration(), 0);
        castingTime = Math.Clamp(castingTime, MinBonusCastTime, MaxBonusCastTime);
        if (kind == SpellBonusKind.OverTime && !spell.IsChanneled)
        {
            castingTime = DotCastTime;
        }

        int overTime = 0;
        int extraEffects = 0;
        bool directDamage = false;
        bool areaEffect = false;
        bool leech = false;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (AreaTargets.Contains((int)effect.TargetA) || AreaTargets.Contains((int)effect.TargetB))
            {
                areaEffect = true;
            }

            switch (effect.Effect)
            {
                case SpellEffectName.SchoolDamage:
                case SpellEffectName.PowerDrain:
                case SpellEffectName.HealthLeech:
                case SpellEffectName.EnvironmentalDamage:
                case SpellEffectName.PowerBurn:
                case SpellEffectName.Heal:
                    directDamage = true;
                    leech |= effect.Effect == SpellEffectName.HealthLeech;
                    break;
                case SpellEffectName.ApplyAura:
                    switch (effect.AuraType)
                    {
                        case AuraType.PeriodicDamage:
                        case AuraType.PeriodicHeal:
                        case AuraType.PeriodicLeech:
                            leech |= effect.AuraType == AuraType.PeriodicLeech;
                            if (spell.GetDuration() != 0)
                            {
                                overTime = spell.GetDuration();
                            }

                            break;
                        case AuraType.Dummy:
                        case AuraType.ModDecreaseSpeed:
                            extraEffects++;
                            break;
                        case AuraType.ModConfuse:
                        case AuraType.ModStun:
                        case AuraType.ModRoot:
                            extraEffects += 2;
                            break;
                    }

                    break;
            }
        }

        if (overTime > 0 && castingTime > 0 && directDamage)
        {
            // Combined spells with both over time and direct damage (SpellEntry.cpp:593-606).
            uint originalCastTime = Math.Clamp((uint)spell.GetCastTime(0), MinBonusCastTime, MaxBonusCastTime);
            float overTimePortion = overTime / DotReferenceDuration;
            float portionToOverTime = overTimePortion / (overTimePortion + (originalCastTime / FullBonusCastTime));
            if (kind == SpellBonusKind.OverTime)
            {
                castingTime = (uint)(castingTime * portionToOverTime);
            }
            else if (portionToOverTime < 1.0f)
            {
                castingTime = (uint)(castingTime * (1 - portionToOverTime));
            }
            else
            {
                castingTime = 0;
            }
        }

        if (areaEffect)
        {
            castingTime /= 2;
        }

        // Only HEALTH_LEECH and PERIODIC_LEECH halve (SpellEntry.cpp:598-606): PowerDrain / PowerBurn /
        // PeriodicManaLeech do not.
        if (leech)
        {
            castingTime /= 2;
        }

        for (int i = 0; i < extraEffects; i++)
        {
            castingTime = (uint)(castingTime * 0.95f);
        }

        return castingTime;
    }

    /// <summary>
    /// vmangos SpellEntry::GetAuraMaxTicks: duration (clamped to 30000 ms) over the amplitude of the first
    /// periodic damage / heal / leech effect; 1 for a spell with no duration, 6 when no such effect carries an amplitude.
    /// </summary>
    public static ushort AuraMaxTicks(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        int duration = spell.GetDuration();
        if (duration == 0)
        {
            return 1;
        }

        if (duration > MaxDotDuration)
        {
            duration = MaxDotDuration;
        }

        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect == SpellEffectName.ApplyAura
                && effect.AuraType is AuraType.PeriodicDamage or AuraType.PeriodicHeal or AuraType.PeriodicLeech)
            {
                if (effect.Amplitude != 0)
                {
                    // The vmangos expression is int32 / uint32 returned through uint16.
                    return unchecked((ushort)((uint)duration / effect.Amplitude));
                }

                break;
            }
        }

        return DefaultTicks;
    }

    /// <summary>
    /// vmangos SpellEntry::CalculateDefaultCoefficient: <c>castTimeForBonus / 3500</c>, and for the over-time
    /// half additionally the duration factor (<c>duration / 15000</c>, channels excluded) divided by the tick count.
    /// </summary>
    public static float DefaultCoefficient(SpellInfo spell, SpellBonusKind kind)
    {
        ArgumentNullException.ThrowIfNull(spell);
        float dotFactor = 1.0f;
        if (kind == SpellBonusKind.OverTime)
        {
            if (!spell.IsChanneled)
            {
                dotFactor = spell.GetDuration() / DotReferenceDuration;
            }

            ushort ticks = AuraMaxTicks(spell);
            if (ticks != 0)
            {
                dotFactor /= ticks;
            }
        }

        return CastTimeForBonus(spell, kind) / FullBonusCastTime * dotFactor;
    }

    /// <summary>
    /// vmangos SpellCaster::CalculateLevelPenalty (the Nostalrius 1.12 version, not the BC formula): spells
    /// of level 1..20 lose 3.75 percent per level below 20; every other level is not penalised.
    /// </summary>
    public static float LevelPenalty(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        uint spellLevel = spell.SpellLevel;
        return spellLevel is 0 or > 20 ? 1.0f : 1 - ((20.0f - spellLevel) * 0.0375f);
    }

    /// <summary>
    /// The coefficient and level penalty vmangos SpellBonusWithCoeffs uses for one effect: an explicit
    /// per-effect coefficient (spell_template.effectBonusCoefficient, &gt;= 0) wins and then carries its own level
    /// penalty, so none is applied; otherwise the default coefficient and the level penalty are used.
    /// </summary>
    public static EffectiveCoefficient Resolve(SpellInfo spell, SpellBonusKind kind, float? explicitCoefficient = null)
        => explicitCoefficient is >= 0.0f
            ? new EffectiveCoefficient(explicitCoefficient.Value, 1.0f)
            : new EffectiveCoefficient(DefaultCoefficient(spell, kind), LevelPenalty(spell));
}

/// <summary>A resolved bonus coefficient and the level penalty that goes with it (1 for an explicit coefficient).</summary>
public readonly record struct EffectiveCoefficient(float Coefficient, float LevelPenalty)
{
    /// <summary>The factor the benefit is multiplied by.</summary>
    public float Factor => Coefficient * LevelPenalty;
}
