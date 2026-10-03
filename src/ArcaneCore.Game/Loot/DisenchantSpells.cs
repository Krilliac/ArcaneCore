using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Registers Disenchant (<see cref="SpellEffectName.Disenchant"/>, effect 99) on a spell system: the cast check of
/// <see cref="DisenchantLoot.CheckTarget"/> (vmangos Spell.cpp:7376-7392) and the effect (SpellEffects.cpp:5059-5073) on the item named in the
/// cast's target block. The per-map state is <see cref="DisenchantLoot"/>; this class only routes by map. Install once per spell system.
/// </summary>
public sealed class DisenchantSpells(Func<Map, DisenchantLoot?> disenchantOf)
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system.HasEffectHandler(SpellEffectName.Disenchant))
        {
            throw new InvalidOperationException("the Disenchant effect already has a handler; installing a second one would replace it");
        }

        system.RegisterEffectCheck(SpellEffectName.Disenchant, CheckTarget);
        system.RegisterEffect(SpellEffectName.Disenchant, Effect);
    }

    private static Item? ItemOf(Player player, SpellCastTargets targets)
        => (targets.Mask & (SpellCastTargetFlags.Item | SpellCastTargetFlags.TradeItem)) != 0 && !targets.Item.IsEmpty
            ? player.Inventory.GetItemByGuid(targets.Item)
            : null;

    private SpellCastResult CheckTarget(SpellEffectCheckContext context)
        => context.Caster is Player player && player.Map is { } map && disenchantOf(map) is { } disenchant
            ? disenchant.CheckTarget(player, ItemOf(player, context.Targets))
            : SpellCastResult.CantBeDisenchanted;

    private void Effect(SpellEffectContext context)
    {
        if (context.Caster is Player player && player.Map is { } map && ItemOf(player, context.Cast.Targets) is { } item)
        {
            disenchantOf(map)?.Disenchant(player, item, context.Spell.Id);
        }
    }
}