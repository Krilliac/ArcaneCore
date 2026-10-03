using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Cast a spell from an item the player holds (vmangos Player::CastItemUseSpell, Objects/Player.cpp:22139-22214, which builds a
    /// <c>Spell</c> with <c>m_CastItem</c> set and calls <c>prepare</c>). The cast is not triggered, so range, cooldown and
    /// reagent checks apply; the item reaches every check, cost taker and effect handler as <see cref="SpellCast.CastItem"/>.
    /// The use-item rules (slot, trigger type, charges, cooldown category) belong to the CMSG_USE_ITEM handler, not here.
    /// </summary>
    public SpellCastResult CastItemSpell(Player player, Item item, uint spellId, SpellCastTargets targets)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        return spell is null ? SpellCastResult.NotFound : Prepare(player, spell, targets, triggered: false, castItem: item);
    }
}
