using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// Leader handoff (docs/areas/playbots-groups.md, "Leader handoff"): a real player brings a party or raid of managed bots into a dungeon or
/// raid instance and passes the lead to one of them (CMSG_GROUP_SET_LEADER). The coordinator then adopts the player's own server group as a
/// bot-led group of the instance the new leader stands in, as it is: no re-forming, no travel, no teleport, the members keep their raid
/// subgroups, the player stays in as a <see cref="Member.Real"/> member the bots neither wait for nor block on. The bots clear the instance
/// with the ordinary machinery of a run: the catalogue's route for the wing the leader stands in (<see cref="PlayerbotRunMachine"/>,
/// <see cref="PlayerbotGroupAI"/> pulling the trash along the way to each boss), or, for an instance the catalogue has no route for, the
/// cached clear list of the hostile creatures alive in it (<see cref="PlayerbotClearList"/>, nearest first). Wipes, rests, loot, the stall
/// watch and the step bounds are the runs' own.
/// <para>
/// The clear ends when every boss of the wing (or everything on the clear list) is dead, or early: the player takes the lead back (any order
/// such as <c>follow</c>, see <see cref="OwnsAgainst"/>, or the leader changing to anyone else), the bot leader or every real player leaves
/// the group, the leader is moved out of the instance, too many wipes, a stall or a timeout. The leader says why in party (raid) chat, passes
/// the lead back to the player, and every bot is released to its party AI in place: it stays in the player's group and in the instance and
/// follows the player again (never the run-end walk out and disband, which would take the bots out of the player's group).
/// </para>
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    private GroupManager? _leadersWatched;
    private readonly List<(Group Group, ObjectGuid OldLeader)> _leaderChanges = [];
    private int _handoffsStarted, _handoffsCompleted, _handoffsEnded;

    /// <summary>The handoffs adopted, completed and ended early so far (status and tests).</summary>
    internal (int Started, int Completed, int Ended) HandoffTotals => (_handoffsStarted, _handoffsCompleted, _handoffsEnded);

    /// <summary>The bot-led group adopted from server group <paramref name="serverGroupId"/>, or null (world thread).</summary>
    internal BotGroup? HandoffOf(uint serverGroupId)
    {
        foreach (BotGroup group in _groups)
            if (group.Handoff is { } handoff && handoff.ServerGroupId == serverGroupId) return group;
        return null;
    }

    // --- the trigger --------------------------------------------------------------------------------------------------

    /// <summary>Subscribe to the server's leader changes (once per group manager; the social feature may attach after this one).</summary>
    private void WatchLeaderChanges()
    {
        GroupManager? groups = GroupManagerOrNull();
        if (ReferenceEquals(groups, _leadersWatched) || _subscriptions is not { IsDisposed: false } bag) return;
        if (_leadersWatched is not null) _leadersWatched.LeaderChanged -= OnLeaderChanged; // also undone by the bag at detach (a second -= does nothing)
        _leadersWatched = groups;
        if (groups is not null) bag.Subscribe<Action<Group, ObjectGuid>>(OnLeaderChanged, h => groups.LeaderChanged += h, h => groups.LeaderChanged -= h);
        // A bot removed from its group by another member remembers who did it (a kick is the only uninvite that removes a member).
        if (groups is not null) bag.Subscribe<Action<ObjectGuid, ObjectGuid>>(OnMemberKicked, h => groups.MemberKicked += h, h => groups.MemberKicked -= h);
    }

    /// <summary>GroupManager.LeaderChanged (world thread, inside the packet handler): queued, handled at the coordinator's next update.</summary>
    private void OnLeaderChanged(Group group, ObjectGuid oldLeader)
    {
        if (_leaderChanges.Count >= 256) _leaderChanges.RemoveAt(0);
        _leaderChanges.Add((group, oldLeader));
    }

    /// <summary>The leader changes since the last update: adopt a handed-over group, or end a handoff whose lead moved on.</summary>
    private void TakeLeaderChanges()
    {
        WatchLeaderChanges();
        if (_leaderChanges.Count == 0) return;
        (Group Group, ObjectGuid OldLeader)[] changes = [.. _leaderChanges];
        _leaderChanges.Clear();
        foreach ((Group server, ObjectGuid old) in changes)
        {
            try
            {
                if (HandoffOf(server.Id) is { } led)
                {
                    if (led.LeaderGuid == server.LeaderGuid) continue;
                    // A player who has the lead again, or a bot leader that passed it on: the clear ends here (a bot-to-bot pass is no handoff).
                    EndHandoff(led, success: false, IsRealOnline(server.LeaderGuid) ? "leader-taken-back" : "leader-passed", handBackTo: null);
                    continue;
                }

                TryAdopt(server, old);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.groups", ex);
                PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.HandoffFault,
                    string.Create(CultureInfo.InvariantCulture, $"server-group {server.Id} (leader {server.LeaderGuid.Value:X})"),
                    string.Create(CultureInfo.InvariantCulture, $"leader handoff of server group {server.Id} failed ({ex.GetType().Name}: {ex.Message})"),
                    _world?.Identity);
                logger.LogFault("playerbot.group-coordinator", ex, "Playerbot leader handoff of server group {Group} failed", server.Id);
            }
        }
    }

    private bool IsRealOnline(ObjectGuid guid) => _world?.FindOnlinePlayer(guid) is { Session: WorldSession { IsManaged: false } };

    private bool TryFindBot(ObjectGuid guid, out PlayerbotGroupBot bot)
    {
        foreach (PlayerbotGroupBot candidate in _bots.Values)
            if (candidate.Session.Player?.Guid == guid)
            {
                bot = candidate;
                return true;
            }

        bot = default;
        return false;
    }

    /// <summary>
    /// A server group's lead went from a real player online, still in it, to one of the managed bots: adopt the group when the new leader
    /// stands in a dungeon or raid instance (and say why not otherwise, the bots then keep following the player).
    /// </summary>
    internal BotGroup? TryAdopt(Group server, ObjectGuid oldLeader)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (_world is not { } world || server.IsBattlegroundGroup) return null;
        if (world.FindOnlinePlayer(server.LeaderGuid) is not { Session: WorldSession { IsManaged: true } } leader
            || !TryFindBot(leader.Guid, out PlayerbotGroupBot leaderBot) || _memberOf.ContainsKey(leaderBot.BotId)) return null;
        if (world.FindOnlinePlayer(oldLeader) is not { Session: WorldSession { IsManaged: false } } giver || !server.IsMember(oldLeader)) return null;

        WorldMaps maps = WorldMaps.Of(world);
        if (leader.Map is not { } map || maps.Registry.Find(leader.MapId) is not { IsDungeon: true } instance)
        {
            Note($"{leader.Name} was given the lead of {giver.Name}'s group outside an instance (map {leader.MapId}): the bots keep following");
            return null;
        }

        var leaderAt = new Vector3(leader.X, leader.Y, leader.Z);
        PlayerbotRunPlan? plan = PlayerbotLeaderHandoff.ChooseWing(Catalogue().Plans, leader.MapId, leaderAt);
        (uint Trigger, uint Map, Vector3 At, Vector3 Landing)? way = plan is not null
            ? (plan.EntranceTrigger, plan.EntranceMap, plan.Entrance, ExitLanding(world, leader.MapId) ?? plan.Entrance)
            : EntranceOf(world, leader.MapId);
        var inside = new List<(PlayerbotGroupBot Bot, Player Player, byte SubGroup)>();
        foreach (GroupMemberSlot slot in server.Members)
            if (TryFindBot(slot.Guid, out PlayerbotGroupBot bot) && !bot.Scripted && !_memberOf.ContainsKey(bot.BotId)
                && bot.Session.Player is { IsInWorld: true } player && ReferenceEquals(player.Map, map))
                inside.Add((bot, player, slot.SubGroup));

        string? refusal = !_options.Groups.Enabled ? "groups-disabled"
            : leaderBot.Scripted ? "scripted"
            : !PlayerbotMapPolicy.MayMoveOn(leader, _options) ? "map-not-allowed"
            : way is null ? "no-entrance"
            : !inside.Any(i => i.Bot.BotId == leaderBot.BotId) ? "no-bots-inside"
            : null;
        if (refusal is not null)
        {
            Note($"{leader.Name} will not lead the clear of {instance.Name} for {giver.Name}: {refusal}");
            logger.LogInformation("Playerbot leader handoff refused: {Bot} in {Instance} ({Map}) for {Player}: {Reason}", leader.Name, instance.Name, leader.MapId, giver.Name, refusal);
            Say(leaderBot.Session, leader, PlayerbotLeaderHandoff.RefusedLine(instance.Name, refusal));
            return null;
        }

        (uint trigger, uint entranceMap, Vector3 entrance, Vector3 landing) = way!.Value;
        PlayerbotClearList? sweep = plan is null ? new PlayerbotClearList(ClearTargets(leader)) : null;
        PlayerbotHandoffBot[] described = [.. inside.Select(i => new PlayerbotHandoffBot(i.Bot.BotId, i.Player.Class, TalentRole(i.Bot.Session, i.Player), i.Player.Level))];
        PlayerbotGroupRole[] roles = PlayerbotLeaderHandoff.AssignRoles(described, server.IsRaid, _options.Groups);
        PlayerbotRunBoss? first = plan?.Bosses[0];
        var goal = new PlayerbotGroupGoal(instance.IsRaid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Dungeon, 0, first?.Entry ?? 0, leader.MapId,
            entranceMap, entrance, trigger, first?.Position ?? leaderAt, inside.Count, 0, PlayerbotLeaderHandoff.Source);
        PlayerbotRunMachine? run = null;
        if (plan is not null)
        {
            run = new PlayerbotRunMachine(plan, plan.Raid ? _options.Groups.DungeonRuns.Raids.MaxWipes : _options.Groups.DungeonRuns.MaxWipes);
            run.Apply(PlayerbotRunSignal.Arrived);
            // The bosses the group already killed (or that never spawned in this save) count as done: dead, or absent from a loaded grid.
            foreach (PlayerbotRunBoss boss in plan.Bosses)
                if (GridLoadedAt(leader, boss.Position) && FindLivingOnMap(leader, boss.Entry) is null) run.Apply(PlayerbotRunSignal.BossDead(boss.Entry));
            run.Apply(PlayerbotRunSignal.AllInside);
        }

        var group = new BotGroup(_nextId++, goal, leaderBot.BotId, leader.Guid, Now)
        {
            Run = run, Raid = server.IsRaid, Handoff = new PlayerbotHandoff(server.Id, giver.Guid, giver.Name, instance.Name, sweep),
            Approach = ApproachPoint(goal, landing),
        };
        for (int index = 0; index < inside.Count; index++)
        {
            (PlayerbotGroupBot bot, Player player, byte subGroup) = inside[index];
            group.Members.Add(new Member(bot.BotId, player.Guid, player.Name, roles[index], described[index].TalentRole, subGroup, real: false)
            {
                Joined = true, AI = new PlayerbotGroupAI(bot.Session, _options),
            });
            _memberOf[bot.BotId] = group;
            _waiting.Remove(bot.BotId);
            PlayerbotAreaTriggers.AllowTeleports(player, true); // a corpse run goes out and back in through the instance's triggers
        }

        foreach (GroupMemberSlot slot in server.Members)
            if (world.FindOnlinePlayer(slot.Guid) is { Session: WorldSession { IsManaged: false } } real)
                group.Members.Add(new Member(Guid.Empty, real.Guid, real.Name, PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, slot.SubGroup, real: true) { Joined = true });

        _groups.Add(group);
        _handoffsStarted++;
        Enter(group, PlayerbotGroupState.Engaging);
        string what = run is not null
            ? string.Create(CultureInfo.InvariantCulture, $"run {run.Plan.Label} {run.Killed}/{run.BossCount} bosses dead")
            : string.Create(CultureInfo.InvariantCulture, $"clear list of {sweep!.Remaining}");
        Note($"group {group.Id} adopted from {giver.Name}'s {(server.IsRaid ? "raid" : "party")} in {instance.Name}: leader {leader.Name}, {what}, "
             + string.Join(", ", group.Members.Select(m => $"{m.Name} {(m.Real ? "player" : m.Role.ToString().ToLowerInvariant())}")));
        logger.LogInformation("Playerbot leader handoff: {Bot} leads {Count} bots of {Player}'s group through {Instance} ({Map}): {What}",
            leader.Name, inside.Count, giver.Name, instance.Name, leader.MapId, what);
        Say(leaderBot.Session, leader, PlayerbotLeaderHandoff.TakeOverLine(instance.Name));
        return group;
    }

    /// <summary>The way in and out of an instance without a catalogue plan: the trigger outside that leads in, near where its exit lands.</summary>
    private static (uint Trigger, uint Map, Vector3 At, Vector3 Landing)? EntranceOf(WorldRuntime world, uint instanceMap)
    {
        WorldMaps maps = WorldMaps.Of(world);
        if (ExitOf(world, instanceMap) is not { } exit || maps.FindAreaTriggerTeleport(exit.Id) is not { } outside) return null;
        var landing = new Vector3(outside.TargetX, outside.TargetY, outside.TargetZ);
        if (EntranceTo(maps, outside.TargetMap, instanceMap, landing) is not { } entrance) return null;
        return (entrance.Id, entrance.MapId, new Vector3(entrance.X, entrance.Y, entrance.Z), landing);
    }

    /// <summary>Where the instance's exit puts a player outside (the group's approach is on that side of the entrance), or null.</summary>
    private static Vector3? ExitLanding(WorldRuntime world, uint instanceMap)
        => ExitOf(world, instanceMap) is { } exit && WorldMaps.Of(world).FindAreaTriggerTeleport(exit.Id) is { } outside
            ? new Vector3(outside.TargetX, outside.TargetY, outside.TargetZ) : null;

    /// <summary>
    /// The hostile creatures alive on the leader's map instance (its loaded grids): attackable, hostile to the group, no service NPCs or
    /// critters, nobody else's. One pass over the map's creatures, at the handoff and at most once more when the list runs empty.
    /// </summary>
    private List<PlayerbotClearTarget> ClearTargets(Player leader)
    {
        var targets = new List<PlayerbotClearTarget>();
        if (leader.Map is not { } map || map.FindUpdater<CreatureMapSystem>() is not { } system) return targets;
        GroupManager? groups = GroupManagerOrNull();
        foreach (Creature creature in system.Creatures)
        {
            if (!creature.IsAlive || !creature.IsInWorld || creature.Template.NpcFlags != 0 || creature.Template.CreatureType == 8
                || !map.Combat.Hooks.CanAttack(leader, creature) || !map.Combat.Hooks.IsHostileTo(creature, leader)
                || PlayerbotBrain.IsSomeoneElses(leader, creature, groups)) continue;
            targets.Add(new PlayerbotClearTarget(creature.Guid.Value, creature.Entry, new Vector3(creature.X, creature.Y, creature.Z)));
        }

        return targets;
    }

    /// <summary>
    /// The creature a handed-over group without a catalogue route clears next (<see cref="PlayerbotClearList.Next"/>), seen from
    /// <paramref name="player"/>, or null: nothing left, or the player is not in the group's instance (a lookup from elsewhere finds nothing).
    /// </summary>
    internal static Creature? SweepTarget(BotGroup group, Player player, uint now, Func<Creature, bool>? skip = null)
    {
        if (group.Handoff?.Sweep is not { } sweep || player.Map is not { } map || player.MapId != group.Goal.MapId) return null;
        PlayerbotClearTarget? next = sweep.Next(new Vector3(player.X, player.Y, player.Z),
            guid => map.FindObject(new ObjectGuid(guid)) is Creature creature && creature.IsInWorld
                ? (creature.IsAlive, new Vector3(creature.X, creature.Y, creature.Z)) : null,
            guid => group.IsShunned(new ObjectGuid(guid), now)
                || skip is not null && map.FindObject(new ObjectGuid(guid)) is Creature creature && skip(creature));
        return next is { } target ? map.FindObject(new ObjectGuid(target.Guid)) as Creature : null;
    }

    // --- the clear ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The conditions of a handoff, every tick for a handed-over group: the leader still leads the same server group, a real player online
    /// is still in it, and the leader (alive) has not been moved out of the instance while the group engages. True when the clear ended.
    /// </summary>
    private bool WatchHandoff(BotGroup group, Player leader, GroupManager? groupManager)
    {
        if (group.Handoff is not { } handoff || _world is not { } world) return false;
        string? reason = null;
        bool handBack = true;
        if (groupManager?.GetGroup(leader.Guid) is not { } server || server.Id != handoff.ServerGroupId) reason = "leader-left";
        else if (server.LeaderGuid != leader.Guid)
        {
            reason = IsRealOnline(server.LeaderGuid) ? "leader-taken-back" : "leader-passed";
            handBack = false;
        }
        else if (!server.Members.Any(m => world.FindOnlinePlayer(m.Guid) is { Session: WorldSession { IsManaged: false } })) reason = "player-left";
        else if (group.State == PlayerbotGroupState.Engaging && leader.IsAlive && leader.MapId != group.Goal.MapId) reason = "left-instance";
        if (reason is null) return false;
        EndHandoff(group, success: false, reason, handBackTo: handBack ? handoff.HandedBy : null);
        return true;
    }

    /// <summary>
    /// The Engaging state of a handed-over group without a catalogue route: clear the list nearest first; when it is empty (and once rebuilt
    /// from what is alive now), let the loot settle as a run does after its last boss, then the clear is done.
    /// </summary>
    private void AdvanceSweep(BotGroup group, Player leader, PlayerbotClearList sweep, uint now)
    {
        if (CheckWipe(group, leader)) return;
        if (leader.MapId != group.Goal.MapId || leader.Map is null) return; // outside (a corpse run): the leader walks back in first
        // Next drops the dead and the gone as it meets them, so a creature still on the list after a null target was skipped, not
        // finished: every one left is shunned for now (no path, a stalled fight). Wait for the shun to lapse; neither spend the one
        // rebuild (it would hold the same creatures) nor call the clear done while hostiles live.
        if (SweepTarget(group, leader, now) is not null || sweep.Remaining > 0 || !sweep.Rebuilt && sweep.Rebuild(ClearTargets(leader)))
        {
            group.RunDoneMs = 0;
            return;
        }

        if (group.RunDoneMs == 0) group.RunDoneMs = Math.Max(1u, now);
        bool busy = group.InFight(now) || group.Members.Any(m => m.AI is { Looting: true });
        if (busy && unchecked(now - group.RunDoneMs) <= LootSettleMs) return;
        Note(string.Create(CultureInfo.InvariantCulture, $"group {group.Id} cleared {group.Handoff!.InstanceName} ({sweep.Initial} on the list, {sweep.GivenUp} unreachable)"));
        EndHandoff(group, success: true, "completed", handBackTo: group.Handoff.HandedBy);
    }

    /// <summary>
    /// End a handed-over group's clear (done, or early with <paramref name="reason"/>): the leader says so in party or raid chat and passes the
    /// lead to <paramref name="handBackTo"/> (or the first real player online in the group) unless the lead already moved; every bot is
    /// released in place to its party AI, still in the player's group and in the instance (no leave, no disband, no instance reset).
    /// </summary>
    private void EndHandoff(BotGroup group, bool success, string reason, ObjectGuid? handBackTo)
    {
        if (group.Handoff is not { } handoff || !_groups.Contains(group)) return;
        group.Succeeded = success;
        group.Reason = success ? "completed" : reason;
        if (success) NoteSuccess(group);
        if (!success) group.Run?.Apply(PlayerbotRunSignal.Abort(reason));
        if (!_shuttingDown && _world is { } world && group.LeaderPlayer(world) is { } leader
            && _bots.TryGetValue(group.LeaderBotId, out PlayerbotGroupBot bot) && bot.Session.Player?.Guid == leader.Guid)
        {
            Say(bot.Session, leader, success ? PlayerbotLeaderHandoff.ClearedLine(handoff.InstanceName) : PlayerbotLeaderHandoff.StoppedLine(handoff.InstanceName, reason));
            if (handBackTo is { } wanted && GroupManagerOrNull()?.GetGroup(leader.Guid) is { } server && server.Id == handoff.ServerGroupId && server.IsLeader(leader.Guid))
            {
                ObjectGuid to = server.IsMember(wanted) && IsRealOnline(wanted) ? wanted
                    : server.Members.Select(m => m.Guid).FirstOrDefault(IsRealOnline);
                if (!to.IsEmpty) Act(bot.Session, WorldOpcode.CmsgGroupSetLeader, BitConverter.GetBytes(to.Value), budgeted: false);
            }
        }

        // Released with the server group left as it is: the bots stay in the player's group (Release's leave is for a bot group's own disband).
        foreach (Member member in group.Members.Where(m => !m.Real).ToArray()) Release(group, member, member.Guid);
        _groups.Remove(group);
        if (success) _handoffsCompleted++;
        else _handoffsEnded++;
        NoteRunEnded(group);
        Note($"group {group.Id} handed back to the party AI ({group.Reason})");
        logger.LogInformation("Playerbot leader handoff of group {Group} in {Instance} ended: {Reason}", group.Id, handoff.InstanceName, group.Reason);
    }

    /// <summary>
    /// The party AI asks before it acts on its master's chat (<see cref="PlayerbotPartyAI.GroupOwns"/>): true while a handed-over group drives
    /// <paramref name="bot"/>, so the party AI neither engages nor stops it. An order that moves or fights (<c>follow</c>, <c>stay</c>,
    /// <c>come</c>, <c>attack</c>, <c>stop</c>, <c>leave</c>; <see cref="PlayerbotLeaderHandoff.TakesTheLeadBack"/>) takes the lead back
    /// first: the clear ends, the lead goes to <paramref name="master"/>, and false lets the party AI carry the order out.
    /// </summary>
    internal bool OwnsAgainst(Player bot, Player master, PlayerbotPartyCommand? command)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(master);
        if (!TryFindBot(bot.Guid, out PlayerbotGroupBot found) || !_memberOf.TryGetValue(found.BotId, out BotGroup? group) || group.Handoff is null) return false;
        if (command is not { } order || !PlayerbotLeaderHandoff.TakesTheLeadBack(order)) return true;
        Note($"{master.Name} ordered {bot.Name} to {order.ToString().ToLowerInvariant()}: the lead goes back");
        EndHandoff(group, success: false, "called-back", handBackTo: master.Guid);
        return false;
    }

    /// <summary>The bot says <paramref name="text"/> in its group's chat (raid chat in a raid) through its own CMSG_MESSAGECHAT.</summary>
    private void Say(WorldSession session, Player bot, string text)
    {
        if (_shuttingDown || GroupManagerOrNull()?.GetGroup(bot.Guid) is not { } server) return;
        Language language = bot.Team == Team.Horde ? Language.Orcish : Language.Common;
        Act(session, WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(server.IsRaid ? ChatType.Raid : ChatType.Party, language, text), budgeted: false);
    }

    /// <summary>The handoff part of the <c>.playerbot groups</c> report.</summary>
    private string HandoffSummary() => string.Create(CultureInfo.InvariantCulture,
        $"leader handoffs: {_groups.Count(g => g.Handoff is not null)} leading, {_handoffsStarted} adopted, {_handoffsCompleted} cleared, {_handoffsEnded} ended early.");
}
