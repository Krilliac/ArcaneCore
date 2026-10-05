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
