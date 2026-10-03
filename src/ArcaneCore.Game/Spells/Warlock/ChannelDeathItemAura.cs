using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// SPELL_AURA_CHANNEL_DEATH_ITEM (86, vmangos Aura::HandleChannelDeathItem, SpellAuras.cpp:2833-2880): Drain Soul and Shadowburn make a
/// Soul Shard (or the effect's item) when the aura ends because its target died, never when it expires or is dispelled.
/// <list type="bullet">
/// <item>The caster must be a player in the world (a gone or non-player caster makes nothing) and the effect value is the count.</item>
/// <item>Warlock spells make one item per warlock and target: if another channel-death-item aura of the same caster is still on the victim
/// the later one makes it (Shadowburn together with Drain Soul gives one shard, two warlocks give one each).</item>
/// <item>Soul Shards need a victim that is an honor or XP target (<see cref="SoulShardRules.IsHonorOrXpTarget"/>) and, for a creature, one the
/// caster tapped (<see cref="ISoulShardTapSource"/>; without a source every kill counts as tapped).</item>
/// <item>No room: the equip error is sent even when a part fits (vmangos), the part that fits is created, nothing when none fits.</item>
/// </list>
/// </summary>
public sealed class ChannelDeathItemAura : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ChannelDeathItem, new AuraHandler(Apply: OnApply, Tick: null));
    }

    private static void OnApply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (apply || !holder.RemovedByDeath || aura.Amount <= 0)
        {
            return;
        }

        uint itemEntry = holder.Spell.Effects[aura.EffectIndex].ItemType;
        if (itemEntry == 0 || system.AuraCaster(holder) is not Player caster)
        {
            return;
        }

        Unit victim = holder.Target;
        if (holder.Spell.SpellFamilyName == SoulShardRules.WarlockFamily
            && system.GetAuras(victim).Any(h => !h.IsRemoved && h.CasterGuid == holder.CasterGuid && h.HasAura(AuraType.ChannelDeathItem)))
        {
            return; // another channel-death-item aura of this warlock will make the item (HasAuraTypeByCaster)
        }

        if (itemEntry == SoulShardRules.SoulShard
            && (!SoulShardRules.IsHonorOrXpTarget(caster, victim)
                || (victim is Creature creature && SoulShardRules.TapSourceOf(system) is { } tap && !tap.IsTappedBy(creature, caster))))
        {
            return;
        }

        PlayerInventory inventory = caster.Inventory;
        if (inventory.Templates.Find(itemEntry) is not { } template)
        {
            return;
        }

        uint count = (uint)aura.Amount;
        var dest = new List<ItemPosCount>();
        InventoryResult msg = inventory.CanStoreNewItem(itemEntry, count, dest, out uint noSpace);
        if (msg != InventoryResult.Ok)
        {
            count = count > noSpace ? count - noSpace : 0;
            inventory.SendEquipError(msg, null, null, 0, itemEntry);
            if (count == 0 || dest.Count == 0)
            {
                return;
            }
        }

        Item item = inventory.StoreNewItem(dest, template, count);
        if (caster.IsInWorld)
        {
            caster.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(caster.Guid, item, count, received: true, created: true, showInChat: true));
        }
    }
}
