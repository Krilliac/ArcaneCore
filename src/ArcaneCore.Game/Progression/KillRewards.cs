using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
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
