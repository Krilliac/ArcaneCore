using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Totems;

namespace ArcaneCore.Game.Progression;

/// <summary>A reward source's group as seen by kill/cast rewards (member GUIDs and the raid flag).</summary>
public sealed record RewardGroup(IReadOnlyList<ObjectGuid> Members, bool IsRaid);

/// <summary>
/// Who shares a kill or a quest cast, reimplemented from vmangos/core 4b3d241 Player.cpp
/// RewardPlayerAndGroupAtKill / RewardPlayerAndGroupAtCast, Group::RewardGroupAtKill and
/// Player::IsAtGroupRewardDistance: a solo player alone, otherwise every group member on the
/// source's map within the group reward distance (dead members included; ghosts only lose
/// quest credit).
/// </summary>
public static class KillRewards
{
    /// <summary>
    /// The player a killing blow (or any damage) is credited to: vmangos Unit::GetCharmerOrOwnerPlayerOrPlayerItself
    /// (Unit.cpp:4806-4812), which Unit::Kill starts pPlayerTap from (Unit.cpp:981). The unit itself when it is a player, else
    /// the player that charms or owns it (a hunter's pet, a warlock's demon, a totem, a guardian, a charmed creature), else the
    /// player a stand-in unit reports through <see cref="IPlayerControlledUnit"/>; null for a unit no player controls.
    /// </summary>
    public static Player? CreditedPlayer(Unit? unit)
        => unit is null ? null : unit.GetCharmerOrOwnerPlayerOrSelf() ?? DuelRules.ControllingPlayer(unit);

    /// <summary>
    /// Unit::Kill's tap for a creature victim (Unit.cpp:981-1000): pPlayerTap is <paramref name="killingPlayer"/> (see
    /// <see cref="CreditedPlayer"/>) replaced by the creature's original loot recipient while he is on the victim's map; pGroupTap
    /// is the group of the tap while it has members, else pPlayerTap's current group (<paramref name="groupOf"/>; a group that
    /// does not hold him is ignored). Both null when nobody is credited.
    /// </summary>
    public static (Player? Tapper, RewardGroup? Group) ResolveTap(Player? killingPlayer, Creature victim, Func<Player, RewardGroup?> groupOf)
    {
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(groupOf);
        Player? tapper = (victim.LootTapPlayerGuid.IsEmpty ? null : victim.Map?.FindPlayer(victim.LootTapPlayerGuid)) ?? killingPlayer;
        RewardGroup? group = victim.LootTapGroup is { MemberCount: > 0 } tapGroup
            ? new RewardGroup([.. tapGroup.Members.Select(m => m.Guid)], tapGroup.IsRaid)
            : tapper is not null && groupOf(tapper) is { } current && current.Members.Contains(tapper.Guid) ? current : null;
        return (tapper, group);
    }

    /// <summary>
    /// Group::RewardGroupAtKill (Group.cpp:2384-2405): the members of <paramref name="group"/> at reward distance in group order,
    /// then <paramref name="tapper"/> when he is not one of them (he left the group after the tap) and is at reward distance;
    /// without a group the tapper alone (Player::RewardSinglePlayerAtKill).
    /// </summary>
    public static IReadOnlyList<Player> TapRecipients(Player? tapper, Creature victim, RewardGroup? group, float distance)
    {
        ArgumentNullException.ThrowIfNull(victim);
        if (group is null)
        {
            return tapper is null ? [] : [tapper];
        }

        if (tapper is not null && group.Members.Contains(tapper.Guid))
        {
            return Recipients(tapper, victim, group, distance);
        }

        // Any member on the victim's map anchors the distance pass (Recipients measures every member, the anchor too).
        Player? anchor = null;
        foreach (ObjectGuid guid in group.Members)
        {
            if (victim.Map?.FindPlayer(guid) is { } member)
            {
                anchor = member;
                break;
            }
        }

        List<Player> recipients = anchor is null ? [] : [.. Recipients(anchor, victim, group, distance)];
        if (tapper is not null && Recipients(tapper, victim, new RewardGroup([tapper.Guid], false), distance).Count > 0)
        {
            recipients.Add(tapper);
        }

        return recipients;
    }

    public static IReadOnlyList<Player> Recipients(Player actor, Unit source, RewardGroup? group, float distance)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(source);
        if (group is null || !group.Members.Contains(actor.Guid))
        {
            return [actor];
        }

        var recipients = new List<Player>(group.Members.Count);
        float maxSq = distance * distance;
        foreach (ObjectGuid guid in group.Members)
        {
            Player? member = guid == actor.Guid ? actor : source.Map?.FindPlayer(guid);
            if (member is null || !member.IsInWorld || source.Map is null || !ReferenceEquals(member.Map, source.Map))
            {
                continue;
            }

            float dx = member.X - source.X;
            float dy = member.Y - source.Y;
            float dz = member.Z - source.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= maxSq)
            {
                recipients.Add(member);
            }
        }

        return recipients;
    }

    /// <summary>vmangos: quest objectives update for alive members or dead ones that have not released.</summary>
    public static bool CanReceiveQuestCredit(Player player) => player.IsAlive || (player.Flags & PlayerFlags.Ghost) == 0;

    /// <summary>vmangos Creature::IsElite: any rank except normal and rare.</summary>
    public static bool IsElite(Creature creature)
        => (CreatureRank)creature.Template.Rank is CreatureRank.Elite or CreatureRank.RareElite or CreatureRank.WorldBoss;

    /// <summary>
    /// Group::RewardGroupAtKill / RewardSinglePlayerAtKill experience: one share per recipient,
    /// granted through <paramref name="progression"/> (rested bonus applies to kills).
    /// Returns the XP granted to each recipient (0 where nothing was given).
    /// </summary>
    public static IReadOnlyList<uint> AwardExperience(PlayerProgression progression, IReadOnlyList<Player> recipients,
        Creature victim, bool nonRaidDungeon)
    {
        ArgumentNullException.ThrowIfNull(progression);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(victim);
        if (TotemQuery.IsTotem(victim))
        {
            // Player::IsHonorOrXPTarget (Player.cpp:19950) and MaNGOS::XP::Gain (Formulas.h:102-107): a summoned totem is worth nothing.
            return new uint[recipients.Count];
        }

        KillCandidate[] candidates = recipients
            .Select(p => new KillCandidate(p.Level, p.IsAlive, (p.Flags & PlayerFlags.Ghost) != 0)).ToArray();
        IReadOnlyList<KillShare> shares = KillExperience.Distribute(candidates, victim.Level, IsElite(victim), nonRaidDungeon,
            progression.Options.RateXpKill, progression.Options.RateXpKillElite);
        uint[] granted = new uint[recipients.Count];
        foreach (KillShare share in shares)
        {
            if (share.Experience > 0)
            {
                granted[share.MemberIndex] = progression.GiveXp(recipients[share.MemberIndex], share.Experience, victim.Guid);
            }
        }

        return granted;
    }
}
