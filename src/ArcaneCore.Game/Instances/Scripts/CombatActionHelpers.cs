using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>
/// The pieces RaidBossAI, RaidCreatureAI and ScriptDevBossAI share. Each base keeps its own reset, evade, cast-default and
/// encounter-state rules; only the identical arithmetic and the threat-list pick live here.
/// </summary>
internal static class CombatActionHelpers
{
    /// <summary>Health at or below <paramref name="percent"/> (ScriptDev2 <c>GetHealthPercent() &lt;= pct</c>), in integers.</summary>
    public static bool Below(Creature me, uint percent) => (ulong)me.Health * 100 <= (ulong)me.MaxHealth * percent;

    /// <summary>Health strictly below <paramref name="percent"/> (ScriptDev2 <c>GetHealthPercent() &lt; pct</c>), in integers.</summary>
    public static bool HealthBelowPct(Creature me, uint percent) => (ulong)me.Health * 100 < (ulong)me.MaxHealth * percent;

    /// <summary>
    /// The swing half of cmangos UnitAI::SetMeleeEnabled: pushes the flag onto the swing already running at <paramref name="victim"/>.
    /// The caller sets its own <see cref="CreatureAI.MeleeEnabled"/> first.
    /// </summary>
    public static void ApplyMeleeToVictim(Creature me, CreatureMapSystem? system, Unit? victim, bool enabled)
    {
        if (victim is { } v)
        {
            system?.SetMelee(me, v, enabled);
        }
    }

    /// <summary>
    /// A uniformly random threat-list target that passes <paramref name="filter"/>, or null when none does. <paramref name="pick"/>
    /// maps the highest valid index to the chosen index, so each caller keeps its own null-system policy (throw or fall back to 0).
    /// </summary>
    public static Unit? RandomThreatTarget(Creature me, Func<Unit, bool> filter, Func<int, int> pick)
    {
        Unit[] targets = [.. me.Combat.Threat.Entries.Select(e => e.Target).Where(filter)];
        return targets.Length == 0 ? null : targets[pick(targets.Length - 1)];
    }
}
