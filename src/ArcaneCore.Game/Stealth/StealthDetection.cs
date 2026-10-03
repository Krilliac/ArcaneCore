using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>Everything vmangos Unit::CanDetectStealthOf reads, already resolved to numbers.</summary>
/// <param name="Distance">Distance between detector and target (vmangos passes the caller's distance).</param>
/// <param name="DetectorStunned">The detector has UNIT_STATE_STUNNED: a stunned unit never detects.</param>
/// <param name="DetectorIsPlayer">Player detector: 9 (player target) or 21 (creature target) base yards, 1.5 per level.</param>
/// <param name="TargetIsPlayer">A player target uses its MOD_STEALTH total as its stealth skill, a creature uses level x 5.</param>
/// <param name="DetectorLevel">The detector's level as seen by the target (vmangos GetLevelForTarget: world bosses count as target level + 3).</param>
/// <param name="TargetLevel">The target's level as seen by the detector.</param>
/// <param name="TargetStealthTotal">Sum of the target's MOD_STEALTH aura amounts (used for player targets only).</param>
/// <param name="TargetStealthLevelMod">Sum of the target's MOD_STEALTH_LEVEL aura amounts.</param>
/// <param name="DetectorDetectMod">Sum of the detector's MOD_STEALTH_DETECT auras with misc value 0 (Perception: +50).</param>
/// <param name="TargetInDetectorArc">The target is inside the detector's 180 degree front arc.</param>
public readonly record struct StealthDetectionInput(
    float Distance,
    bool DetectorStunned,
    bool DetectorIsPlayer,
    bool TargetIsPlayer,
    int DetectorLevel,
    int TargetLevel,
    int TargetStealthTotal,
    int TargetStealthLevelMod,
    int DetectorDetectMod,
    bool TargetInDetectorArc);

/// <summary>The outcome of a stealth detection roll-free check.</summary>
/// <param name="Detected">The detector sees the target.</param>
/// <param name="Alert">The target is beyond sight but inside the alert band (visible distance, visible distance + 5].</param>
/// <param name="VisibleDistance">The distance up to which the target is detected (after the arc penalty).</param>
public readonly record struct StealthDetectionResult(bool Detected, bool Alert, float VisibleDistance);

/// <summary>
/// The vmangos stealth detection formula (Unit::CanDetectStealthOf, Unit.cpp:6543-6616). The comment there records the
/// sniffed retail measurements the constants come from: always detected below 1.5 yards; a creature detector sees
/// 5/6 yard plus 5/6 yard per level of skill difference; a player detector 9 yards (21 against creatures) plus 1.5 yards
/// per level; capped at 30; minus 9 yards when the target is behind the detector (applied after the cap); alert
/// distance is 5 yards beyond the aggro distance. The tests prove the code equals those measurements; they cannot prove
/// retail behaves so (docs/areas/rogue.md).
/// </summary>
public static class StealthDetection
{
    /// <summary>Below this distance the target is always detected (Unit.cpp:6585 "collision").</summary>
    public const float CollisionDistance = 1.5f;

    /// <summary>Visible-distance cap (Unit.cpp:6603).</summary>
    public const float MaxVisibleDistance = 30.0f;

    /// <summary>Reduction when the target is outside the detector's front arc (Unit.cpp:6609).</summary>
    public const float BehindPenalty = 9.0f;

    /// <summary>The alert band width beyond the visible distance (Unit.cpp:6612).</summary>
    public const float AlertBand = 5.0f;

    /// <summary>Hunter's Mark / far visibility is a separate rule; this is only the stealth distance model.</summary>
    public static StealthDetectionResult Evaluate(in StealthDetectionInput input, StealthOptions? options = null)
    {
        options ??= StealthOptions.Default;
        if (input.DetectorStunned)
        {
            return new StealthDetectionResult(false, false, 0f);
        }

        if (input.Distance < CollisionDistance)
        {
            return new StealthDetectionResult(true, false, CollisionDistance);
        }

        if (input.Distance > (input.DetectorIsPlayer ? options.MaxPlayerDetectRange : options.MaxCreatureDetectRange))
        {
            return new StealthDetectionResult(false, false, 0f);
        }

        float visibleDistance = input.DetectorIsPlayer ? (input.TargetIsPlayer ? 9.0f : 21.0f) : (5.0f / 6.0f);
        float yardsPerLevel = input.DetectorIsPlayer ? 1.5f : 5.0f / 6.0f;
        int stealthSkill = input.TargetIsPlayer ? input.TargetStealthTotal : input.TargetLevel * 5;
        stealthSkill += input.TargetStealthLevelMod;
        int detectSkill = (input.DetectorLevel * 5) + input.DetectorDetectMod;
        if (input.DetectorLevel - input.TargetLevel > 3)
        {
            yardsPerLevel *= 2;
        }

        visibleDistance += (detectSkill - stealthSkill) * yardsPerLevel / 5.0f;
        visibleDistance = Math.Clamp(visibleDistance, 0.0f, MaxVisibleDistance);
        if (!input.TargetInDetectorArc)
        {
            visibleDistance -= BehindPenalty;
        }

        float alertRange = visibleDistance + AlertBand;
        bool alert = input.Distance <= alertRange && input.Distance > visibleDistance;
        return new StealthDetectionResult(input.Distance <= visibleDistance, alert, visibleDistance);
    }

    /// <summary>
    /// vmangos SpellCaster::GetLevelForTarget (SpellCaster.cpp:70-84) for a unit: a world boss creature counts as its
    /// target's level plus <see cref="CombatConstants.WorldBossLevelDiff"/> (clamped 1-255), everything else its own level.
    /// </summary>
    public static int LevelForTarget(Unit unit, Unit? target)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is ICombatCreature { IsWorldBoss: true } && target is not null)
        {
            return Math.Clamp(target.Level + CombatConstants.WorldBossLevelDiff, 1, 255);
        }

        return unit.Level;
    }

    /// <summary>
    /// Resolve the inputs for two live units and evaluate (vmangos Unit::CanDetectStealthOf). The auras come from
    /// <paramref name="spells"/>: MOD_STEALTH, MOD_STEALTH_LEVEL on the target, MOD_STEALTH_DETECT with misc value 0 on the detector.
    /// </summary>
    public static StealthDetectionResult CanDetectStealthOf(SpellSystem spells, Unit detector, Unit target, float distance, StealthOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(target);
        var input = new StealthDetectionInput(
            distance,
            (detector.UnitFlags & UnitFlags.Stunned) != 0,
            detector is Player,
            target is Player,
            LevelForTarget(detector, target),
            LevelForTarget(target, detector),
            spells.GetTotalAuraModifier(target, AuraType.ModStealth),
            spells.GetTotalAuraModifier(target, AuraType.ModStealthLevel),
            spells.GetTotalAuraModifier(detector, AuraType.ModStealthDetect, aura => aura.MiscValue == 0),
            MapCombat.HasInArc(detector, target, CombatConstants.DefaultArc));
        return Evaluate(input, options);
    }
}
