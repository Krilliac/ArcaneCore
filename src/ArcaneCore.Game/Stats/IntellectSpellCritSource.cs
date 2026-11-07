using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// The stat-driven base spell crit chance (vmangos Unit::GetSpellCritFromIntellect, Unit.cpp:2597-2641): a player gets
/// <see cref="SpellCritTable.CritFromIntellect"/> from its class, level and total intellect; every other unit gets
/// <see cref="SpellCritTable.NonPlayerSpellCrit"/>. The school does not matter; school and generic crit come from auras.
/// <para>
/// Stateless and thread-safe; reads the caster's update fields, so call it on the world thread like the rest of the spell system.
/// The table is not proven retail (<see cref="SpellCritTable.VerifiedRetail"/> is false).
/// </para>
/// </summary>
public sealed class IntellectSpellCritSource : ISpellCritSource
{
    /// <summary>Index of intellect among the UNIT_FIELD_STAT fields (strength, agility, stamina, intellect, spirit).</summary>
    private const int IntellectStat = 3;

    /// <inheritdoc />
    public float SpellCritPercent(Unit caster, SpellSchool school)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (caster is not Player player)
        {
            return SpellCritTable.NonPlayerSpellCrit;
        }

        // The field can hold a negative value here (flat negative auras/items are written unclamped); vmangos clamps stats at 0 on write
        // (Object::SetStatInt32Value, Object.cpp:1274-1280), so read it signed and floor at 0 rather than wrapping to ~4.29e9.
        int intellect = Math.Max(0, player.GetInt32(UpdateFields.UnitFieldStat0 + IntellectStat));
        return SpellCritTable.CritFromIntellect(player.Class, player.Level, (uint)intellect);
    }
}
