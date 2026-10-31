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
    /// The GUID vmangos Creature::SetLootRecipient records for a tap by <paramref name="unit"/>, acting for <paramref name="player"/>
    /// (Creature.cpp:1602-1605): the pet's own GUID while it is its owner's active pet (UNIT_FIELD_SUMMON), a provisional tap the
    /// owner or his group takes over on their first hit (<see cref="TakesOverPetTap"/>); otherwise the player's.
    /// Guardians, totems and charmed creatures tap for their player at once.
    /// </summary>
    public static ObjectGuid TapGuid(Unit unit, Player player)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(player);
        return unit is Creature { IsPet: true } && !unit.Guid.IsEmpty && player.PetGuid == unit.Guid ? unit.Guid : player.Guid;
    }

    /// <summary>
    /// vmangos Unit::DealDamage (Unit.cpp:811-819): a tap still held by a pet passes to a player <paramref name="attacker"/> who owns
    /// that pet, or who is in the group recorded with the tap (<paramref name="inTapGroup"/>).
    /// </summary>
    public static bool TakesOverPetTap(ObjectGuid tap, Unit attacker, bool inTapGroup)
        => !tap.IsEmpty && !tap.IsPlayer && attacker is Player hitter && (hitter.PetGuid == tap || inTapGroup);

    /// <summary>
    /// Unit::Kill's tap for a creature victim (Unit.cpp:981-1000): pPlayerTap is <paramref name="killingPlayer"/> (see
    /// <see cref="CreditedPlayer"/>, null when no player on the map is behind the killing blow) replaced by the creature's original
    /// loot recipient; a tap still held by a pet leaves no pPlayerTap at all (Unit.cpp:995-997). pGroupTap is the group of the tap
    /// while it has members, else pPlayerTap's current group (<paramref name="groupOf"/>; a group that does not hold him is ignored).
    /// Both null when nobody is credited. A tap that failed the damage-origin rule was already cleared at the kill
    /// (<see cref="Creature.SettleKillCredit"/>, Unit.cpp:1007-1008), which leaves the killing player alone with NO group
    /// (pGroupTap stays null): rep, quest credit and honor for him only, 0 XP (Formulas.h:152).
    /// Limit: the recipient is looked up on the victim's map only (vmangos GetOriginalLootRecipient finds him anywhere in the world);
    /// a tapper who has left the map is replaced by the killing player.
    /// </summary>
    public static (Player? Tapper, RewardGroup? Group) ResolveTap(Player? killingPlayer, Creature victim, Func<Player, RewardGroup?> groupOf)
    {
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(groupOf);
        // Unit::Kill (Unit.cpp:1007-1008): the damage-origin rule failed, the tap was cleared at the kill (Creature.SettleKillCredit) and
        // pGroupTap is never set (the group assignments at :991 and :998-999 sit inside the allowed branch): the killing blow's player
        // alone, whatever his group, whoever tapped (a provisional pet tap included). Null when no player is behind the blow.
        if (!victim.KillCreditAllowed)
        {
            return (killingPlayer, null);
        }

        ObjectGuid tap = victim.LootTapPlayerGuid;
        Player? tapper = tap.IsEmpty ? killingPlayer
            : !tap.IsPlayer ? null
            : victim.Map?.FindPlayer(tap) ?? killingPlayer;
        // Unit::Kill (Unit.cpp:1020-1098) gates loot AND RewardGroupAtKill on pPlayerTap.
        // A provisional pet tap cannot reward its group until a player takes it over.
        if (tapper is null)
        {
            return (null, null);
        }

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

    public static IReadOnlyList<Player> Recipients(Player actor, WorldObject source, RewardGroup? group, float distance)
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

    /// <summary>
    /// vmangos Unit::IsCharmerOrOwnerPlayerOrPlayerItself for a victim (the PvP flag of RewardSinglePlayerAtKill and
    /// RewardGroupAtKill): a creature a player owns or charms, or one a stand-in reports through <see cref="IPlayerControlledUnit"/>.
    /// Such a kill gives no experience, no reputation and no KilledMonster credit.
    /// </summary>
    public static bool IsPvpVictim(Unit victim)
    {
        ArgumentNullException.ThrowIfNull(victim);
        return victim.IsCharmerOrOwnerPlayerOrPlayerItself || DuelRules.ControllingPlayer(victim) is not null;
    }

    /// <summary>vmangos: quest objectives update for alive members or dead ones that have not released.</summary>
    public static bool CanReceiveQuestCredit(Player player) => player.IsAlive || (player.Flags & PlayerFlags.Ghost) == 0;

    /// <summary>vmangos Creature::IsElite: any rank except normal and rare.</summary>
    public static bool IsElite(Creature creature)
        => (CreatureRank)creature.Template.Rank is CreatureRank.Elite or CreatureRank.RareElite or CreatureRank.WorldBoss;

    /// <summary>vmangos CREATURE_STATIC_FLAG_NO_XP (static_flags1 bit 0x2, same bit in cmangos).</summary>
    private const uint StaticFlagNoXp = 0x00000002;

    /// <summary>MaNGOS::XP::Gain (Formulas.h:112): a CREATURE_STATIC_FLAG_NO_XP victim gives no experience to players or their pets.</summary>
    internal static bool GrantsNoExperience(Creature victim) => (victim.Template.StaticFlags1 & StaticFlagNoXp) != 0;

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
        if (GrantsNoExperience(victim))
        {
            // MaNGOS::XP::Gain (Formulas.h:112): CREATURE_STATIC_FLAG_NO_XP (CreatureDefines.h:99) gives no experience.
            return new uint[recipients.Count];
        }

        if (TotemQuery.IsTotem(victim) || IsPvpVictim(victim))
        {
            // Player::IsHonorOrXPTarget (Player.cpp:19950) and MaNGOS::XP::Gain (Formulas.h:102-107): a summoned totem is worth nothing;
            // a victim a player owns or charms is a PvP kill, which gives no experience (Player.cpp:19961-19962, Group.cpp:2364, 2378).
            return new uint[recipients.Count];
        }

        KillCandidate[] candidates = recipients
            .Select(p => new KillCandidate(p.Level, p.IsAlive, (p.Flags & PlayerFlags.Ghost) != 0)).ToArray();
        // MaNGOS::XP::Gain multiplies by the players' damage share before the kill rate (Formulas.h:152-153).
        IReadOnlyList<KillShare> shares = KillExperience.Distribute(candidates, victim.Level, IsElite(victim), nonRaidDungeon,
            progression.Options.RateXpKill * victim.KillXpModifier, progression.Options.RateXpKillElite);
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
