using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Items.ItemUse;

/// <summary>The category and recovery times that apply to one cast (Spell.dbc values, or the item's own for an item spell).</summary>
public readonly record struct ItemSpellCooldown(uint Category, uint RecoveryTime, uint CategoryRecoveryTime);

/// <summary>
/// The cooldown an item spell really uses (vmangos Player::AddCooldown <c>pickCooldowns</c>, Objects/Player.cpp:22139-22160, and
/// the matching readiness test): the item's <c>spellcategory_N</c> replaces the spell's category when set, and its
/// <c>spellcooldown_N</c> / <c>spellcategorycooldown_N</c> replace the spell's recovery times when not negative.
/// </summary>
public static class ItemSpellCooldowns
{
    /// <summary>The cooldown of <paramref name="spell"/> cast from <paramref name="castItem"/> (null: the spell's own values).</summary>
    public static ItemSpellCooldown Pick(SpellInfo spell, Item? castItem)
    {
        ArgumentNullException.ThrowIfNull(spell);
        var result = new ItemSpellCooldown(spell.Category, spell.RecoveryTime, spell.CategoryRecoveryTime);
        if (castItem is null)
        {
            return result;
        }

        foreach (Kernel.Items.ItemSpell itemSpell in castItem.Template.Spells)
        {
            if (itemSpell.SpellId != spell.Id)
            {
                continue;
            }

            if (itemSpell.Category != 0)
            {
                result = result with { Category = itemSpell.Category };
            }

            if (itemSpell.Cooldown >= 0)
            {
                result = result with { RecoveryTime = (uint)itemSpell.Cooldown };
            }

            if (itemSpell.CategoryCooldown >= 0)
            {
                result = result with { CategoryRecoveryTime = (uint)itemSpell.CategoryCooldown };
            }

            break;
        }

        return result;
    }
}
