using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Raised on the world thread after SPELL_EFFECT_SPIRIT_HEAL resurrected a player (<see cref="SpiritHealEffect"/>); the pets area
    /// brings the player's pet back (vmangos Player::AutoReSummonPet, the last call of Spell::EffectSpiritHeal).
    /// </summary>
    public event Action<Player>? PlayerSpiritHealed;

    internal void NotifySpiritHealed(Player player) => PlayerSpiritHealed?.Invoke(player);
}
