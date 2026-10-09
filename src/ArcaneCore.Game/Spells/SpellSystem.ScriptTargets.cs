using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>The spell_script_target types for units (vmangos SpellMgr.h SpellTargetType; 0, the game object type, is not selected here).</summary>
    private const uint ScriptTargetCreature = 1;
    private const uint ScriptTargetDead = 2;
    private const uint ScriptTargetPlayer = 3;

    /// <summary>
    /// The unit script targets of vmangos Spell::SetTargetMap: TARGET_UNIT_SCRIPT_NEAR_CASTER (38) goes through
    /// Spell::CheckScriptTargeting, which keeps a valid explicit target and otherwise takes the one nearest unit any
    /// spell_script_target row lists (a single unit, Spell.cpp:445-575); TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC (7)
    /// fills the source area and keeps the units whose entry and state a row matches, or every unit when the spell has
    /// no rows (Spell.cpp:2356-2398). The caster is excluded from both. vmangos fails a 38 cast with no target
    /// (SPELL_FAILED_BAD_TARGETS); here the effect only gets no unit.
    /// </summary>
    private List<(Unit Unit, float Multiplier)> SelectScriptTargets(
        SpellCast cast, SpellEffectInfo effect, int effectIndex, bool nearest, Unit? explicitTarget)
    {
        if (cast.Caster.Map is not { } map) return [];
        IReadOnlyList<SpellStore.ScriptTarget> rows = Store.GetScriptTargets(cast.Spell.Id);
        if (nearest && rows.Count == 0) return [];
        // vmangos SetTargetMap: the effect radius, else the spell's maximum range (Spell.cpp:2049-2053).
        float radius = effect.Radius > 0 ? effect.Radius : cast.Spell.Range.Max;
        if (radius <= 0) return [];
        // mangos-classic Spell::CheckScriptTargeting bounds the "anywhere" range (50000) of an entry search to 200 yards.
        if (nearest && radius >= 50_000f) radius = 200f;
        (float x, float y, float z) = nearest ? (cast.Caster.X, cast.Caster.Y, cast.Caster.Z) : SourceCentre(cast, effect);

        bool Matches(Unit unit)
        {
            if (ReferenceEquals(unit, cast.Caster)) return false;
            if (rows.Count == 0) return unit.IsAlive;
            return rows.Any(row => (row.InverseEffectMask & (1u << effectIndex)) == 0
                && (row.TargetEntry == 0 && row.Type == ScriptTargetPlayer && unit is Player
                    || unit is Creature creature && creature.Entry == row.TargetEntry)
                && (row.Type switch
                {
                    ScriptTargetCreature => unit is Creature && unit.IsAlive,
                    ScriptTargetDead => unit is Creature && !unit.IsAlive,
                    ScriptTargetPlayer => unit is Player && unit.IsAlive,
                    _ => false,
                }));
        }

        List<Unit> units = [.. UnitsInRadius(map, x, y, z, radius).Where(Matches)
            .Where(unit => IsInLineOfSightFromPoint(cast.Spell, map, x, y, z, unit))];
        if (nearest)
        {
            // CheckScriptTargeting: a matching explicit target wins, else the nearest match; either way one unit.
            if (explicitTarget is not null && units.Contains(explicitTarget)) return [(explicitTarget, 1f)];
            float Distance(Unit unit) => (unit.X - x) * (unit.X - x) + (unit.Y - y) * (unit.Y - y) + (unit.Z - z) * (unit.Z - z);
            return units.Count == 0 ? [] : [(units.MinBy(Distance)!, 1f)];
        }

        CapTargets(units, cast.Spell.MaxAffectedTargets);
        return [.. units.Select(unit => (unit, 1f))];
    }
}
