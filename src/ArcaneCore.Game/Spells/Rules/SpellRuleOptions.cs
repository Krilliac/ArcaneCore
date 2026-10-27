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

    /// <summary>
    /// Spell batch window in ms (SpellSystem.Batching.cs): a player's cast that goes off is held until the window closes and then resolves with
    /// every other cast of that window, so two players who kill, charge or interrupt each other in one window both get their action. Retail 1.12
    /// batched (Kaivax, Blizzard, 2019, https://us.forums.blizzard.com/en/wow/t/137118/1) and Blizzard's Classic servers used 400 ms until
    /// 1.13.7 (https://us.forums.blizzard.com/en/wow/t/838945). Opt-in while it is verified against a client: 0 (default) resolves every cast at
    /// once; 400 is the retail value.
    /// </summary>
    public uint SpellBatchWindowMs { get; init; }

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

    /// <summary>
    /// Path of a file with vmangos' partial-resist outcome table (<see cref="ResistOutcomeTable"/>: 31 rows of
    /// <c>resist100,resist75,resist50,resist25,resist0,chanceResist</c>, Unit.cpp:1885-1918), which the repository does
    /// not ship. Unset: the engine draws the two quarter steps around the average resist (mean-preserving
    /// approximation, docs/areas/spell-rules.md); the damage-over-time one-tenth rule and the vulnerability bonus apply either way.
    /// </summary>
    public string? ResistTablePath { get; init; }

    /// <summary>Whether crowd-control spells diminish (vmangos Spell.cpp:1733-1800). Retail: true; false is a dev-host switch.</summary>
    public bool DiminishingReturns { get; init; } = true;

    /// <summary>
    /// Milliseconds after the last aura of a diminishing group ended before the level resets. Retail: 15 seconds, patch 1.4.0
    /// (2005-04-19): "Spells must be used on the same target within 15 seconds of the *end* of the duration in order to be
    /// diminished" (https://vanilla-wow-archive.fandom.com/wiki/Patch_1.4.0, PvP section); vmangos Unit::GetDiminishing, Unit.cpp:7630, agrees.
    /// </summary>
    public uint DiminishingResetMs { get; init; } = 15_000;

    /// <summary>
    /// Period in milliseconds of a reset sweep for diminishing returns. Retail: 0, the level resets as soon as
    /// <see cref="DiminishingResetMs"/> has passed (patch 1.4.0). A positive value (for example 5000) reproduces the "resets between 15 and
    /// 20 seconds" reading reported for Classic Era (https://warcraft.wiki.gg/wiki/Diminishing_returns, Classic section, and
    /// https://vanilla-wow-archive.fandom.com/wiki/Diminishing_returns: "it checks every 5 seconds"); neither page cites a Blizzard source.
    /// </summary>
    public uint DiminishingResetSweepMs { get; init; }

    /// <summary>
    /// Whether Frost Shock shares the controlled-root diminishing group with Frost Nova and Entangling Roots. Retail: false, its own
    /// "slowing effect" group (patch 1.4.0, Shaman: "This is considered a slowing effect", against "an immobilizing effect" for the roots;
    /// https://vanilla-wow-archive.fandom.com/wiki/Patch_1.4.0). true reproduces vmangos (SpellEntry.cpp:333-343, DIMINISHING_CONTROL_ROOT).
    /// </summary>
    public bool FrostShockDiminishesAsRoot { get; init; }

    /// <summary>
    /// Whether immunities (aura-granted and creature static masks) are enforced. Retail: true; false disables every
    /// immunity check for development hosts.
    /// </summary>
    public bool ImmunityEnforcement { get; init; } = true;
}
