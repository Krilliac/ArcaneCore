using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// Consume an accepted offer after relocation (vmangos Player::ResurrectUsingRequestData /
    /// DELAYED_RESURRECT_PLAYER): clamp captured health/mana to current maxima, rage 0, full energy,
    /// clear ghost/root, remove the body through its owning map. No resurrection sickness.
    /// </summary>
    public bool ResurrectFromRequest(Player player, ResurrectionRequest request)
    {
        if (player.IsAlive || player.IsQuestSettlementPending || !ReferenceEquals(player.Map, _map)
            || !request.Accepted || !ReferenceEquals(PlayerResurrection.GetRequest(player), request))
        {
            return false;
        }

        ResurrectPlayer(player, 0, applySickness: false);
        player.Health = Math.Max(1u, Math.Min(player.MaxHealth, request.Health));
        SetPower(player, PowerType.Mana, request.Mana);
        SetPower(player, PowerType.Rage, 0);
        SetPower(player, PowerType.Energy, GetMaxPower(player, PowerType.Energy));
        if (player.Combat.Corpse is { } corpse)
        {
            RemoveCorpse(corpse);
            player.Combat.Corpse = null;
        }

        return true;
    }
}
