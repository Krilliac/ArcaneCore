using ArcaneCore.Game.Crafting.Enchanting;

namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    /// <summary>
    /// The player's enchantment engine (null until the enchanting feature attaches it during login, see docs/areas/crafting.md). World-thread
    /// owned once the player is in a map.
    /// </summary>
    public PlayerEnchantments? Enchantments { get; private set; }

    /// <summary>Attach the enchantment engine once, before the player is handed to the world thread.</summary>
    public void AttachEnchantments(PlayerEnchantments enchantments)
    {
        ArgumentNullException.ThrowIfNull(enchantments);
        if (Enchantments is not null)
        {
            throw new InvalidOperationException("enchantments are already attached to this player");
        }

        Enchantments = enchantments;
    }
}
