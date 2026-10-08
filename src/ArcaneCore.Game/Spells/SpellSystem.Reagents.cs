using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Whether a player has offered an item in an open trade (the economy owns trades; set with the crafting feature's reagent check,
    /// <c>ReagentRules.Install</c>). Offered items neither count as reagents nor are taken (vmangos <c>Player::HasItemCount</c> and
    /// <c>DestroyItemCount</c> skip <c>IsInTrade</c>, Player.cpp:8681-8734). Null: nothing is offered.
    /// </summary>
    public Func<Player, Item, bool>? ReagentTradeFilter { get; set; }

    private Func<Item, bool>? TradedReagentFilter(Player player)
        => ReagentTradeFilter is { } filter ? item => filter(player, item) : null;

    internal bool IgnoresReagents(Unit caster, SpellCastTargets targets, bool triggered, SpellInfo? triggeringSpell)
    {
        if (caster is not Player || _objectCastDepth != 0) return true;
        // Spell.cpp:7070-7081: standalone server triggers and children whose
        // master carries reagent slot zero do not pay again. A triggered spell
        // on a foreign trade item still requires its own reagents.
        return triggered && !targets.Mask.HasFlag(SpellCastTargetFlags.TradeItem)
            && (triggeringSpell is null || triggeringSpell.Reagents[0].Item != 0);
    }

    private static bool CollectReagents(SpellInfo spell, out IReadOnlyList<InventoryRewardGrant> requirements)
    {
        var counts = new Dictionary<uint, ulong>();
        foreach (SpellReagent reagent in spell.Reagents)
        {
            if (!reagent.IsPresent || reagent.Count == 0) continue;
            uint entry = (uint)reagent.Item;
            ulong count = counts.GetValueOrDefault(entry) + reagent.Count;
            // Duplicate or corrupt content cannot overflow a count or cause a
            // partially paid cast. Inventory objective notifications use int.
            if (count > int.MaxValue)
            {
                requirements = [];
                return false;
            }
            counts[entry] = count;
        }
        requirements = counts.Select(p => new InventoryRewardGrant(p.Key, (uint)p.Value)).ToArray();
        return true;
    }

    private SpellCastResult CheckReagents(Unit caster, SpellInfo spell, SpellCastTargets targets, bool triggered, SpellInfo? triggeringSpell,
        Item? castItem = null)
    {
        if (IgnoresReagents(caster, targets, triggered, triggeringSpell)) return SpellCastResult.CastOk;
        if (!CollectReagents(spell, out IReadOnlyList<InventoryRewardGrant> requirements)) return SpellCastResult.ItemNotReady;
        // Spell.cpp:7249-7279 checks carried counts before cast start and again
        // at completion. Ankh in the bank therefore cannot pay for Reincarnation.
        var player = (Player)caster;
        Func<Item, bool>? traded = TradedReagentFilter(player);
        return requirements.All(r => player.Inventory.GetItemCount(r.Entry, exclude: traded) >= RequiredReagentCount(r, castItem))
            ? SpellCastResult.CastOk : SpellCastResult.ItemNotReady;
    }

    private static uint RequiredReagentCount(InventoryRewardGrant requirement, Item? castItem)
    {
        // Spell.cpp:7249-7279: when the cast item is also a reagent and is an
        // expendable item on its last charge, retain one extra reagent for the
        // cast-item deletion that follows TakeReagents.
        if (castItem is null || castItem.Entry != requirement.Entry || requirement.Count <= 1)
        {
            return requirement.Count;
        }

        for (int index = 0; index < castItem.Template.Spells.Count && index < Item.SpellChargeSlots; index++)
        {
            ItemSpell spell = castItem.Template.Spells[index];
            int charges = castItem.GetInt32(UpdateFields.ItemFieldSpellCharges + index);
            if (spell.Charges < 0 && charges > -2 && charges < 2)
            {
                return checked(requirement.Count + 1);
            }
        }

        return requirement.Count;
    }

    private SpellCastResult StageCastReagents(SpellCast cast, out InventoryRewardStage? stage)
    {
        stage = null;
        if (IgnoresReagents(cast.Caster, cast.Targets, cast.IsTriggered, cast.TriggeringSpell)) return SpellCastResult.CastOk;
        if (!CollectReagents(cast.Spell, out IReadOnlyList<InventoryRewardGrant> requirements)) return SpellCastResult.ItemNotReady;
        if (cast.CastItem is { } castItem)
        {
            requirements = requirements.Select(r => r.Entry == castItem.Entry
                ? new InventoryRewardGrant(r.Entry, RequiredReagentCount(r, castItem)) : r).ToArray();
        }
        if (requirements.Count == 0) return SpellCastResult.CastOk;

        // Reuse the existing detached inventory operation with no grants.
        // Validate the entire batch before power/cooldown/effects; callbacks see
        // all removals applied, never a half-consumed set of reagent stacks.
        var player = (Player)cast.Caster;
        IReadOnlySet<ObjectGuid>? offered = TradedReagentFilter(player) is { } traded
            ? player.Inventory.AllItems.Where(traded).Select(item => item.Guid).ToHashSet()
            : null;
        if (player.Inventory.TryStageQuestRewards([], requirements, out stage, out _, offered) != InventoryResult.Ok || stage is null)
        {
            return SpellCastResult.ItemNotReady;
        }

        // The detached inventory has no live combat/logout/disarm state.
        // DestroyItemCount's source rule checks CanUnequipItem before either
        // a full removal or a stack decrement (PlayerInventory.Storage.cs).
        IEnumerable<Item> consumed = stage.Removed.Concat(stage.Stacks
            .Where(s => s.Count < s.Existing.Count).Select(s => s.Existing));
        if (consumed.Any(item => player.Inventory.CanUnequipItem(item.BagSlot, item.Slot, swap: false) != InventoryResult.Ok))
        {
            stage = null;
            return SpellCastResult.ItemNotReady;
        }
        return SpellCastResult.CastOk;
    }
}
