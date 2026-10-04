using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Items.ItemUse;

/// <summary>
/// The cast-item part of vmangos <c>Spell::CheckItems</c> (Spells/Spell.cpp:7109-7175): the first block of the item checks, so it runs before the
/// item-target fit, the spell focus and the reagents (<see cref="SpellCastCheckOrder.Equipment"/> + 10; the equipped item requirement at
/// Equipment itself comes first, as in the source).
/// <list type="number">
/// <item>The item sits in an open trade window: <see cref="SpellCastResult.ItemGone"/>.</item>
/// <item>The player no longer has the item: <see cref="SpellCastResult.ItemNotReady"/>.</item>
/// <item>A limited-charge spell with no charge left: <see cref="SpellCastResult.NoChargesRemain"/>.</item>
/// <item>
/// The 1.11 rejuvenation rule (client patch 1.11.0, guarded <c>SUPPORTED_CLIENT_BUILD &gt; 1_10_2</c>): a consumable cast at a unit is refused
/// only when NO effect is usable, a heal needs missing health and an energize missing power of its type. The loop keeps the last refusal
/// reason it saw and ends successfully at the first usable effect.
/// </item>
/// </list>
/// </summary>
/// <param name="isInTrade">Whether a player has offered an item in an open trade (the economy feature owns trades); null means never.</param>
public sealed class CastItemCheck(Func<Player, Item, bool>? isInTrade = null) : ISpellCastCheck
{
    public const int CastItemOrder = SpellCastCheckOrder.Equipment + 10;

    private const uint ItemClassConsumable = 0;

    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => CastItemOrder;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.CastItem is not { } item || context.Caster is not Player player)
        {
            return SpellCastResult.CastOk;
        }

        if (isInTrade?.Invoke(player, item) == true)
        {
            return SpellCastResult.ItemGone;
        }

        if (player.Inventory.GetItemCount(item.Entry) < 1)
        {
            return SpellCastResult.ItemNotReady;
        }

        if (ItemSpellCharges.HasNoChargesLeft(item))
        {
            return SpellCastResult.NoChargesRemain;
        }

        if (item.Template.Class == ItemClassConsumable && UnitTargetOf(context) is { } target)
        {
            return ConsumableRefusal(context.Spell, target);
        }

        return SpellCastResult.CastOk;
    }

    /// <summary>vmangos <c>m_targets.getUnitTarget()</c> after PrepareForSpellSystem: the named unit, or the caster for a self cast.</summary>
    private static Unit? UnitTargetOf(in SpellCastCheckContext context)
    {
        if (context.Target is { } target)
        {
            return target;
        }

        if ((context.Targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) != 0)
        {
            return context.System.Units.Find(context.Caster, context.Targets.Unit);
        }

        return context.Targets.Mask == SpellCastTargetFlags.Self ? context.Caster : null;
    }

    /// <summary>Spell.cpp:7131-7165.</summary>
    internal static SpellCastResult ConsumableRefusal(SpellInfo spell, Unit target)
    {
        SpellCastResult failReason = SpellCastResult.CastOk;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            // Pet-targeted effects are skipped: for them the unit target is the caster, not the real target (Spell.cpp:7138-7141).
            if (effect.TargetA == SpellImplicitTarget.UnitCasterPet)
            {
                continue;
            }

            if (effect.Effect == SpellEffectName.Heal)
            {
                if (target.Health == target.MaxHealth)
                {
                    failReason = SpellCastResult.AlreadyAtFullHealth;
                    continue;
                }

                failReason = SpellCastResult.CastOk;
                break;
            }

            // Mana Potion, Rage Potion, Thistle Tea ...
            if (effect.Effect == SpellEffectName.Energize)
            {
                if (effect.MiscValue < 0 || effect.MiscValue > (int)PowerType.Happiness)
                {
                    failReason = SpellCastResult.AlreadyAtFullPower;
                    continue;
                }

                var power = (PowerType)effect.MiscValue;
                if (SpellSystem.GetPower(target, power) == target.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power))
                {
                    failReason = SpellCastResult.AlreadyAtFullPower;
                    continue;
                }

                failReason = SpellCastResult.CastOk;
                break;
            }
        }

        return failReason;
    }
}
