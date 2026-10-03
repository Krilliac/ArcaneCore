using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Cast a spell from an item the player holds (vmangos Player::CastItemUseSpell, Objects/Player.cpp:7362-7396, which builds a
    /// <c>Spell</c> with <c>m_CastItem</c> set and calls <c>prepare</c>). Unless <paramref name="triggered"/> (the second and later spell of an
    /// item with several), the cast is not triggered, so range, cooldown and reagent checks apply; the item reaches every check, cost taker
    /// and effect handler as <see cref="SpellCast.CastItem"/>. A spell cast from an item takes no power (Spell::TakePower, Spell.cpp:5053).
    /// The use-item rules (slot, trigger type, equipped-only, bind, combat) belong to <see cref="ItemUseService"/>, not here.
    /// </summary>
    public SpellCastResult CastItemSpell(Player player, Item item, uint spellId, SpellCastTargets targets, bool triggered = false)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        return spell is null ? SpellCastResult.NotFound : Prepare(player, spell, targets, triggered, castItem: item);
    }

    /// <summary>Spell::TakeCastItem (Spell.cpp:4991-5048): charges and the destroy of a spent expendable item, see <see cref="ItemSpellCharges"/>.</summary>
    private static void TakeCastItem(SpellCast cast) => ItemSpellCharges.TakeCastItem(cast);
}
