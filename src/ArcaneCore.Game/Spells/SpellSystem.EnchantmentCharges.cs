using ArcaneCore.Kernel.WorldData.Items;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>Immutable startup spell_enchant_charges content; zero is unlimited.</summary>
    public ISpellEnchantChargesCatalog SpellEnchantCharges { get; set; } = new SpellEnchantChargesCatalog();
}
