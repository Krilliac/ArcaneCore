using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// The line-of-sight part of vmangos <c>Spell::CheckCast</c>: a non-triggered spell without
/// SPELL_ATTR_EX2_IGNORE_LOS fails with SPELL_FAILED_LINE_OF_SIGHT when a static model blocks the
/// view from the caster to its unit target or destination. Without vmap data nothing blocks.
/// Called from <see cref="SpellSystem"/>'s cast check (one line, docs/integration/vmap-los.md).
/// </summary>
public static class SpellLineOfSight
{
    /// <summary>vmangos/cmangos <c>SPELL_ATTR_EX2_IGNORE_LOS</c>.</summary>
    public const SpellAttributesEx2 IgnoreLineOfSight = (SpellAttributesEx2)0x00000004;

    /// <summary>CAST_OK, or LINE_OF_SIGHT when the caster cannot see <paramref name="target"/>.</summary>
    public static SpellCastResult Check(Unit caster, SpellInfo spell, Unit target, bool triggered)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(target);
        if (triggered || ReferenceEquals(caster, target) || spell.HasAttribute(IgnoreLineOfSight) || caster.Map is not { } map)
        {
            return SpellCastResult.CastOk;
        }

        return map.Collision.IsWithinLineOfSight(caster, target) ? SpellCastResult.CastOk : SpellCastResult.LineOfSight;
    }

    /// <summary>CAST_OK, or LINE_OF_SIGHT when the caster cannot see the destination point.</summary>
    public static SpellCastResult CheckDest(Unit caster, SpellInfo spell, float x, float y, float z, bool triggered)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (triggered || spell.HasAttribute(IgnoreLineOfSight) || caster.Map is not { } map)
        {
            return SpellCastResult.CastOk;
        }

        return map.Collision.IsWithinLineOfSight(caster, x, y, z) ? SpellCastResult.CastOk : SpellCastResult.LineOfSight;
    }
}
