using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>The contract categories the sweep reports. Gating categories fail the test; the rest are ratcheted against the baseline.</summary>
internal static class SweepCategory
{
    // (a) cast
    public const string Exception = "a.exception";
    public const string LoggedFault = "a.logged-fault";

    /// <summary>An error-level log line without an exception (often missing content in the test world): reported, ratcheted, not gating.</summary>
    public const string ErrorLog = "a.error-log";
    public const string UnhandledEffect = "a.unhandled-effect";
    public const string UnhandledAura = "a.unhandled-aura";
    public const string UnhandledOther = "a.unhandled-other";

    // (b) aura lifetime
    public const string DurationMismatch = "b.duration-mismatch";
    public const string RemovedEarly = "b.removed-early";
    public const string NeverExpires = "b.never-expires";
    public const string NotRestored = "b.state-not-restored";

    // (c) aura interrupt flags
    public const string InterruptNotRemoved = "c.interrupt-not-removed";
    public const string InterruptRemovedWithoutFlag = "c.removed-without-flag";

    // (d) cast / channel interrupt flags
    public const string CastNotInterrupted = "d.cast-not-interrupted";
    public const string CastInterruptedWithoutFlag = "d.cast-interrupted-without-flag";

    // (e) cooldowns
    public const string CooldownMissing = "e.cooldown-missing";
    public const string CooldownNotExpired = "e.cooldown-not-expired";
    public const string CategoryCooldownMissing = "e.category-cooldown-missing";

    // (f) costs
    public const string CostNotDeducted = "f.cost-not-deducted";
    public const string CostOverDeducted = "f.cost-over-deducted";

    // (g) range
    public const string OutOfRangeAccepted = "g.out-of-range-accepted";

    // (h) stacking
    public const string StackOverCap = "h.stack-over-cap";

    // (i) dispel
    public const string NotDispelled = "i.not-dispelled";

    // (j) invariants
    public const string HealthOutOfRange = "j.health-out-of-range";
    public const string DeathStateInconsistent = "j.death-state-inconsistent";
    public const string LeftoverFlag = "j.leftover-flag";
    public const string StuckCasting = "j.stuck-casting";
    public const string TickStopped = "j.tick-stopped";

    /// <summary>The categories that fail the test outright: (a) exceptions (thrown or caught-and-logged) and (j) invariant breaks.</summary>
    public static bool IsGating(string category) => category.StartsWith("j.", StringComparison.Ordinal)
        || category is Exception or LoggedFault;
}

/// <summary>One broken contract: the category, the spell, and what was seen (stable text: it is the baseline key).</summary>
internal sealed record SweepViolation(string Category, uint SpellId, string Detail)
{
    /// <summary>The baseline line: category, spell id and detail, tab separated.</summary>
    public string Key => $"{Category}\t{SpellId}\t{Detail}";
}

/// <summary>What happened to one swept spell.</summary>
internal sealed class SweepSpellOutcome
{
    public required uint SpellId { get; init; }

    public required string Name { get; init; }

    public required string PlayerClass { get; init; }

    public string TargetKind { get; set; } = "";

    public string CastResult { get; set; } = "";

    public bool AppliedAura { get; set; }

    /// <summary>
    /// Checks that ran (by contract letter) and checks that could not run with the reason. A skipped check is never a pass: the coverage
    /// line (<see cref="Coverage"/>) is ratcheted, so a check that stops running fails the sweep like a new violation.
    /// </summary>
    public SortedDictionary<string, string> Checks { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The coverage line: spell id, generic cast result, and the checks that ran (b..i; c with the number of interrupt actions that ran).
    /// </summary>
    public string Coverage()
    {
        var ran = new List<string>();
        foreach ((string letter, string state) in Checks)
        {
            if (letter == "c")
            {
                int actions = state.Split(',').Count(part => part.EndsWith("=ran", StringComparison.Ordinal));
                if (actions > 0)
                {
                    ran.Add("c" + actions);
                }
            }
            else if (state.StartsWith("ran", StringComparison.Ordinal) || state is "permanent-1s" or "long-config-only")
            {
                ran.Add(letter);
            }
        }

        return $"{SpellId}\t{CastResult}\t{string.Join(' ', ran)}";
    }
}

/// <summary>Where a spell's aura landed and on what the sweep cast it.</summary>
internal enum SweepTargetKind
{
    Self,
    Hostile,
    Friendly,
    GameObject,
    Location,
}

/// <summary>Classification of the implicit targets of a spell (Spell.dbc EffectImplicitTargetA/B).</summary>
internal static class SweepTargets
{
    /// <summary>Whether an effect names the explicit unit target (the one the client's range check measures).</summary>
    public static bool HasExplicitUnitTarget(SpellInfo spell) => spell.Effects.Any(e => !e.IsEmpty && e.TargetA is SpellImplicitTarget.UnitEnemy
        or SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitParty or SpellImplicitTarget.UnitRaid
        or SpellImplicitTarget.UnitFriendAndParty or SpellImplicitTarget.UnitFriendChainHeal);

    public static SweepTargetKind KindOf(SpellInfo spell)
    {
        bool enemy = false, friend = false, gameObject = false, location = false;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty)
            {
                continue;
            }

            foreach (SpellImplicitTarget target in new[] { effect.TargetA, effect.TargetB })
            {
                switch (target)
                {
                    case SpellImplicitTarget.UnitEnemy:
                    case SpellImplicitTarget.UnitEnemyNearCaster:
                        enemy = true;
                        break;
                    case SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc:
                    case SpellImplicitTarget.EnumUnitsEnemyAoeAtDynobjLoc:
                        location = true;
                        break;
                    case SpellImplicitTarget.UnitFriend:
                    case SpellImplicitTarget.UnitParty:
                    case SpellImplicitTarget.UnitRaid:
                    case SpellImplicitTarget.UnitFriendAndParty:
                    case SpellImplicitTarget.UnitFriendChainHeal:
                        friend = true;
                        break;
                    case SpellImplicitTarget.Unit:
                        // TARGET_UNIT: any unit; a harmful spell picks an enemy, otherwise a friend.
                        if (spell.IsPositive)
                        {
                            friend = true;
                        }
                        else
                        {
                            enemy = true;
                        }

                        break;
                    case SpellImplicitTarget.GameObject:
                    case SpellImplicitTarget.GameObjectItem:
                        gameObject = true;
                        break;
                    case SpellImplicitTarget.LocationCasterDest:
                    case SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc:
                    case SpellImplicitTarget.EnumUnitsPartyAoeAtDestLoc:
                    case SpellImplicitTarget.EnumUnitsScriptAoeAtDestLoc:
                        location = true;
                        break;
                }
            }
        }

        // An enemy area at a destination (Blizzard) is cast at a point next to the hostile creature, not at the unit.
        return enemy ? SweepTargetKind.Hostile : friend ? SweepTargetKind.Friendly : gameObject ? SweepTargetKind.GameObject
            : location ? SweepTargetKind.Location : SweepTargetKind.Self;
    }
}
