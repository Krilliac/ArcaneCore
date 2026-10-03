using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// The 10% durability loss of a player's death (vmangos Unit::Kill, Unit.cpp:1190-1202): worn items only
    /// (<c>DurabilityLossAll(0.10f, false)</c>) and an empty SMSG_DURABILITY_DAMAGE_DEATH. It does not apply when a player tapped
    /// the kill (the killer is a player, or a unit that acts for one: <c>pPlayerTap</c>, which for a self kill is the victim
    /// itself, so the environmental deaths, that take their own loss in Player::EnvironmentalDamage, do not lose it twice), nor
    /// in a battleground. vmangos also skips it for a spell with SPELL_ATTR_EX3_NO_DURABILITY_LOSS; <see cref="Kill"/> is not
    /// told which spell dealt the blow, so that exception is not modelled (docs/areas/graveyards-resurrection.md, limits).
    /// </summary>
    private void ApplyDeathDurabilityLoss(Player victim, Unit? killer)
    {
        if (killer is not null && DuelRules.ControllingPlayer(killer) is not null)
        {
            return;
        }

        if (Maps.Templates.WorldMaps.Of(_world).Registry.Find(victim.MapId) is { IsBattleground: true })
        {
            return;
        }

        victim.Inventory.DurabilityLossAll(0.10, inventory: false);
        victim.Session.Send(WorldOpcode.SmsgDurabilityDamageDeath, []);
    }
}
