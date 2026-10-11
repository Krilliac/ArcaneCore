using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Totems;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Who receives kill reputation, as vmangos credits it (Unit::Kill, Unit.cpp:978-1000 and 1094-1097, then
/// Player::RewardSinglePlayerAtKill, Player.cpp:19959-19980, or Group::RewardGroupAtKill / RewardGroupAtKill_helper,
/// Group.cpp:2295-2409): the tap decides, not the killing blow. The player who tapped the creature (<see cref="Creature.LootTapPlayerGuid"/>,
/// replaced by the killer's controlling player when he is not on the map, or when nobody tapped it) and the group of the tap
/// (<see cref="Creature.LootTapGroup"/> while it is not disbanded, else that player's current group). Solo, he alone gains;
/// otherwise every member of that group at reward distance, alive or dead (reputation, unlike experience, does not need a living
/// member), and then the tapper himself when he left the group after the tap and is still at reward distance.
/// <c>RewardReputation(Unit*)</c> gives nothing for a pet victim (patch 1.10, Player.cpp:6361-6365) or for a victim a player
/// controls (the PvP flag of the group helper; a totem or a charmed unit). The killer need not be alive.
/// <para>
/// Experience and quest kill credit resolve the same tap (<see cref="KillRewards.ResolveTap"/>): a tap a pet still holds credits nobody
/// (Unit.cpp:1020 gates group rewards too), and a tap that failed the damage-origin rule (players dealt 35% or less) was cleared at the kill, so then only
/// a player behind the killing blow is credited. The Alterac Valley reputation for killing enemy
/// players (Group.cpp:2305-2320) belongs to the battleground area.
/// </para>
/// </summary>
public static class ReputationKillCredit
{
    /// <summary>Whether killing <paramref name="victim"/> can give reputation at all.</summary>
    public static bool Gives(Creature victim)
        => !victim.IsPet && !TotemQuery.IsTotem(victim) && !KillRewards.IsPvpVictim(victim);

    /// <summary>
    /// The kill of <paramref name="victim"/>, with <paramref name="killer"/> the player the killing blow acted for (null when no
    /// player on the victim's map did): resolve the tapper and the group of the tap (see the type summary; <paramref name="groupOf"/>
    /// gives a player's current group) and <see cref="Award"/> them. Returns the recipients.
    /// </summary>
    public static IReadOnlyList<Player> AwardKill(ReputationService service, Player? killer, Creature victim, Func<Player, RewardGroup?> groupOf,
        float distance)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(groupOf);

        // Unit.cpp:981-1000: pPlayerTap starts as the killer's player and becomes the original loot recipient when he is online;
        // pGroupTap is the group of the tap while it exists, else pPlayerTap's group.
        (Player? tapper, RewardGroup? group) = KillRewards.ResolveTap(killer, victim, groupOf);
        if (tapper is null && group is null)
        {
            return [];
        }

        return Award(service, tapper, victim, group, distance);
    }

    /// <summary>
    /// Give <see cref="ReputationService.RewardKill"/> to every recipient of a kill credited to <paramref name="tapper"/> and
    /// <paramref name="group"/>; returns the recipients (those without loaded standings get nothing).
    /// </summary>
    public static IReadOnlyList<Player> Award(ReputationService service, Player? tapper, Creature victim, RewardGroup? group, float distance)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(victim);
        if (!Gives(victim))
        {
            return [];
        }

        IReadOnlyList<Player> recipients = KillRewards.TapRecipients(tapper, victim, group, distance);
        foreach (Player recipient in recipients)
        {
            service.RewardKill(recipient, victim);
        }

        return recipients;
    }
}
