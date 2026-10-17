using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>What a party bot does when it is not fighting: follow its master, hold its place, or follow without ever attacking.</summary>
public enum PlayerbotPartyMode
{
    /// <summary>Follow the master at 2-5 yards and assist it (the default).</summary>
    Follow,

    /// <summary>Hold this place: no following and no teleport; fight back only what attacks the bot, without chasing.</summary>
    Stay,

    /// <summary>Follow, but never attack (mangoszero "passive"; the 'stop' command).</summary>
    Passive,
}

/// <summary>The follow step <see cref="PlayerbotParty.DecideFollow"/> chose.</summary>
internal enum PlayerbotFollowAction
{
    /// <summary>Close enough (or told to stay): stand.</summary>
    Hold,

    /// <summary>Walk to a point beside the master.</summary>
    Move,

    /// <summary>Teleport to the master (vmangos PartyBotAI .goname: more than 100 yards away or on another map or instance).</summary>
    Teleport,

    /// <summary>The master cannot be reached now (between maps, or on a map the bot may not enter): stand and wait.</summary>
    Wait,

    /// <summary>
    /// <see cref="PlayerbotCatchUpMode.FightThrough"/>: the master is on another map and an area trigger on the bot's map leads there
    /// (a dungeon entrance or exit): walk into it, as the master did.
    /// </summary>
    WalkToEntrance,
}

/// <summary>
/// The facts one follow decision needs (all read on the world thread). <see cref="SameMap"/> means the same map instance;
/// <see cref="MasterInOtherInstance"/> that the master is on the bot's map id but in another instance of it.
/// <see cref="CatchUp"/>, <see cref="TeleportDistance"/> and <see cref="LeaderCombatRange"/> are <c>World:Playerbots:Follow</c>'s;
/// <see cref="EntranceToMaster"/> that an area trigger on the bot's map leads to the master's map.
/// </summary>
internal readonly record struct PlayerbotFollowFacts(
    PlayerbotPartyMode Mode, bool MasterInWorld, bool SameMap, float Distance, bool TeleportToLeader, bool MasterMapAllowed,
    bool MasterFlying = false, bool InCombat = false, bool MasterInOtherInstance = false,
    PlayerbotCatchUpMode CatchUp = PlayerbotCatchUpMode.Teleport, float TeleportDistance = PlayerbotParty.TeleportDistance,
    bool MasterInCombat = false, float LeaderCombatRange = PlayerbotParty.PartyAssistRange, bool EntranceToMaster = false);

/// <summary>Another group member as <see cref="PlayerbotParty.ShouldAutoRevive"/> sees it.</summary>
internal readonly record struct PlayerbotReviveMember(bool InCombat, bool Alive, bool Healer, float? Distance);

/// <summary>
/// The pure rules of a party bot (vmangos PartyBotAI.cpp), apart from the world so they can be tested alone: who the master is,
/// whether to stand, walk or teleport, which unit to attack and whether to revive in place.
/// </summary>
internal static class PlayerbotParty
{
    /// <summary>The nearest a following bot stands to its master (vmangos PB_MIN_FOLLOW_DIST is 3; the lane spec asks 2-5).</summary>
    internal const float MinFollowDistance = 2f;

    /// <summary>The farthest a following bot stands from its master before it walks again.</summary>
    internal const float MaxFollowDistance = 5f;

    /// <summary>vmangos PartyBotAI::UpdateAI: "Teleport to leader if too far away" (IsWithinDistInMap(pLeader, 100.0f)).</summary>
    internal const float TeleportDistance = 100f;

    /// <summary>vmangos SelectPartyAttackTarget: another member's attacker counts within 50 yards of the bot.</summary>
    internal const float PartyAssistRange = 50f;

    /// <summary>vmangos ShouldAutoRevive: a living member within 15 yards lets the bot revive in place.</summary>
    internal const float ReviveCompanyRange = 15f;

    /// <summary>
    /// The bot's master (vmangos PartyBotAI::GetPartyLeader): the group's leader when it is a real player online, otherwise the first
    /// real player online in the group's member order; <see cref="ObjectGuid.Empty"/> when the group has none (bots only, or every
    /// real player offline). A real player is a socket client, never a managed bot.
    /// </summary>
    internal static ObjectGuid ResolveMaster(ObjectGuid leader, IReadOnlyList<ObjectGuid> members, ObjectGuid self,
        Func<ObjectGuid, bool> isRealPlayerOnline)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(isRealPlayerOnline);
        if (!leader.IsEmpty && leader != self && members.Contains(leader) && isRealPlayerOnline(leader)) return leader;
        foreach (ObjectGuid member in members)
            if (member != self && isRealPlayerOnline(member)) return member;
        return ObjectGuid.Empty;
    }

    /// <summary>
    /// One follow step before any route is planned (vmangos UpdateAI :795-817 and :884-891). Stay holds whatever happens. A master
    /// between maps is waited for, and so is one on a taxi flight (vmangos UpdateAI: idle while the leader IsTaxiFlying).
    /// <list type="bullet">
    /// <item><description>A master on another map is teleported to when <see cref="MayTeleport"/> allows it and the bot may be on that
    /// map (AllowedMaps, or the dungeon or raid the master is in: <see cref="PlayerbotMapPolicy"/>); with
    /// <see cref="PlayerbotCatchUpMode.FightThrough"/> the bot instead walks into the area trigger that leads there when there is one.
    /// A master in another instance of the bot's own map id is waited for: the teleport service moves a bot within its map id by a
    /// near teleport, which never changes the instance, and no trigger leads from an instance to another one of the same map.</description></item>
    /// <item><description>In <see cref="PlayerbotCatchUpMode.Teleport"/> mode a master on the bot's map farther than
    /// <see cref="PlayerbotFollowFacts.TeleportDistance"/> (vmangos: 100 yards) is teleported to; the other modes walk.</description></item>
    /// <item><description>Within <see cref="MaxFollowDistance"/> the bot holds, otherwise it walks (and <see cref="PlayerbotFollower"/> judges the
    /// route it plans: <see cref="PlayerbotFollowRules"/>).</description></item>
    /// </list>
    /// </summary>
    internal static PlayerbotFollowAction DecideFollow(in PlayerbotFollowFacts facts)
    {
        if (facts.Mode == PlayerbotPartyMode.Stay) return PlayerbotFollowAction.Hold;
        if (!facts.MasterInWorld || facts.MasterFlying) return PlayerbotFollowAction.Wait;
        bool mayTeleport = MayTeleport(facts);
        if (!facts.SameMap)
        {
            if (!facts.MasterMapAllowed || facts.MasterInOtherInstance) return PlayerbotFollowAction.Wait;
            if (mayTeleport) return PlayerbotFollowAction.Teleport;
            return facts.CatchUp == PlayerbotCatchUpMode.FightThrough && facts.EntranceToMaster
                ? PlayerbotFollowAction.WalkToEntrance : PlayerbotFollowAction.Wait;
        }

        if (!float.IsFinite(facts.Distance)) return PlayerbotFollowAction.Wait;
        if (facts.CatchUp == PlayerbotCatchUpMode.Teleport && facts.Distance > facts.TeleportDistance && mayTeleport)
            return PlayerbotFollowAction.Teleport;
        return facts.Distance > MaxFollowDistance ? PlayerbotFollowAction.Move : PlayerbotFollowAction.Hold;
    }

    /// <summary>
    /// Whether a catch-up teleport is allowed at all: <see cref="PlayerbotPartyOptions.TeleportToLeader"/> is on, the mode is not
    /// <see cref="PlayerbotCatchUpMode.FightThrough"/>, and the bot is out of combat (vmangos teleports only inside its
    /// <c>!IsInCombat()</c> block), or its master left the fight: the master is out of combat and farther than
    /// <see cref="PlayerbotFollowFacts.LeaderCombatRange"/> yards (or on another map). A bot then teleports out of a fight its group
    /// has left instead of dying in it alone.
    /// </summary>
    internal static bool MayTeleport(in PlayerbotFollowFacts facts)
    {
        if (!facts.TeleportToLeader || facts.CatchUp == PlayerbotCatchUpMode.FightThrough) return false;
        if (!facts.InCombat) return true;
        return !facts.MasterInCombat && (!facts.SameMap || !float.IsFinite(facts.Distance) || facts.Distance > facts.LeaderCombatRange);
    }

    /// <summary>
    /// Where a follower stands: <paramref name="distance"/> yards from the master at <paramref name="angle"/> radians from the master's
    /// facing (vmangos MoveFollow(pLeader, urand(dist), frand(angle))).
    /// </summary>
    internal static Vector3 FollowPoint(Vector3 master, float masterOrientation, float angle, float distance)
    {
        float heading = masterOrientation + angle;
        return new Vector3(master.X + (MathF.Cos(heading) * distance), master.Y + (MathF.Sin(heading) * distance), master.Z);
    }

    /// <summary>
    /// vmangos PartyBotAI::SelectAttackTarget (:359) and SelectPartyAttackTarget (:414), in this order: the target the master
    /// ordered ('attack'), the master's own victim, whoever attacks the bot, then whoever attacks another member within
    /// <see cref="PartyAssistRange"/> yards. Each candidate must pass <paramref name="valid"/> (alive, same map, attackable).
    /// </summary>
    internal static T? SelectAttackTarget<T>(T? ordered, T? masterVictim, IEnumerable<T> ownAttackers,
        IEnumerable<(T Unit, float Distance)> memberAttackers, Func<T, bool> valid) where T : class
    {
        ArgumentNullException.ThrowIfNull(ownAttackers);
        ArgumentNullException.ThrowIfNull(memberAttackers);
        ArgumentNullException.ThrowIfNull(valid);
        if (ordered is not null && valid(ordered)) return ordered;
        if (masterVictim is not null && valid(masterVictim)) return masterVictim;
        foreach (T attacker in ownAttackers)
            if (valid(attacker)) return attacker;
        foreach ((T unit, float distance) in memberAttackers)
            if (float.IsFinite(distance) && distance <= PartyAssistRange && valid(unit)) return unit;
        return null;
    }

    /// <summary>
    /// vmangos PartyBotAI::ShouldAutoRevive (:237): a released ghost revives at once; while any other member fights, or a healer
    /// (priest, paladin, shaman, druid) is alive to resurrect it, the bot waits; otherwise it revives when a living member is within
    /// <see cref="ReviveCompanyRange"/> yards.
    /// </summary>
    internal static bool ShouldAutoRevive(bool ghost, IEnumerable<PlayerbotReviveMember> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (ghost) return true;
        bool company = false;
        foreach (PlayerbotReviveMember member in others)
        {
            if (member.InCombat) return false;
            if (!member.Alive) continue;
            if (member.Healer) return false;
            if (member.Distance is { } distance && distance <= ReviveCompanyRange) company = true;
        }

        return company;
    }

    /// <summary>vmangos CombatBotBaseAI::IsHealerClass: the classes that can resurrect.</summary>
    internal static bool IsHealerClass(Class @class) => @class is Class.Priest or Class.Paladin or Class.Shaman or Class.Druid;
}
