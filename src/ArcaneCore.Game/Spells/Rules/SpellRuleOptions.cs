using ArcaneCore.Game.Combat;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Tunables of the spell combat rules (configuration section <c>SpellRules</c>). Every default is the
/// retail / vmangos behaviour; each non-default value is a deliberate, documented deviation
/// (docs/areas/spell-rules.md).
/// </summary>
public sealed class SpellRuleOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SpellRules";

    /// <summary>
    /// Lowest base magic hit chance in percent before modifiers: 22 (vmangos SpellCaster.cpp:829-834,
    /// from a classic duel test). Set 1 to reproduce cmangos-classic, which floors the final chance only.
    /// </summary>
    public float MagicHitFloorPercent { get; init; } = 22.0f;

    /// <summary>Levels a world boss counts above its target (vmangos CONFIG WorldBossLevelDiff, World.cpp:744 default 3).</summary>
    public int WorldBossLevelDiff { get; init; } = CombatConstants.WorldBossLevelDiff;

    /// <summary>
    /// Whether creatures that are not player-owned may crit with spells. Retail: false (vmangos
    /// Unit::GetSpellCritChance, Unit.cpp:5216-5219 "Mobs cannot land critical strikes with spells").
    /// </summary>
    public bool CreatureSpellCrit { get; init; }

    /// <summary>
    /// Whether holy resistance is ignored when resisting damage. Retail (vmangos Unit::CalculateDamageAbsorbAndResist,
    /// Unit.cpp:1936-1946): false, holy is resisted like any magic school. true reproduces cmangos-classic
    /// (Unit::CalculateEffectiveMagicResistancePercent, Unit.cpp:3917: "completely ignore holy resistance value").
    /// </summary>
    public bool IgnoreHolyResistance { get; init; }

    /// <summary>Whether crowd-control spells diminish (vmangos Spell.cpp:1733-1800). Retail: true; false is a dev-host switch.</summary>
    public bool DiminishingReturns { get; init; } = true;

    /// <summary>
    /// Milliseconds after the last aura of a diminishing group ended before the level resets (vmangos
    /// Unit::GetDiminishing, Unit.cpp:7630: 15 seconds).
    /// </summary>
    public uint DiminishingResetMs { get; init; } = 15_000;

    /// <summary>
    /// Whether immunities (aura-granted and creature static masks) are enforced. Retail: true; false disables every
    /// immunity check for development hosts.
    /// </summary>
    public bool ImmunityEnforcement { get; init; } = true;
}
