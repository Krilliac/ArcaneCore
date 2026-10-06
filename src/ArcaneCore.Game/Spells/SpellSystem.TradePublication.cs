using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private sealed record TradePublicationReceipt(Guid OperationId);
    private readonly ConditionalWeakTable<TradeEnchantmentPlan, TradePublicationReceipt> _tradePublications = new();

    /// <summary>Publish a triggered cast after durable costs and enchant fields are committed.
    /// No effect or cost executes again; duplicate callbacks for the same plan are ignored.
    /// This is live publication, not a durable crash-recovery journal.</summary>
    public bool PublishCommittedTradeEnchantment(Guid operationId, Player caster, Item item, TradeEnchantmentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(plan);
        if (operationId == Guid.Empty || plan.SpellSnapshot is not { } spell
            || item.Guid.Low != plan.UpdatedRecipientItem.Guid || !caster.IsInWorld
            || item.Inventory?.Player is not { IsInWorld: true } recipient || item.OwnerGuid != recipient.Guid
            || _tradePublications.TryGetValue(plan, out _)) return false;

        if (plan.ItemCast is { } itemCast)
        {
            _tradePublications.Add(plan, new TradePublicationReceipt(operationId));

            // A deferred item use has already applied its staged inventory payment and
            // effects. Publish the same provenance as an ordinary item cast: source item
            // GUID, owner as caster, real recipient item in the TradeItem target, and the
            // selected template cooldown metadata. Item casts have no power or GCD payment.
            var itemTargets = new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = item.Guid };
            ObjectGuid castItemOrCaster = itemCast.CastItemReagentPaymentWaived ? caster.Guid : itemCast.CastItemGuid;
            AddCooldown(GetOrCreateState(caster), spell, triggered: true,
                itemCooldownMs: itemCast.ItemSpell.Cooldown,
                itemCategoryCooldownMs: itemCast.ItemSpell.CategoryCooldown,
                itemCategory: itemCast.ItemSpell.Category == 0 ? null : itemCast.ItemSpell.Category,
                itemId: itemCast.CastItemEntry);
            SendToSet(caster, WorldOpcode.SmsgSpellGo, SpellPackets.BuildSpellGo(castItemOrCaster, caster.Guid, spell.Id,
                SpellCastFlags.Unknown9, [], [], itemTargets), includeSelf: true);
            return true;
        }

        _tradePublications.Add(plan, new TradePublicationReceipt(operationId));

        // vmangos Spell.cpp3679 updateTradeSlotItem: request slot6 becomes the
        // actual item GUID before SpellGo; the TRADE_ITEM mask remains set.
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.TradeItem, Item = item.Guid };
        AddCooldown(GetOrCreateState(caster), spell, triggered: true);
        if (plan.PowerCost > 0 && spell.PowerType == (int)PowerType.Mana
            && ((uint)spell.AttributesEx2 & Casters.CasterAttributes.Ex2DontBlockManaRegen) == 0)
            caster.Combat.NoteManaUsed(spell.Id);
        SendToSet(caster, WorldOpcode.SmsgSpellGo, SpellPackets.BuildSpellGo(caster.Guid, caster.Guid, spell.Id,
            SpellCastFlags.Unknown9, [], [], targets), includeSelf: true);
        return true;
    }
}
