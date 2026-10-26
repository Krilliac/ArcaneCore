using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>The forced duration of the equip cooldown (vmangos Player::ApplyEquipCooldown: <c>30 * IN_MILLISECONDS</c>, Player.cpp:19253).</summary>
    public const uint EquipCooldownMs = 30_000;

    /// <summary>
    /// vmangos Player::ApplyEquipCooldown (Player.cpp:19235-19261; mangos-classic Player.cpp:18235-18261), the last step of Player::EquipItem for
    /// the item put on or the worn stack it merged into (:10380, :10404): unless the template has ITEM_FLAG_NO_EQUIP_COOLDOWN (ItemPrototype.h:71),
    /// every on-use spell of the item that the spell store knows gets the equip cooldown (<see cref="AddEquipCooldown"/>) and the client is told
    /// with SMSG_ITEM_COOLDOWN, also when an older cooldown was kept. Alive or dead, in combat or not. Called by the equip binding
    /// (<see cref="ItemEquipSpells"/>) once the item's create block is queued; the queued blocks go out first so the client knows the item. World thread.
    /// </summary>
    public void ApplyEquipCooldown(Player player, Item item)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if ((item.Template.Flags & (uint)ItemTemplateFlags.NoEquipCooldown) != 0)
        {
            return;
        }

        foreach (ItemSpell itemSpell in item.Template.Spells)
        {
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != ItemSpellTriggers.OnUse || Store.Get(itemSpell.SpellId) is not { } spell)
            {
                continue;
            }

            AddEquipCooldown(GetOrCreateState(player), spell);
            if (player.IsInWorld)
            {
                player.PendingUpdates.FlushTo(player.PendingUpdatesSend, 0);
            }

            player.Session.Send(WorldOpcode.SmsgItemCooldown, SpellPackets.BuildItemCooldown(item.Guid, spell.Id));
        }
    }

    /// <summary>
    /// vmangos Player::AddCooldown(spell, nullptr, false, 30000) (Player.cpp:22139-22243): an unexpired cooldown of the spell, its own time or a
    /// category time it started, is kept unchanged (:22172-22178). Otherwise the spell's recovery is the forced 30 s through the cooldown spell
    /// mods (:22200-22206), and its Spell.dbc category starts with the Spell.dbc category time: no item prototype is passed, so the item
    /// template's category fields are not read and the cooldown is not item-owned (itemId 0). No no-cooldown cheat check: vmangos makes that
    /// in Spell::SendSpellCooldown, which this path does not go through.
    /// </summary>
    private void AddEquipCooldown(UnitSpellState state, SpellInfo spell)
    {
        uint now = NowMs;
        if (state.SpellCooldowns.TryGetValue(spell.Id, out uint until) && IsCooldownRunning(until, now))
        {
            return;
        }

        foreach ((uint ownedCategory, uint owner) in state.CategoryOwners)
        {
            if (owner == spell.Id && state.CategoryCooldowns.TryGetValue(ownedCategory, out until) && IsCooldownRunning(until, now))
            {
                return;
            }
        }

        uint recovery = (uint)Math.Max(ModInt(state.Unit, spell, SpellModOp.Cooldown, (int)EquipCooldownMs), 0);
        if (recovery > 0)
        {
            state.SpellCooldowns[spell.Id] = now + ClampCooldownSpan(recovery);
        }

        uint category = spell.Category;
        if (category != 0 && spell.CategoryRecoveryTime > 0)
        {
            // vmangos CooldownMap::AddCooldown: a running category cooldown keeps its original owner and is only extended.
            if (!state.CategoryOwners.ContainsKey(category) || !state.CategoryCooldowns.TryGetValue(category, out uint running) || !IsCooldownRunning(running, now))
            {
                state.CategoryOwners[category] = spell.Id;
            }

            state.CategoryCooldowns[category] = now + ClampCooldownSpan(spell.CategoryRecoveryTime);
        }

        state.CooldownOwners.Remove(spell.Id);
    }
}
