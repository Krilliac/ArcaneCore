using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Death;

namespace ArcaneCore.Game.Spells;

public sealed record TradeEnchantmentPlan(ItemInstanceData UpdatedRecipientItem,
    IReadOnlyList<InventoryRewardGrant> Reagents,
    CharacterLife? CasterLifeAfter = null,
    uint PowerCost = 0,
    SpellInfo? SpellSnapshot = null);

public sealed partial class SpellSystem
{
    /// <summary>Pure acceptance-stage plan for a server-resolved foreign trade item.</summary>
    public SpellCastResult TryPlanTradeEnchantment(Player caster, Player recipient, Item target,
        uint spellId, ObjectGuid castItemGuid, out TradeEnchantmentPlan? plan, bool acceptance = false)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(target);
        plan = null;
        if (!ReferenceEquals(target.Inventory, recipient.Inventory) || target.OwnerGuid != recipient.Guid
            || caster.IsQuestSettlementPending || recipient.IsQuestSettlementPending)
            return SpellCastResult.ItemNotReady;
        if (Store.Get(spellId) is not { } spell || spell.IsPassive
            || Spellbook is { } book && !book.HasSpell(caster, spellId))
            return SpellCastResult.NotKnown;
        if (castItemGuid != default)
            return SpellCastResult.ItemNotReady;
        uint powerCost = CalculatePowerCost(caster, spell);
        CharacterLife capturedLife = PlayerLife.Capture(caster);
        CharacterLife? lifeAfter = null;
        CharacterLife life = capturedLife;
        uint[] powers = [.. life.Powers];
        if (spell.PowerType == SpellMath.PowerHealth)
        {
            if (powerCost > life.Health) return SpellCastResult.CasterAurastate;
            lifeAfter = life with { Health = life.Health - powerCost, Powers = Array.AsReadOnly(life.Powers.ToArray()) };
        }
        else if (spell.PowerType is >= 0 and <= (int)PowerType.Happiness)
        {
            int powerIndex = spell.PowerType;
            if (powerIndex >= powers.Length || powerCost > powers[powerIndex]) return SpellCastResult.CasterAurastate;
            powers[powerIndex] -= powerCost;
            lifeAfter = life with { Powers = Array.AsReadOnly(powers) };
        }
        else if (powerCost != 0)
        {
            return SpellCastResult.ItemNotReady;
        }

        SpellEffectInfo? enchant = null;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty) continue;
            if (effect.Effect is not (SpellEffectName.EnchantItem or SpellEffectName.EnchantItemTemporary) || enchant is not null)
                return SpellCastResult.ItemNotReady;
            enchant = effect;
        }
        if (enchant is null || ItemEnchantments.Find((uint)enchant.MiscValue) is null)
            return SpellCastResult.ItemNotReady;
        uint duration = 0;
        uint charges = 0;
        int slot = enchant.Effect == SpellEffectName.EnchantItem ? 0 : 1;
        if (slot == 1)
        {
            // A plan must not advance the live RNG. Fixed enchant values use the
            // same level/formula/modifier path as ordinary effects; randomized
            // durations need a later acceptance-only roll seam.
            if (enchant.DieSides is not (0 or 1) || enchant.DicePerLevel != 0)
                return SpellCastResult.ItemNotReady;
            int index = spell.Effects.ToList().IndexOf(enchant);
            int value = ModifyValue(SpellValueKind.EffectValue, caster, spell, index,
                spell.CalculateEffectValue(index, caster.Level, new Random(0)), target: null);
            long milliseconds = Math.Max(0, (long)value) * 1000L;
            if (milliseconds > uint.MaxValue) return SpellCastResult.ItemNotReady;
            duration = (uint)milliseconds;
            charges = SpellEnchantCharges.Find(spell.Id) ?? 0;
        }

        if (!CollectReagents(spell, out IReadOnlyList<InventoryRewardGrant> reagents))
            return SpellCastResult.ItemNotReady;
        Item? castItem = castItemGuid == default ? null : caster.Inventory.GetItemByGuid(castItemGuid);
        if (castItem is not null)
            reagents = reagents.Select(r => r.Entry == castItem.Entry
                ? new InventoryRewardGrant(r.Entry, RequiredReagentCount(r, castItem)) : r).ToArray();
        foreach (InventoryRewardGrant reagent in reagents)
            if (caster.Inventory.GetItemCount(reagent.Entry) < reagent.Count) return SpellCastResult.ItemNotReady;

        SpellCastTargets targets = new()
        {
            Mask = SpellCastTargetFlags.TradeItem,
            Item = new ObjectGuid(6),
            ServerValidatedTradeItem = target,
        };
        UnitSpellState state = GetOrCreateState(caster);
        SpellCastResult check = CheckCast(state, spell, targets, unitTarget: null, triggered: acceptance,
            strict: true, castItem: null);
        if (check != SpellCastResult.CastOk)
            return check;

        uint[] enchantments = new uint[Item.EnchantmentValues];
        IReadOnlyList<uint> existing = target.ToData().Enchantments;
        for (int i = 0; i < enchantments.Length && i < existing.Count; i++) enchantments[i] = existing[i];
        int offset = slot * 3;
        enchantments[offset] = (uint)enchant.MiscValue;
        enchantments[offset + 1] = duration;
        enchantments[offset + 2] = charges;
        plan = new TradeEnchantmentPlan(target.ToData() with { Enchantments = Array.AsReadOnly(enchantments) },
            Array.AsReadOnly(reagents.ToArray()), powerCost == 0 ? null : lifeAfter, powerCost, spell with { });
        return SpellCastResult.CastOk;
    }

}
