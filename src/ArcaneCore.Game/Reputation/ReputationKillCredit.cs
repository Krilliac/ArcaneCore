using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Totems;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Who receives kill reputation, as vmangos credits it (Player::RewardSinglePlayerAtKill, Player.cpp:19959-19980, and
/// Group::RewardGroupAtKill / RewardGroupAtKill_helper, Group.cpp:2295-2409): a solo player alone, otherwise every group
/// member at reward distance, alive or dead (reputation, unlike experience, does not need a living member), and
/// <c>RewardReputation(Unit*)</c> gives nothing for a pet victim (patch 1.10, Player.cpp:6361-6365) or for a victim a player
/// controls (the PvP flag of the group helper; a totem or a charmed unit). The killer need not be alive.
/// <para>
/// Limit: vmangos credits the loot tapper plus the group of the tap, not the killer; this codebase credits the killer's
/// group, as experience and quest credit do, until a tap primitive exists. The Alterac Valley reputation for killing
/// enemy players (Group.cpp:2305-2320) belongs to the battleground area.
/// </para>
/// </summary>
public static class ReputationKillCredit
{
    /// <summary>Whether killing <paramref name="victim"/> can give reputation at all.</summary>
    public static bool Gives(Creature victim)
        => !victim.IsPet && !TotemQuery.IsTotem(victim) && DuelRules.ControllingPlayer(victim) is null;

    /// <summary>Give <see cref="ReputationService.RewardKill"/> to every recipient; returns the recipients (those without loaded standings get nothing).</summary>
    public static IReadOnlyList<Player> Award(ReputationService service, Player killer, Creature victim, RewardGroup? group, float distance)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(killer);
        ArgumentNullException.ThrowIfNull(victim);
        if (!Gives(victim))
        {
            return [];
        }

        IReadOnlyList<Player> recipients = KillRewards.Recipients(killer, victim, group, distance);
        foreach (Player recipient in recipients)
        {
            service.RewardKill(recipient, victim);
        }

        return recipients;
    }
}
