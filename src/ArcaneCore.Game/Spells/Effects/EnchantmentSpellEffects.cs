using ArcaneCore.Game.Items;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Items;

namespace ArcaneCore.Game.Spells.Effects;

public sealed class EnchantmentSpellEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.RegisterEffectCheck(SpellEffectName.EnchantItem, EnchantItemSpellRules.Check);
        system.RegisterEffectCheck(SpellEffectName.EnchantItemTemporary, EnchantItemSpellRules.Check);
        system.RegisterEffect(SpellEffectName.EnchantItem, static context => Apply(context, 0, 0));
        system.RegisterEffect(SpellEffectName.EnchantItemTemporary, static context =>
        {
            long duration = context.Value <= 0 ? 0 : (long)context.Value * 1000L;
            if (duration > uint.MaxValue) return;
            uint charges = context.System.SpellEnchantCharges.Find(context.Spell.Id) ?? 0;
            Apply(context, 1, (uint)duration, charges);
        });
    }

    private static void Apply(SpellEffectContext context, int slot, uint durationMs, uint charges = 0)
    {
        if (context.Caster is not Player player || EnchantItemSpellRules.ResolveItem(context) is not { } item
            || player.Inventory.EnchantmentSink is not { } sink)
            return;
        sink.ApplyEnchantment(player, item, slot, apply: false);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + (slot * 3), (uint)context.Effect.MiscValue);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + (slot * 3) + 1, durationMs);
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + (slot * 3) + 2, charges);
        sink.ApplyEnchantment(player, item, slot, apply: true);
    }
}
