using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>Per-current-spell consumable resource check, matching vmangos Spell::CheckItems.</summary>
    internal SpellCastResult CheckItemResources(Player player, Item item, SpellInfo spell,
        SpellCastTargets targets, Unit? target, bool triggered)
    {
        if ((ItemClass)item.Template.Class != ItemClass.Consumable || target is null)
        {
            return SpellCastResult.CastOk;
        }

        SpellCastResult failure = SpellCastResult.CastOk;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            // vmangos skips TARGET_UNIT_CASTER_PET in this gate.
            if (effect.TargetA == SpellImplicitTarget.UnitCasterPet) continue;
            if (effect.Effect == SpellEffectName.Heal)
            {
                failure = SpellCastResult.AlreadyAtFullHealth;
                if (target.Health < target.GetUInt32(UpdateFields.UnitFieldMaxhealth)) return SpellCastResult.CastOk;
            }
            else if (effect.Effect == SpellEffectName.Energize)
            {
                failure = SpellCastResult.AlreadyAtFullPower;
                if (effect.MiscValue is >= 0 and <= (int)PowerType.Happiness)
                {
                    int power = effect.MiscValue;
                    if (target.GetUInt32(UpdateFields.UnitFieldPower1 + power) < target.GetUInt32(UpdateFields.UnitFieldMaxpower1 + power)) return SpellCastResult.CastOk;
                }
            }
        }

        return failure;
    }
}
