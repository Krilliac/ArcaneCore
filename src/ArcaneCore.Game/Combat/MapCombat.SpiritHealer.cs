using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// The death-state half of vmangos WorldSession::SendSpiritResurrect: a ghost is resurrected
    /// at 50% (Player::ResurrectPlayer(0.5f, true) — the sickness itself is the caller's, through
    /// <see cref="CombatHooks.OnResurrected"/> or the spirit-healer service) and its corpse goes away
    /// (vmangos SpawnCorpseBones turns it into bones; bones are not modelled). Then the graveyard
    /// trip vmangos makes when the corpse's graveyard differs from the ghost's
    /// (<see cref="Death.IGraveyardRepop.TeleportToCorpseGraveyard"/>, when a graveyard feature is registered).
    /// </summary>
    public bool ResurrectAtSpiritHealer(Player player)
    {
        UnitCombat c = player.Combat;
        if (IsQuestSettlementPending(player) || IsAliveState(player) || (player.Flags & PlayerFlags.Ghost) == 0)
        {
            return false;
        }

        Death.CorpsePlace? place = c.Corpse is { } body ? new Death.CorpsePlace(body.MapId, body.X, body.Y, body.Z) : null;
        ResurrectPlayer(player, CombatConstants.CorpseReclaimRestorePercent, applySickness: true);
        if (c.Corpse is { } corpse)
        {
            RemoveCorpse(corpse);
            c.Corpse = null;
        }

        if (Death.DeathSeams.Find(_world)?.Graveyards is { } graveyards)
        {
            graveyards.TeleportToCorpseGraveyard(player, place);
        }
        else
        {
            player.NeedsVisibilityUpdate = true;
        }

        return true;
    }
}
