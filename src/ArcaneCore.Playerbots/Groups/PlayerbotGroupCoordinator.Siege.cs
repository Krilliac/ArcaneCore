using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Logging;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// The siege (docs/areas/playbots-groups.md, "Siege"): a GM-ordered march of one or two bot-led raids along stored waypoints to a creature
/// entry, and the objective's death. Everything here runs on the world thread and lives in memory only.
/// <para>
/// <b>Plan.</b> The waypoints are the invoker's positions (<see cref="AddSiegeWaypoint"/>). <see cref="StartSiege"/> splits the enlisted bots
/// over the raids (tanks, then healers, then damage, dealt round the raids; the leader is the best tank), forms each as an ordinary bot-led
/// group (the same invitations, raid conversion and subgroups) and goes straight to <see cref="PlayerbotGroupState.Engaging"/>. A siege raid
/// is always a raid, takes no slot of <c>MaxGroups</c> and has no goal timeout of its own: the GM stops it. A siege that makes no progress
/// for <see cref="PlayerbotSiegeCommander.StallMs"/> is reported stalled (<see cref="WatchProgress"/>), not ended.
/// </para>
/// <para>
/// <b>Command.</b> About every <see cref="SiegeTickMs"/> <see cref="TickSiege"/> reads each raid (health, lag, hostiles near its leader)
/// into <see cref="PlayerbotSiegeFacts"/>, advances the shared <see cref="PlayerbotSiege.Step"/> once every raid's leader stands at the
/// waypoint and none fights, asks <see cref="PlayerbotSiegeCommander.Decide"/> and applies the order: the skull and cross through the
/// leader's own MSG_RAID_TARGET_UPDATE, one raid warning when the order changed (<see cref="PlayerbotSiegeAnnouncer"/>), the leader's
/// destination and target for <see cref="PlayerbotGroupAI"/>. A lost fight is a retreat in two phases under one latch: the raid disengages to the
/// waypoint behind the step (<see cref="PlayerbotSiegeOrder.Retreat"/>), then stands there and fights back what followed it
/// (<see cref="PlayerbotSiegeOrder.Rally"/>); a retreat is counted once, where its latch begins, and begins once per engagement. The siege is won when the objective entry has been seen alive and none is while the grid at its last known position is loaded.
/// </para>
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>The most waypoints a siege may have.</summary>
    internal const int MaxSiegeWaypoints = 64;

    /// <summary>How often the raids are commanded.</summary>
    internal const uint SiegeTickMs = 400;

    /// <summary>A hurt member holds its raid back no longer than this (a bot that cannot be healed back must not stop the siege for good).</summary>
    internal const uint HurtHoldMs = 90_000;

    /// <summary>A leader waits this long, wherever it is, for a member more than <see cref="PlayerbotSiegeCommander.LagYards"/> behind (the regroup holds it, the lockstep and the credit wait); then the lag is waived (<see cref="PlayerbotSiegeFacts.LagWaived"/>) until the lag clears or the step changes.</summary>
    internal const uint LagHoldMs = 90_000;

    /// <summary>
    /// The longest a step lasts: a siege that has been heading for the same waypoint this long with every raid led and none in a retreat latch moves on by one step, whatever the leaders are doing
    /// (a permanent fight at a waypoint must not hold the siege for good). Counted from the step's start (<see cref="PlayerbotSiege.StepSinceMs"/>); one step per deadline.
    /// </summary>
    internal const uint StepDeadlineMs = 600_000;

    /// <summary>A living member below this health percentage is hurt.</summary>
    internal const float HurtPercent = 60f;

    /// <summary>How long the won siege waits for every leader's closing raid warning (the warnings are at least 3 s apart).</summary>
    internal const uint SiegeWinAnnounceMs = 4_000;

    private PlayerbotSiege? _siege = null;

    // The characters of the siege's raids as they were formed, published whole when the siege starts and cleared when it ends: the social memory reads it
    // from a map's thread (a PvP kill), so it is a set nothing changes after it is published.
    private volatile HashSet<ObjectGuid> _siegeMembers = [];

    /// <summary>Whether <paramref name="guid"/> belongs to a raid of the siege under way (any thread). A kill with either side in one is organised PvP, not a grudge.</summary>
    internal bool IsInSiegeGroup(ObjectGuid guid) => _siegeMembers.Contains(guid);

    private readonly List<Vector3> _siegeWaypoints = [];
    private uint _siegeWaypointMap = 0;
    private uint _siegeTickedMs = 0;
    private bool _siegeTicked = false;
    private int _nextSiegeId = 1;
    private readonly List<SiegeView> _siegeViews = [];
    private readonly List<ObjectGuid> _siegeDeadScratch = [];

    /// <summary>Raids of the siege still alive as groups whose leader cannot command them this tick (dead or gone): they still hold the others.</summary>
    private int _siegeUnled = 0;

    /// <summary>The siege under way, or null (tests, status and the GM commands).</summary>
    internal PlayerbotSiege? Siege => _siege;

    /// <summary>The waypoints a siege started now would follow, in order.</summary>
    internal IReadOnlyList<Vector3> SiegeWaypoints => _siegeWaypoints;

    /// <summary>The map the waypoints are on (0 with none).</summary>
    internal uint SiegeWaypointMap => _siegeWaypointMap;

    /// <summary>How the last siege ended: <c>won</c>, or <c>stopped: reason</c> (null: none has ended yet).</summary>
    internal string? SiegeOutcome { get; private set; }

    // --- waypoints ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Append a waypoint on <paramref name="mapId"/>. Null when added; else the refusal code: <c>siege-running</c>, <c>waypoint-invalid</c>
    /// (not finite), <c>waypoint-limit</c> (<see cref="MaxSiegeWaypoints"/>) or <c>waypoint-other-map</c> (the way stays on one map).
    /// </summary>
    internal string? AddSiegeWaypoint(uint mapId, Vector3 at)
    {
        if (_siege is not null) return "siege-running";
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return "waypoint-invalid";
        if (_siegeWaypoints.Count >= MaxSiegeWaypoints) return "waypoint-limit";
        if (_siegeWaypoints.Count > 0 && mapId != _siegeWaypointMap) return "waypoint-other-map";
        _siegeWaypointMap = mapId;
        _siegeWaypoints.Add(at);
        return null;
    }

    /// <summary>Forget the waypoints: how many there were, or -1 while a siege runs (it keeps its own copy, but the GM stops it first).</summary>
    internal int ClearSiegeWaypoints()
    {
        if (_siege is not null) return -1;
        int count = _siegeWaypoints.Count;
        _siegeWaypoints.Clear();
        _siegeWaypointMap = 0;
        return count;
    }

    // --- start and stop ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Start a siege with the bots <paramref name="botIds"/> in <paramref name="raids"/> bot-led raids after creature entry
    /// <paramref name="objectiveEntry"/>. Null with <paramref name="refusal"/> set when nothing started: <c>siege-running</c>,
    /// <c>no-waypoints</c>, <c>bad-raids</c>, <c>bad-objective</c>, <c>groups-unavailable</c>, <c>too-few-bots</c> (two for every raid). A bot that
    /// cannot join is added to <paramref name="skipped"/> with its reason (<c>not-running</c>, <c>busy</c>, <c>grouped</c>, <c>dead</c>,
    /// <c>other-map</c>, <c>in-group</c>, <c>other-team</c>, <c>raid-full</c>). World thread.
    /// </summary>
    internal PlayerbotSiege? StartSiege(IReadOnlyList<Guid> botIds, uint objectiveEntry, int raids, List<(Guid BotId, string Reason)> skipped, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(botIds);
        ArgumentNullException.ThrowIfNull(skipped);
        refusal = null;
        if (_siege is not null) { refusal = "siege-running"; return null; }
        if (_siegeWaypoints.Count == 0) { refusal = "no-waypoints"; return null; }
        if (raids is < 1 or > 2) { refusal = "bad-raids"; return null; }
        if (objectiveEntry == 0) { refusal = "bad-objective"; return null; }
        if (_world is null || GroupManagerOrNull() is not { } groups) { refusal = "groups-unavailable"; return null; }

        uint mapId = _siegeWaypointMap;
        var eligible = new List<(Guid BotId, PlayerbotGroupBot Bot, Player Player)>();
        foreach (Guid botId in botIds)
        {
            string? why = null;
            Player? player = null;
            if (!_bots.TryGetValue(botId, out PlayerbotGroupBot bot)) why = "not-running";
            else if (!bot.Free) why = "busy";
            else if (_memberOf.ContainsKey(botId)) why = "grouped";
            else if (bot.Session.Player is not { IsInWorld: true } found) why = "not-running";
            else if (!found.IsAlive || (found.Flags & PlayerFlags.Ghost) != 0) why = "dead";
            else if (found.MapId != mapId) why = "other-map";
            else if (groups.GetGroup(found.Guid) is not null || groups.GetInvite(found.Guid) is not null) why = "in-group";
            else player = found;
            if (why is not null || player is null) skipped.Add((botId, why ?? "not-running"));
            else eligible.Add((botId, bot, player));
        }

        // One team: the raids are server groups, and a group never holds both factions. The larger side enlists.
        if (eligible.Count > 0)
        {
            Team team = eligible.GroupBy(e => e.Player.Team).OrderByDescending(g => g.Count()).ThenBy(g => (int)g.Key).First().Key;
            for (int i = eligible.Count - 1; i >= 0; i--)
                if (eligible[i].Player.Team != team)
                {
                    skipped.Add((eligible[i].BotId, "other-team"));
                    eligible.RemoveAt(i);
                }
        }

        var candidates = eligible.Select(e => Candidate(e.BotId, e.Player, Now))
            .OrderByDescending(c => c.Level).ThenBy(c => c.BotId).ToList();
        int capacity = raids * Group.MaxRaidSize;
        while (candidates.Count > capacity)
        {
            skipped.Add((candidates[^1].BotId, "raid-full"));
            candidates.RemoveAt(candidates.Count - 1);
        }

        if (candidates.Count < raids * 2) { refusal = "too-few-bots"; return null; }

        // Tanks first, then healers, then the rest, dealt round the raids so each holds its share of every role.
        var buckets = new List<(PlayerbotGroupCandidate Bot, PlayerbotGroupRole Role)>[raids];
        for (int i = 0; i < raids; i++) buckets[i] = [];
        int dealt = 0;
        foreach (PlayerbotGroupRole role in new[] { PlayerbotGroupRole.Tank, PlayerbotGroupRole.Healer, PlayerbotGroupRole.Damage })
            foreach (PlayerbotGroupCandidate candidate in candidates.Where(c => PlayerbotGroupContent.Preferred(c.Class, c.TalentRole) == role))
                buckets[dealt++ % raids].Add((candidate, role));

        Vector3 last = _siegeWaypoints[^1];
        var plans = new List<PlayerbotGroupPlan>(raids);
        foreach (var bucket in buckets)
        {
            PlayerbotGroupCandidate leader = bucket.Where(m => m.Role == PlayerbotGroupRole.Tank).Select(m => m.Bot)
                .OrderByDescending(c => c.Level).ThenBy(c => c.BotId).FirstOrDefault()
                ?? bucket.Select(m => m.Bot).OrderByDescending(c => c.Level).ThenBy(c => c.BotId).First();
            int subgroups = Math.Max(1, (bucket.Count + PlayerbotGroupContent.PartySize - 1) / PlayerbotGroupContent.PartySize);
            var members = new List<(PlayerbotGroupCandidate, PlayerbotGroupRole, byte)>(bucket.Count);
            for (int k = 0; k < bucket.Count; k++) members.Add((bucket[k].Bot, bucket[k].Role, (byte)(k % subgroups)));
            plans.Add(new PlayerbotGroupPlan(leader, members));
        }

        Vector3 rally = plans[0].Leader.Position;
        var siege = new PlayerbotSiege(_nextSiegeId++, mapId, objectiveEntry, [.. _siegeWaypoints], rally, Now);
        for (int i = 0; i < plans.Count; i++)
        {
            PlayerbotGroupPlan plan = plans[i];
            foreach (var member in plan.Members)
            {
                Unhold(member.Bot.BotId, null);
                if (_bots.TryGetValue(member.Bot.BotId, out PlayerbotGroupBot raided)) raided.Brain.Stay = null; // the raid owns the bot now
            }
            var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Siege, 0, objectiveEntry, mapId, mapId, rally, 0, last, plan.Members.Count, 0, "siege");
            BotGroup group = Form(goal, plan);
            var raid = new PlayerbotSiegeRaid(siege, i + 1) { Group = group };
            group.Siege = raid;
            siege.Raids.Add(raid);
        }

        _siege = siege;
        _siegeMembers = [.. siege.Raids.Where(raid => raid.Group is not null).SelectMany(raid => raid.Group!.Members).Select(member => member.Guid)];
        SiegeOutcome = null;
        _siegeTicked = false;
        Note($"siege {siege.Id} started: {siege.Raids.Count} raids, {siege.Waypoints.Count} waypoints on map {mapId}, objective {objectiveEntry}");
        logger.LogInformation("Playerbot siege {Siege} started: {Raids} raids, {Count} bots, {Waypoints} waypoints on map {Map}, objective {Entry}",
            siege.Id, siege.Raids.Count, candidates.Count, siege.Waypoints.Count, mapId, objectiveEntry);
        return siege;
    }

    /// <summary>End the siege under way for <paramref name="reason"/> (the GM's command: <c>gm</c>). False when none runs.</summary>
    internal bool StopSiege(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (_siege is not { } siege) return false;
        EndSiege(siege, reason);
        return true;
    }

    private void EndSiege(PlayerbotSiege siege, string reason)
    {
        if (!ReferenceEquals(_siege, siege)) return;
        _siege = null;
        _siegeMembers = [];
        SiegeOutcome = reason == "won" ? "won" : "stopped: " + reason;
        foreach (PlayerbotSiegeRaid raid in siege.Raids)
            if (raid.Group is { } group && _groups.Contains(group) && group.State != PlayerbotGroupState.Disbanding)
            {
                // Only a won siege is a success the members remember; a stop (GM, event ended, raids ended) ends the groups the same way
                // but records no GroupSucceeded (marking the success as noted keeps every later end of the group from recording it).
                if (reason != "won") group.SuccessNoted = true;
                Finish(group, success: true, "siege-" + reason, setAside: false);
            }
        TellWatchers(siege, $"Siege {siege.Id} ended: {SiegeOutcome}");
        siege.Watchers.Clear();
        Note($"siege {siege.Id} ended: {SiegeOutcome}");
        logger.LogInformation("Playerbot siege {Siege} ended: {Outcome}", siege.Id, SiegeOutcome);
    }

    // --- a raid of the siege in the group's life ---------------------------------------------------------------------------

    /// <summary>
    /// The coordinator's part of a siege raid in <see cref="PlayerbotGroupState.Engaging"/> (instead of the quest group's wipe, goal and stall
    /// rules): a dead leader hands the raid to its best living member, a raid with nobody left alive is over. True when the group ended.
    /// </summary>
    private bool AdvanceSiegeRaid(BotGroup group, Player leader, uint now)
    {
        if (_world is not { } world || group.Siege is not { } raid) return false;
        bool anyAlive = false;
        foreach (Member member in group.Members)
            if (!member.Real && member.Joined && world.FindOnlinePlayer(member.Guid) is { IsAlive: true }) { anyAlive = true; break; }
        if (!anyAlive)
        {
            Note($"group {group.Id}: the siege raid has fallen");
            Finish(group, success: false, "siege-raid-fallen", setAside: false);
            return true;
        }

        if (!leader.IsAlive && unchecked(now - raid.PromoteTriedMs) >= 1_000)
        {
            raid.PromoteTriedMs = Math.Max(1u, now);
            PromoteLeader(group);
        }

        return false;
    }

    /// <summary>
    /// The siege raid lost its leader without the group ending (the bot stopped or left): the server group has passed the lead on by itself, so
    /// the raid follows the member who leads it now. False when no bot member of the raid leads it.
    /// </summary>
    private bool AdoptServerLeader(BotGroup group)
    {
        if (_world is not { } world || GroupManagerOrNull() is not { } groups) return false;
        foreach (Member member in group.Members)
        {
            if (member.Real || !member.Joined || member.BotId == group.LeaderBotId || world.FindOnlinePlayer(member.Guid) is null) continue;
            if (groups.GetGroup(member.Guid) is not { } server || !server.IsLeader(member.Guid)) continue;
            group.LeaderBotId = member.BotId;
            group.LeaderGuid = member.Guid;
            Note($"group {group.Id}: {member.Name} leads the siege raid");
            return true;
        }

        return false;
    }

    // --- status and watch ---------------------------------------------------------------------------------------------------

    /// <summary>How often a watching GM may be sent a changed raid line.</summary>
    internal const uint WatchGapMs = 2_000;

    private string LeaderNameOf(BotGroup group) => group.Find(group.LeaderBotId)?.Name ?? "?";

    /// <summary>
    /// The <c>.playerbot siege status</c> lines: the siege and one line per raid, or just the raid led by <paramref name="leaderName"/>
    /// (case-insensitive) with the last raid warnings it said. A single line says that no siege runs (and how the last one ended).
    /// </summary>
    internal IReadOnlyList<string> SiegeStatus(string? leaderName)
    {
        if (_siege is not { } siege)
            return SiegeOutcome is null ? ["Playerbot siege: none is running."] : ["Playerbot siege: none is running.", "Last siege: " + SiegeOutcome + "."];
        var lines = new List<string>();
        bool all = string.IsNullOrEmpty(leaderName);
        if (all)
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"Siege {siege.Id}: {siege.Raids.Count} raids, objective {siege.ObjectiveEntry}, step {siege.Step}/{siege.Waypoints.Count}, running {unchecked(Now - siege.StartedMs) / 1000}s{(siege.Won ? ", won" : string.Empty)}{(siege.ObjectiveUnseen ? ", objective unseen (its grid is not loaded)" : string.Empty)}{(siege.Stalled ? $", stalled: no progress for {unchecked(Now - siege.ProgressMs) / 1000}s" : string.Empty)}."));
        foreach (PlayerbotSiegeRaid raid in siege.Raids)
        {
            if (raid.Group is not { } group) continue;
            string name = LeaderNameOf(group);
            if (!all && !name.Equals(leaderName, StringComparison.OrdinalIgnoreCase)) continue;
            int total = 0, alive = 0;
            foreach (Member member in group.Members)
            {
                if (member.Real || !member.Joined) continue;
                total++;
                if (_world?.FindOnlinePlayer(member.Guid) is { IsAlive: true }) alive++;
            }

            string what = raid.Detail.Length > 0 ? raid.Detail : group.State.ToString().ToLowerInvariant();
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"raid {raid.Index} ({name}, {alive}/{total} alive): {what} retreats={raid.Retreats}"));
            if (!all) foreach (string said in raid.Announcer.Sent.TakeLast(3)) lines.Add("said: " + said);
        }

        if (!all && lines.Count == 0) lines.Add($"Playerbot siege: no raid led by '{leaderName}'.");
        return lines;
    }

    /// <summary>Watch (or stop watching) the siege as the GM <paramref name="gm"/>. False when no siege runs.</summary>
    internal bool SetSiegeWatch(ObjectGuid gm, bool on)
    {
        if (_siege is not { } siege) return false;
        if (on) siege.Watchers.Add(gm);
        else siege.Watchers.Remove(gm);
        return true;
    }

    private void TellWatchers(PlayerbotSiege siege, string line)
    {
        if (siege.Watchers.Count == 0 || _world is not { } world) return;
        foreach (ObjectGuid guid in siege.Watchers.ToArray())
        {
            if (world.FindOnlinePlayer(guid) is { IsInWorld: true } gm) gm.SendSystemMessage(line);
            else siege.Watchers.Remove(guid);
        }
    }

    /// <summary>Each raid whose order, step or strength changed since the watchers were last told gets one line (at most every <see cref="WatchGapMs"/>).</summary>
    private void NotifyWatchers(PlayerbotSiege siege, uint now)
    {
        if (siege.Watchers.Count == 0 || siege.WatchedMs != 0 && unchecked(now - siege.WatchedMs) < WatchGapMs) return;
        siege.WatchedMs = Math.Max(1u, now);
        foreach (SiegeView view in _siegeViews)
        {
            if (view.Raid.WatchLine == view.Raid.WatchedLine) continue;
            view.Raid.WatchedLine = view.Raid.WatchLine;
            string line = string.Create(CultureInfo.InvariantCulture, $"Siege {siege.Id} raid {view.Raid.Index} ({LeaderNameOf(view.Group)}): {view.Raid.WatchLine}");
            TellWatchers(siege, line);
        }
    }

    // --- the commander's tick ----------------------------------------------------------------------------------------------

    /// <summary>What the commander read of one raid this tick.</summary>
    private sealed class SiegeView(PlayerbotSiegeRaid raid, BotGroup group, Player leader, Group server)
    {
        public PlayerbotSiegeRaid Raid { get; } = raid;
        public BotGroup Group { get; } = group;
        public Player Leader { get; } = leader;
        public Group Server { get; } = server;
        public PlayerbotSiegeFacts Facts { get; set; }

        /// <summary>The hostiles within the pull range of the leader, those already fighting first, then the nearest first.</summary>
        public List<(Unit Unit, float Yards)> Hostiles { get; } = [];
    }

    private void TickSiegeSafely()
    {
        try
        {
            TickSiege();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.SiegeFault,
                string.Create(CultureInfo.InvariantCulture, $"siege {_siege?.Id ?? 0}"),
                string.Create(CultureInfo.InvariantCulture, $"siege {_siege?.Id ?? 0} failed during coordinator update ({ex.GetType().Name}: {ex.Message})"),
                _world?.Identity);
            logger.LogFault("playerbot.group-coordinator", ex, "Playerbot siege {Siege} failed during coordinator update", _siege?.Id ?? 0);
        }
    }

    private void TickSiege()
    {
        if (_siege is not { } siege || _world is not { } world) return;
        uint now = Now;
        if (_siegeTicked && unchecked(now - _siegeTickedMs) < SiegeTickMs) return;
        _siegeTicked = true;
        _siegeTickedMs = now;

        bool anyLive = false;
        bool allEngaging = true;
        foreach (PlayerbotSiegeRaid raid in siege.Raids)
        {
            if (raid.Group is not { } group || !_groups.Contains(group)) continue;
            anyLive = true;
            if (group.State != PlayerbotGroupState.Engaging) allEngaging = false;
        }

        if (!anyLive)
        {
            EndSiege(siege, "raids-ended");
            return;
        }

        if (!allEngaging) return; // a raid still forms: the siege waits for it

        // The forming time does not count towards the step deadline: its clock starts with the first tick that runs the step logic.
        if (!siege.StepClockStarted)
        {
            siege.StepClockStarted = true;
            siege.StepSinceMs = Math.Max(1u, now);
        }

        GroupManager? groups = GroupManagerOrNull();
        _siegeViews.Clear();
        _siegeUnled = 0;
        foreach (PlayerbotSiegeRaid raid in siege.Raids)
        {
            if (raid.Group is not { } group || !_groups.Contains(group)) continue;
            if (group.LeaderPlayer(world) is not { IsAlive: true, Map: not null } leader || groups?.GetGroup(leader.Guid) is not { } server)
            {
                // A raid without a leader holds the others at their waypoint until it has one again (or ends): it is not dropped from the plan.
                // Its engagement is still tracked, so the fight is not stale when a new leader takes over (the retreat latch is kept too).
                _siegeUnled++;
                TrackEngagement(group, raid, world, now);
                raid.Order = PlayerbotSiegeOrder.Hold;
                raid.Push = PlayerbotSiegePush.None;
                raid.PushInForce = PlayerbotSiegePush.None;
                raid.PullTarget = default;
                continue;
            }

            var view = new SiegeView(raid, group, leader, server);
            ReadRaid(siege, view, world, now);
            _siegeViews.Add(view);
        }

        // The step deadline pauses while a raid has no leader (it does not restart: a leader that dies again and again must not hold the step for good).
        siege.TrackLeaders(_siegeUnled > 0, now);
        if (_siegeViews.Count == 0) return;
        // The witness stands on the siege's map: a leader elsewhere sees another map's creatures and grids, which says nothing of the objective.
        // With none on the map the objective is not judged this tick.
        if (WatchObjective(siege, _siegeViews.FirstOrDefault(v => v.Leader.MapId == siege.MapId)?.Leader, now)) return;

        // Furthest-waypoint credit (CreditedStep): a lockstep that does not need the leaders at the waypoint at the same moment. Every raid's leader has stood at the waypoint the siege heads for
        // (or beyond it) at some time, so the step goes on by one, which keeps a defender post that drags the leaders off the waypoint from holding the siege at that step for good. It keeps the
        // lockstep's lag rule (no member more than LagYards behind its leader unless waived, and a waiver is spent by the one step it moves: SetStep restarts the lag clock) and is refused while a leader answers an attacker.
        // Lockstep: otherwise the step moves on once every raid's leader stands at the waypoint and nothing fights; a raid with a dead leader holds it. A raid
        // that pushes through its fight (PlayerbotSiegeCommander.PushOf) does not wait for the fight, but no raid moves the step while a living member
        // is more than LagYards behind its leader (the members of a pushing raid disengage and catch up).
        GrantPushes(siege);
        int credited = CreditedStep(siege);
        if (credited > siege.Step)
        {
            foreach (SiegeView view in _siegeViews)
                if (view.Facts.LagWaived) NoteLagWaived(siege, view, now);

            int from = siege.Step;
            SetStep(siege, credited);
            Note($"siege {siege.Id}: every raid has reached waypoint {from + 1}, step {from} -> {credited}");
            logger.LogInformation("Playerbot siege {Siege}: every raid has reached waypoint {Waypoint}, step {From} -> {To}", siege.Id, from + 1, from, credited);
        }
        else if (siege.Step < siege.Waypoints.Count && _siegeUnled == 0
            && _siegeViews.All(v => v.Facts.AtWaypointReal && (v.Facts.MaxLagYards <= PlayerbotSiegeCommander.LagYards || v.Facts.LagWaived)
                && (!v.Facts.InCombat || PlayerbotSiegeCommander.PushOf(v.Facts) != PlayerbotSiegePush.None)))
        {
            foreach (SiegeView view in _siegeViews)
                if (view.Facts.LagWaived) NoteLagWaived(siege, view, now);

            SetStep(siege, siege.Step + 1);
        }

        // The per-step deadline: no fight, lag or loop at a waypoint holds the siege for good. One step, then the clock starts afresh.
        if (StepDeadlineDue(siege, _siegeUnled, _siegeViews.Any(v => v.Raid.RetreatLatched), now))
        {
            int from = siege.Step;
            SetStep(siege, from + 1);
            Note($"siege {siege.Id}: waypoint {from + 1} not taken in {StepDeadlineMs / 1000}s, moving on to waypoint {from + 2}");
            logger.LogWarning("Playerbot siege {Siege}: waypoint {Waypoint} not taken in {Seconds}s, moving on to waypoint {Next}", siege.Id, from + 1, StepDeadlineMs / 1000, from + 2);
            string deadline = string.Create(CultureInfo.InvariantCulture, $"Waypoint {from + 1} not taken in {StepDeadlineMs / 1000}s, moving on");
            foreach (SiegeView view in _siegeViews)
            {
                view.Raid.Announcer.Force(deadline, now); // not dropped by the gap rule: a step change by the deadline is rare and the raid should hear it
                RaidWarning(view.Group, view.Leader, deadline);
            }
        }

        WatchProgress(siege, now);
        foreach (SiegeView view in _siegeViews) Command(siege, view, now);
        NotifyWatchers(siege, now);
    }

    /// <summary>
    /// Whether the step has lasted <see cref="StepDeadlineMs"/> and the siege moves on: a waypoint still ahead, every raid led (<paramref name="unled"/> raids without a leader hold it), none in a retreat latch
    /// (<paramref name="retreatLatched"/>: a lost fight is not a reason to go on). The clock (<see cref="PlayerbotSiege.StepSinceMs"/>) restarts where progress was impossible: the siege's first tick past forming,
    /// and the end of a retreat latch (a latch is bounded by the retreat limit); the time a raid spends without a leader is paused (<see cref="PlayerbotSiege.TrackLeaders"/>), not restarted.
    /// </summary>
    internal static bool StepDeadlineDue(PlayerbotSiege siege, int unled, bool retreatLatched, uint now)
        => siege.Step < siege.Waypoints.Count && unled == 0 && !retreatLatched && unchecked(now - siege.StepSinceMs) >= StepDeadlineMs;

    /// <summary>The step moves to <paramref name="step"/>: every raid starts it afresh (no answer, no waypoint reading, clocks of the step).</summary>
    private void SetStep(PlayerbotSiege siege, int step)
    {
        siege.Step = step;
        siege.StepSinceMs = Math.Max(1u, Now);
        foreach (SiegeView view in _siegeViews)
        {
            view.Raid.AtWaypoint = false;
            view.Raid.AtWaypointReal = false;
            view.Raid.EndAnswer();
            view.Raid.LagSinceMs = 0; // a lag waiver is spent by the step it moves: the next one waits LagHoldMs afresh, even where the next waypoint lies in the same disk
            view.Facts = view.Facts with
            {
                AtWaypoint = false, AtWaypointReal = false, PeersAtWaypoint = true, MsInCombatAtStep = 0, MsQuietInCombat = 0, ObjectiveLeg = OnFinalLeg(siege), PushBlocked = false, LagWaived = false,
            };
        }
    }

    /// <summary>
    /// The step the furthest-waypoint credit moves the siege to, or -1 for none: always the next step, and only when every led raid's leader has stood at the waypoint of the step or beyond it
    /// (<see cref="PlayerbotSiegeRaid.FurthestWaypoint"/>) and at least one leader is not at it now (where all are, the lockstep and its combat and push rules decide), and no raid blocks it
    /// (<see cref="BlocksCredit"/>). No credit with an unled raid, nor past the last waypoint.
    /// </summary>
    private int CreditedStep(PlayerbotSiege siege)
    {
        if (_siegeUnled != 0 || _siegeViews.Count == 0 || siege.Step >= siege.Waypoints.Count) return -1;
        bool allAtNow = true;
        foreach (SiegeView view in _siegeViews)
        {
            if (view.Raid.FurthestWaypoint < siege.Step || BlocksCredit(view.Facts, view.Raid)) return -1;
            allAtNow &= view.Facts.AtWaypointReal;
        }

        return allAtNow ? -1 : siege.Step + 1;
    }

    /// <summary>
    /// A raid blocks the furthest-waypoint credit while its leader answers an attacker (<see cref="PlayerbotSiegeRaid.AnsweringStep"/>) or a living member is more than <see cref="PlayerbotSiegeCommander.LagYards"/> behind
    /// it and the lag is not waived (<see cref="PlayerbotSiegeFacts.LagWaived"/>: the lockstep's rule, a waiver being spent by the step it moves, which restarts the lag clock).
    /// </summary>
    internal static bool BlocksCredit(in PlayerbotSiegeFacts facts, PlayerbotSiegeRaid raid)
        => raid.AnsweringStep >= 0 || (facts.MaxLagYards > PlayerbotSiegeCommander.LagYards && !facts.LagWaived);

    /// <summary>Raises <paramref name="raid"/>'s furthest waypoint to the highest waypoint at or beyond <paramref name="step"/> that a leader at <paramref name="leader"/> stands at (level-aware, <see cref="IsAtWaypoint"/>).</summary>
    internal static void NoteFurthestWaypoint(PlayerbotSiegeRaid raid, IReadOnlyList<Vector3> waypoints, int step, Vector3 leader)
    {
        for (int j = waypoints.Count - 1; j >= Math.Max(step, 0) && j > raid.FurthestWaypoint; j--)
            if (IsAtWaypoint(leader, waypoints[j]))
            {
                raid.FurthestWaypoint = j;
                return;
            }
    }

    /// <summary>The leader is within the answer's reach of the waypoint: the leash plus the disk in the plane, and on the waypoint's level (<see cref="PlayerbotSiegeCommander.WaypointLevelYards"/>).</summary>
    internal static bool WithinAnswerReach(Vector3 leader, Vector3 waypoint)
    {
        float dx = leader.X - waypoint.X, dy = leader.Y - waypoint.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy)) <= PlayerbotSiegeCommander.SiegeAnswerLeashYards + PlayerbotSiegeCommander.WaypointYards
            && MathF.Abs(leader.Z - waypoint.Z) <= PlayerbotSiegeCommander.WaypointLevelYards;
    }

    /// <summary>
    /// Reads the leader's answer for the raid and returns whether the leader counts as at the waypoint of <paramref name="step"/> because of it. An answer begins only from a real reading (<paramref name="real"/>:
    /// <see cref="IsAtWaypoint"/>, level-aware) at this step, for this leader, while the raid pushes, and when the leader's group AI has asked for it (<see cref="PlayerbotSiegeRaid.AnswerRequestedMs"/>, fresh for
    /// <see cref="PlayerbotSiegeCommander.AnswerFreshMs"/>). It lasts until the leader is back in the disk and no longer fighting, and is bound: it ends when the leader leaves the reach (<see cref="WithinAnswerReach"/>) or
    /// after <see cref="LagHoldMs"/> spent out of the disk on the step (<see cref="PlayerbotSiegeRaid.AnswerOutMs"/>: per step, not per excursion, and fighting inside the disk costs none of it; once spent, the step has no further answer). The request must come from this leader. It belongs to its step and its leader (another step, another leader or the end of the push end it), so it neither carries over to the next waypoint nor sustains itself.
    /// </summary>
    internal static bool UpdateAnswer(PlayerbotSiegeRaid raid, ObjectGuid leaderGuid, int step, Vector3 leader, Vector3 waypoint, bool real, uint now)
    {
        bool requested = raid.AnswerRequestedMs != 0 && raid.AnswerRequestedLeader == leaderGuid && unchecked((int)(now - raid.AnswerRequestedMs)) <= PlayerbotSiegeCommander.AnswerFreshMs;
        if (raid.AnsweringStep >= 0 && (!raid.PushingThrough || raid.AnsweringStep != step || raid.AnsweringLeader != leaderGuid)) raid.EndAnswer();
        if (raid.AnswerClockStep != step)
        {
            raid.AnswerClockStep = step;
            raid.AnswerOutMs = 0;
            raid.AnswerLastMs = 0;
        }

        // Only the time spent out of the disk answers counts against the step's bound: a leader fighting inside the disk answers nothing out there.
        bool answering = raid.AnsweringStep >= 0;
        if (answering && !real && raid.AnswerLastMs != 0) raid.AnswerOutMs += unchecked(now - raid.AnswerLastMs);
        bool spent = raid.AnswerOutMs >= LagHoldMs;
        raid.AnswerSpent = spent;
        if (!answering)
        {
            if (!raid.PushingThrough || !real || !requested || spent) return false;
            raid.AnsweringStep = step;
            raid.AnsweringLeader = leaderGuid;
            raid.AnswerLastMs = Math.Max(1u, now);
            return true;
        }

        if (real && !requested)
        {
            raid.EndAnswer();
            return true;
        }

        if (!WithinAnswerReach(leader, waypoint) || spent)
        {
            raid.EndAnswer();
            return real;
        }

        raid.AnswerLastMs = Math.Max(1u, now);
        return true;
    }

    /// <summary>
    /// Whether a leader at <paramref name="leader"/> stands at <paramref name="waypoint"/>: within <see cref="PlayerbotSiegeCommander.WaypointYards"/> in the plane, and on its level (no more than
    /// <see cref="PlayerbotSiegeCommander.WaypointLevelYards"/> apart in height). A lift-top waypoint has the same XY as the floor under it (up to about 100 yards below in the Undercity, whose levels are about 20 yards apart), and a leader
    /// on the wrong level is not at it.
    /// </summary>
    internal static bool IsAtWaypoint(Vector3 leader, Vector3 waypoint)
    {
        float dx = leader.X - waypoint.X, dy = leader.Y - waypoint.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy)) <= PlayerbotSiegeCommander.WaypointYards && MathF.Abs(leader.Z - waypoint.Z) <= PlayerbotSiegeCommander.WaypointLevelYards;
    }

    /// <summary>The step heads for the last waypoint or beyond it: the final leg to the objective, where no raid is pushed through (<see cref="PlayerbotSiegeFacts.ObjectiveLeg"/>).</summary>
    private static bool OnFinalLeg(PlayerbotSiege siege) => siege.Step >= siege.Waypoints.Count - 1;

    /// <summary>
    /// A push is a decision of the siege, not of one raid: a raid that qualifies (<see cref="PlayerbotSiegeCommander.PushOf"/>) is granted it unless
    /// (1) another raid is in combat and does not qualify (the lockstep would wait for that fight, and the pushing leader would idle among its hostiles), or
    /// (2) its leader stands at the waypoint and the lockstep cannot leave it because a peer is not there (or a raid is unled). A push held back by a
    /// member's lag stays granted: the leader waits at the waypoint, fights back what attacks it, and the members catch up (for at most <see cref="LagHoldMs"/>). A push in force (<see cref="PlayerbotSiegeRaid.PushInForce"/>) is held, not lost, while it is blocked, but a qualification that is blocked starts no push (<see cref="NextPushInForce"/>); a withdrawn push (<see cref="PlayerbotSiegeRaid.PushDeniedStep"/>) is blocked for its step.
    /// </summary>
    private void GrantPushes(PlayerbotSiege siege)
    {
        int count = _siegeViews.Count;
        Span<bool> qualifies = count <= 8 ? stackalloc bool[count] : new bool[count];
        for (int i = 0; i < count; i++)
        {
            _siegeViews[i].Facts = _siegeViews[i].Facts with { PushBlocked = _siegeViews[i].Raid.PushDeniedStep == siege.Step };
            qualifies[i] = PlayerbotSiegeCommander.PushOf(_siegeViews[i].Facts) != PlayerbotSiegePush.None;
        }

        for (int i = 0; i < count; i++)
        {
            if (!qualifies[i]) continue;
            bool blocked = false;
            bool peersAtWaypoint = true;
            for (int j = 0; j < count; j++)
            {
                if (j == i) continue;
                if (_siegeViews[j].Facts.InCombat && !qualifies[j]) blocked = true;
                if (!_siegeViews[j].Facts.AtWaypoint) peersAtWaypoint = false;
            }

            if (_siegeViews[i].Facts.AtWaypoint && (!peersAtWaypoint || _siegeUnled > 0 || siege.Step >= siege.Waypoints.Count)) blocked = true;
            if (blocked) _siegeViews[i].Facts = _siegeViews[i].Facts with { PushBlocked = true };
        }
    }

    /// <summary>
    /// The stall watch: a siege whose step has not moved and whose leaders have got no nearer to the step goal (<see cref="NoteGoalProgress"/>) for <see cref="PlayerbotSiegeCommander.StallMs"/> is stalled.
    /// It is reported once (event note, log, a raid warning from each led raid) and shown by <see cref="SiegeStatus"/>; a step or progress ends it.
    /// </summary>
    private void WatchProgress(PlayerbotSiege siege, uint now)
    {
        // A retreat, and the fight at its rally point, is no progress; nor is the hold of a push for the peer, nor a fight in place. A leader that gets nearer to its goal counts, fighting or not,
        // and so does a fight at the objective and a pushing leader that is getting nearer.
        bool progress = _siegeViews.Any(v => ShowsProgress(v.Facts, v.Raid, now));
        if (!NoteSiegeProgress(siege, progress, now)) return;
        string where = siege.Step < siege.Waypoints.Count ? string.Create(CultureInfo.InvariantCulture, $"waypoint {siege.Step + 1}") : "the objective";
        Note($"siege {siege.Id}: no progress towards {where} for {PlayerbotSiegeCommander.StallMs / 1000}s");
        logger.LogWarning("Playerbot siege {Siege} stalled: no progress towards {Where} for {Seconds}s", siege.Id, where, PlayerbotSiegeCommander.StallMs / 1000);
        string line = "Stalled: no progress towards " + where;
        foreach (SiegeView view in _siegeViews)
            if (view.Raid.Announcer.Offer(line, now)) RaidWarning(view.Group, view.Leader, line);
    }

    /// <summary>
    /// The stall clock: progress (or a step change, or the first read) restarts it; <see cref="PlayerbotSiegeCommander.StallMs"/> without either marks the siege stalled. True at the read that does so (once per stall).
    /// </summary>
    internal static bool NoteSiegeProgress(PlayerbotSiege siege, bool progress, uint now)
    {
        if (siege.ProgressMs == 0 || siege.Step != siege.ProgressStep || progress)
        {
            siege.ProgressMs = Math.Max(1u, now);
            siege.ProgressStep = siege.Step;
            siege.Stalled = false;
            return false;
        }

        if (siege.Stalled || unchecked(now - siege.ProgressMs) < PlayerbotSiegeCommander.StallMs) return false;
        siege.Stalled = true;
        return true;
    }

    /// <summary>Read one raid into <see cref="PlayerbotSiegeFacts"/> and its hostiles.</summary>
    private void ReadRaid(PlayerbotSiege siege, SiegeView view, WorldRuntime world, uint now)
    {
        BotGroup group = view.Group;
        PlayerbotSiegeRaid raid = view.Raid;
        Player leader = view.Leader;
        int total = 0, alive = 0;
        bool hasTank = false, tankAlive = false, anyResting = false, anyHurt = false;
        float healthSum = 0f, maxLag = 0f;
        bool objectiveEngaged = false;
        bool inCombat = TrackEngagement(group, raid, world, now);
        foreach (Member member in group.Members)
        {
            if (member.Real || !member.Joined) continue;
            total++;
            bool tank = member.Role == PlayerbotGroupRole.Tank;
            hasTank |= tank;
            if (world.FindOnlinePlayer(member.Guid) is not { IsAlive: true } player) continue;

            alive++;
            tankAlive |= tank;
            healthSum += player.HealthPercent;
            objectiveEngaged |= player.Combat.Victim is Creature { } victim && victim.Entry == siege.ObjectiveEntry;
            anyResting |= member.AI is { Resting: true };
            anyHurt |= player.HealthPercent < HurtPercent;
            if (player.Guid != leader.Guid) maxLag = Math.Max(maxLag, ReferenceEquals(player.Map, leader.Map) ? Distance(player, leader) : 1_000f);
        }

        int deadSince = 0;
        foreach (ObjectGuid dead in _siegeDeadScratch)
            if (!raid.DeadAtEngagement.Contains(dead)) deadSince++;

        uint sinceCombat = inCombat ? 0u : group.LastCombatMs == 0 ? uint.MaxValue : unchecked(now - group.LastCombatMs);

        // A hurt member holds the raid back for a while; one that stays hurt (nobody heals it) no longer does.
        if (anyHurt && !inCombat)
        {
            if (raid.HurtSinceMs == 0) raid.HurtSinceMs = Math.Max(1u, now);
            anyHurt = unchecked(now - raid.HurtSinceMs) <= HurtHoldMs;
        }
        else if (!anyHurt) raid.HurtSinceMs = 0;
        else anyHurt = false;

        // The route of the step: from the waypoint behind it (where the siege began, for the first) to its goal. Hostiles off it do not count, and a leader off it advances before it pulls.
        var leaderPos = new Vector3(leader.X, leader.Y, leader.Z);
        Vector3 routeFrom = RetreatPointOf(siege);
        Vector3 goal = siege.Step < siege.Waypoints.Count ? siege.Waypoints[siege.Step] : ObjectivePointOf(siege, leader);
        bool offRoute = leader.MapId == siege.MapId && RouteYards(leaderPos, routeFrom, goal) > PlayerbotSiegeCommander.RouteTetherYards;
        ScanHostiles(group, view, now, routeFrom, goal);
        float nearest = view.Hostiles.Count == 0 ? float.PositiveInfinity : view.Hostiles.Min(h => h.Yards);

        bool atWaypoint = false;
        bool atWaypointReal = false;
        if (siege.Step < siege.Waypoints.Count)
        {
            Vector3 leaderAt = leaderPos;
            // A leader that left the waypoint's disk to answer an attacker of its own (within the leash) is still at the waypoint for the lag hold and the no-progress clock until it is back.
            // Only the real reading lets the lockstep advance; the answer serves the lag hold and the no-progress clock.
            bool real = IsAtWaypoint(leaderAt, siege.Waypoints[siege.Step]);
            atWaypointReal = real;
            atWaypoint = UpdateAnswer(raid, leader.Guid, siege.Step, leaderAt, siege.Waypoints[siege.Step], real, now) || real;
            if (leader.MapId == siege.MapId) NoteFurthestWaypoint(raid, siege.Waypoints, siege.Step, leaderAt);
        }
        else
        {
            raid.EndAnswer();
        }

        raid.AtWaypoint = atWaypoint;
        raid.AtWaypointReal = atWaypointReal;
        (uint stepCombatMs, uint quietCombatMs) = ReadClocks(siege, raid, inCombat, sinceCombat, view.Hostiles.Count, now);

        // The push is bound by progress: a pushing leader that gets no nearer to its goal for PushStallMs has the push withdrawn for this step.
        float gx = leader.X - goal.X, gy = leader.Y - goal.Y;
        float goalYards = MathF.Sqrt((gx * gx) + (gy * gy));
        NoteGoalProgress(raid, siege.Step, goalYards, PlayerbotElevator.IsTraveling(leader), now);
        if (NotePushProgress(raid, siege.Step, goalYards, atWaypoint, PlayerbotElevator.IsTraveling(leader), now))
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"siege {siege.Id} raid {siege.Raids.IndexOf(raid)}: push withdrawn, the leader got no nearer to waypoint {siege.Step + 1} for {PlayerbotSiegeCommander.PushStallMs / 1000}s"));
            logger.LogWarning("Playerbot siege {Siege} raid {Raid}: push withdrawn, the leader got no nearer to waypoint {Waypoint} for {Seconds}s", siege.Id, siege.Raids.IndexOf(raid), siege.Step + 1, PlayerbotSiegeCommander.PushStallMs / 1000);
        }

        // A member more than LagYards behind its leader holds the raid back for LagHoldMs wherever it is (pushing or not, at the waypoint or on the way), then the lag is waived: the regroup
        // ends and the lockstep and the credit go on without the straggler. A step change restarts the clock (SetStep).
        if (maxLag > PlayerbotSiegeCommander.LagYards)
        {
            if (raid.LagSinceMs == 0) raid.LagSinceMs = Math.Max(1u, now);
        }
        else raid.LagSinceMs = 0;
        bool lagWaived = raid.LagSinceMs != 0 && unchecked(now - raid.LagSinceMs) >= LagHoldMs;
        bool denied = raid.PushDeniedStep == siege.Step;

        // A new leader has its own path: the no-path rally is not inherited (and its time at the point restarts, once per latch). A rally at a real
        // retreat point keeps its time.
        if (raid.LeaderSeen != group.LeaderBotId)
        {
            raid.LeaderSeen = group.LeaderBotId;
            raid.EndNoPathRally();
            raid.EndAnswer();
        }

        // The leader at the retreat point (the waypoint behind the step, else where the siege began) turns a retreat into a rally.
        Vector3 retreatPoint = RetreatPointOf(siege);
        float rx = leader.X - retreatPoint.X, ry = leader.Y - retreatPoint.Y;
        // A leader that cannot plan a way to the point (it stands boxed in) rallies where it is: the raid fights back and the bound applies.
        bool atRally = MathF.Sqrt((rx * rx) + (ry * ry)) <= PlayerbotSiegeCommander.RallyYards || raid.RetreatNoPath;
        uint msAtRally = raid.RallySinceMs == 0 ? 0u : unchecked(now - raid.RallySinceMs);
        uint msRetreating = raid.RetreatSinceMs == 0 ? 0u : unchecked(now - raid.RetreatSinceMs);
        // The skull the commander last set on the objective counts as fighting it too.
        if (!objectiveEngaged && !raid.PullTarget.IsEmpty)
            foreach ((Unit unit, float _) in view.Hostiles)
                if (unit.Guid == raid.PullTarget && unit is Creature { } skulled && skulled.Entry == siege.ObjectiveEntry) { objectiveEngaged = true; break; }

        view.Facts = new PlayerbotSiegeFacts(inCombat, sinceCombat, total, alive, hasTank, tankAlive, total == 0 ? 0f : healthSum / total, maxLag, anyResting, anyHurt,
            atWaypoint, PeersAtWaypoint: true, view.Hostiles.Count, nearest, CrossValid: false, raid.RetreatLatched, raid.PullLatched,
            deadSince, raid.Retreats, objectiveEngaged, atRally, msAtRally, raid.RetreatedThisEngagement,
            stepCombatMs, quietCombatMs, raid.LastLossMs == 0 ? uint.MaxValue : unchecked(now - raid.LastLossMs), OnFinalLeg(siege), denied,
            raid.HostilesAtStepStart, denied ? PlayerbotSiegePush.None : raid.PushInForce, lagWaived, atWaypointReal, offRoute, MsRetreating: msRetreating);
        Unit? cross = IconUnit(view, PlayerbotSiegeCommander.CrossIcon);
        view.Facts = view.Facts with { CrossValid = cross is not null };
        raid.LastFacts = view.Facts;
    }

    /// <summary>
    /// The two clocks of the push-through rules (<see cref="PlayerbotSiegeCommander.PushOf"/>), advanced once per read of a led raid.
    /// <b>Step clock</b> (<see cref="PlayerbotSiegeFacts.MsInCombatAtStep"/>): starts at the first read in combat at a step; a step change resets it
    /// (and it starts again at once if the raid is in combat); <see cref="PlayerbotSiegeCommander.StepClockQuietMs"/> out of combat (the raid's
    /// <paramref name="sinceCombat"/>) resets it; a shorter gap does not. <b>Quiet clock</b> (<see cref="PlayerbotSiegeFacts.MsQuietInCombat"/>):
    /// runs while the raid is in combat with no hostile counted near its leader, and stops at the first read that is not.
    /// </summary>
    internal static (uint StepCombatMs, uint QuietCombatMs) ReadClocks(PlayerbotSiege siege, PlayerbotSiegeRaid raid, bool inCombat, uint sinceCombat, int hostiles, uint now)
    {
        if (raid.ClockStep != siege.Step)
        {
            raid.ClockStep = siege.Step;
            raid.StepCombatSinceMs = 0;
            raid.QuietCombatSinceMs = 0;
        }

        if (inCombat)
        {
            if (raid.StepCombatSinceMs == 0)
            {
                raid.StepCombatSinceMs = Math.Max(1u, now);
                raid.HostilesAtStepStart = hostiles;
            }
        }
        else if (sinceCombat >= PlayerbotSiegeCommander.StepClockQuietMs) raid.StepCombatSinceMs = 0;

        if (inCombat && hostiles == 0)
        {
            if (raid.QuietCombatSinceMs == 0) raid.QuietCombatSinceMs = Math.Max(1u, now);
        }
        else raid.QuietCombatSinceMs = 0;

        return (raid.StepCombatSinceMs == 0 ? 0u : unchecked(now - raid.StepCombatSinceMs),
            raid.QuietCombatSinceMs == 0 ? 0u : unchecked(now - raid.QuietCombatSinceMs));
    }

    /// <summary>
    /// The no-progress bound of a push, advanced once per read of a led raid. While the raid pushes (<see cref="PlayerbotSiegeRaid.PushingThrough"/>) the leader's 2D
    /// distance to its step goal must set a new least value (by more than <see cref="PlayerbotSiegeCommander.PushProgressYards"/>) within
    /// <see cref="PlayerbotSiegeCommander.PushStallMs"/>; standing at the waypoint, or an elevator trip of the leader at any stage (<paramref name="riding"/>: a lift wait and ride gain no 2D yards, both levels share XY; the elevator has its own bounds: <see cref="PlayerbotElevator.NoProgressMs"/> on the walk to the stop, two cycles and 30 s waiting for the car, a cycle and 30 s aboard, dropped at two cycles and 30 s if the car never stands anywhere), counts as progress (the lockstep, the lag hold and its own bound govern at the waypoint). A push
    /// that does not is withdrawn: <see cref="PlayerbotSiegeRaid.PushInForce"/> ends and the step is denied a push (<see cref="PlayerbotSiegeRaid.PushDeniedStep"/>), so the
    /// raid is back at Assault, which is the baseline. A raid that does not push tracks nothing. Returns true at the read that withdraws the push.
    /// </summary>
    internal static bool NotePushProgress(PlayerbotSiegeRaid raid, int step, float yardsToGoal, bool atWaypoint, bool riding, uint now)
    {
        if (!raid.PushingThrough)
        {
            raid.PushBestYards = -1f;
            raid.PushProgressStep = -1;
            return false;
        }

        if (raid.PushProgressStep != step)
        {
            raid.PushProgressStep = step;
            raid.PushBestYards = -1f;
        }

        if (atWaypoint || riding || raid.PushBestYards < 0f || yardsToGoal < raid.PushBestYards - PlayerbotSiegeCommander.PushProgressYards)
        {
            raid.PushBestYards = atWaypoint ? 0f : yardsToGoal;
            raid.PushProgressMs = Math.Max(1u, now);
            return false;
        }

        if (unchecked(now - raid.PushProgressMs) < PlayerbotSiegeCommander.PushStallMs) return false;
        raid.PushDeniedStep = step;
        raid.PushInForce = PlayerbotSiegePush.None;
        raid.PushBestYards = -1f;
        return true;
    }

    /// <summary>
    /// The push the raid has in force after this decision. A push <b>begins only from a granted one</b> (<paramref name="granted"/>, <see cref="PlayerbotSiegeDecision.Push"/>): a
    /// qualification the siege refused (<see cref="GrantPushes"/>) starts nothing, so it cannot be carried later past the health, loss and hostile-count checks it passed only at that
    /// moment. A push already in force survives a block of the grant (a hold at the waypoint for the peer) while it still passes <see cref="PlayerbotSiegeCommander.PushOf"/>'s exclusions;
    /// the denial of its step (<paramref name="denied"/>) ends it. Everything else: none.
    /// </summary>
    internal static PlayerbotSiegePush NextPushInForce(PlayerbotSiegePush inForce, PlayerbotSiegePush granted, in PlayerbotSiegeFacts facts, bool denied)
    {
        if (granted != PlayerbotSiegePush.None) return granted;
        if (inForce != PlayerbotSiegePush.None && PlayerbotSiegeCommander.PushOf(facts with { PushBlocked = denied, Pushing = denied ? PlayerbotSiegePush.None : inForce }) != PlayerbotSiegePush.None)
            return inForce;
        return PlayerbotSiegePush.None;
    }

    /// <summary>
    /// The goal-distance clock of the stall watch, advanced once per read of a led raid: the leader's 2D distance to its step goal must set a new least value (by more than
    /// <see cref="PlayerbotSiegeCommander.PushProgressYards"/>, as <see cref="NotePushProgress"/> asks of a push) to count; a new step starts afresh, and so does a lift ride (<paramref name="riding"/>: both levels share XY).
    /// <see cref="PlayerbotSiegeRaid.GoalImprovedMs"/> is the time of the last such read. Fighting or not does not matter: a fight in place gets no nearer. True at a read that improves.
    /// </summary>
    internal static bool NoteGoalProgress(PlayerbotSiegeRaid raid, int step, float yardsToGoal, bool riding, uint now)
    {
        if (raid.GoalStep != step || raid.GoalBestYards < 0f || riding || yardsToGoal < raid.GoalBestYards - PlayerbotSiegeCommander.PushProgressYards)
        {
            raid.GoalStep = step;
            raid.GoalBestYards = yardsToGoal;
            raid.GoalImprovedMs = Math.Max(1u, now);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether one led raid shows progress to the stall watch (<see cref="WatchProgress"/>): its leader got nearer to the step goal at this read (<see cref="NoteGoalProgress"/>: a fight that gets no nearer is no progress
    /// however long it runs, a walk or a fight on the way that does is) or a member fights the objective, in neither case in a retreat latch nor, in combat, with a push in force; or it pushes and its leader is getting nearer
    /// (<see cref="PlayerbotSiegeRaid.PushProgressMs"/> within <see cref="PlayerbotSiegeCommander.PushStallMs"/>). A hold of a push for the peer is no progress.
    /// </summary>
    internal static bool ShowsProgress(in PlayerbotSiegeFacts facts, PlayerbotSiegeRaid raid, uint now)
    {
        bool nearer = raid.GoalImprovedMs != 0 && unchecked(now - raid.GoalImprovedMs) < SiegeTickMs;
        return (!raid.RetreatLatched && (facts.InCombat ? raid.PushInForce == PlayerbotSiegePush.None && (facts.ObjectiveEngaged || nearer) : nearer))
            || (raid.PushingThrough && raid.PushProgressMs != 0 && unchecked(now - raid.PushProgressMs) < PlayerbotSiegeCommander.PushStallMs);
    }

    /// <summary>The lockstep goes on without a straggler: the event note, the log line and the raid warning.</summary>
    private void NoteLagWaived(PlayerbotSiege siege, SiegeView view, uint now)
    {
        int index = siege.Raids.IndexOf(view.Raid);
        Note(string.Create(CultureInfo.InvariantCulture, $"siege {siege.Id} raid {index}: pushing on from waypoint {siege.Step + 1} after {LagHoldMs / 1000}s with a member more than {PlayerbotSiegeCommander.LagYards:F0} yd behind"));
        logger.LogWarning("Playerbot siege {Siege} raid {Raid}: pushing on from waypoint {Waypoint} after {Seconds}s with a member more than {Yards} yd behind",
            siege.Id, index, siege.Step + 1, LagHoldMs / 1000, PlayerbotSiegeCommander.LagYards);
        if (view.Raid.Announcer.Offer(PushOnLine, now)) RaidWarning(view.Group, view.Leader, PushOnLine);
    }

    /// <summary>The raid warning of a pushing raid whose lockstep goes on without a straggler.</summary>
    internal const string PushOnLine = "Pushing on without the stragglers";

    /// <summary>A member who is dead now and was not at the last read is a loss (<see cref="PlayerbotSiegeRaid.LastLossMs"/>); the first read counts none.</summary>
    private void NoteLosses(PlayerbotSiegeRaid raid, uint now)
    {
        if (raid.LossWatchStarted)
            foreach (ObjectGuid dead in _siegeDeadScratch)
                if (!raid.DeadLastRead.Contains(dead)) raid.LastLossMs = Math.Max(1u, now);
        raid.LossWatchStarted = true;
        raid.DeadLastRead.Clear();
        foreach (ObjectGuid dead in _siegeDeadScratch) raid.DeadLastRead.Add(dead);
    }

    /// <summary>
    /// Read the raid's combat state: who is dead (into <c>_siegeDeadScratch</c>) and whether a living member is in combat. A raid entering combat
    /// after <see cref="PlayerbotSiegeCommander.RetreatQuietMs"/> out of it begins an engagement: the dead are snapshotted (their deaths do not
    /// count towards "a third lost") and the raid may retreat once more. Runs for a led raid (<see cref="ReadRaid"/>) and an unled one alike, so a raid without a leader does not keep a stale engagement.
    /// </summary>
    private bool TrackEngagement(BotGroup group, PlayerbotSiegeRaid raid, WorldRuntime world, uint now)
    {
        bool inCombat = false;
        _siegeDeadScratch.Clear();
        foreach (Member member in group.Members)
        {
            if (member.Real || !member.Joined) continue;
            if (world.FindOnlinePlayer(member.Guid) is not { IsAlive: true } player) _siegeDeadScratch.Add(member.Guid);
            else inCombat |= player.Combat.IsInCombat;
        }

        // A flicker of the combat flag (a member dies, the creatures re-target) is not a new engagement: the raid must have been out of combat for
        // RetreatQuietMs, the time a retreat needs to end by itself.
        if (inCombat && !raid.WasInCombat && (raid.CombatSeenMs == 0 || unchecked(now - raid.CombatSeenMs) >= PlayerbotSiegeCommander.RetreatQuietMs))
        {
            raid.DeadAtEngagement.Clear();
            foreach (ObjectGuid dead in _siegeDeadScratch) raid.DeadAtEngagement.Add(dead);
            raid.RetreatedThisEngagement = false;
            raid.RetreatSinceMs = 0;
        }

        NoteLosses(raid, now);
        raid.WasInCombat = inCombat;
        if (inCombat)
        {
            raid.CombatSeenMs = Math.Max(1u, now);
            group.LastCombatMs = Math.Max(1u, now);
        }

        return inCombat;
    }

    /// <summary>Where a retreat goes: the waypoint behind the step, or where the siege began when the step is the first.</summary>
    private static Vector3 RetreatPointOf(PlayerbotSiege siege) => siege.Step > 0 ? siege.Waypoints[siege.Step - 1] : siege.Rally;

    /// <summary>
    /// How far <paramref name="point"/> is from the route <paramref name="from"/> to <paramref name="to"/>: the plane distance to the segment, but only on the levels of the route (no more than
    /// <see cref="PlayerbotSiegeCommander.WaypointLevelYards"/> above or below the segment's heights: the Undercity floors are about 20 yards apart), else positive infinity.
    /// </summary>
    internal static float RouteYards(Vector3 point, Vector3 from, Vector3 to)
    {
        if (point.Z > MathF.Max(from.Z, to.Z) + PlayerbotSiegeCommander.WaypointLevelYards || point.Z < MathF.Min(from.Z, to.Z) - PlayerbotSiegeCommander.WaypointLevelYards)
            return float.PositiveInfinity;
        float dx = to.X - from.X, dy = to.Y - from.Y;
        float length2 = (dx * dx) + (dy * dy);
        float t = length2 < 0.0001f ? 0f : Math.Clamp((((point.X - from.X) * dx) + ((point.Y - from.Y) * dy)) / length2, 0f, 1f);
        float ex = point.X - (from.X + (t * dx)), ey = point.Y - (from.Y + (t * dy));
        return MathF.Sqrt((ex * ex) + (ey * ey));
    }

    /// <summary>The unit fights the raid: its melee victim is, or its threat list holds, one of <paramref name="members"/> (the group's guids, built once per read).</summary>
    private static bool FightsRaid(Unit unit, HashSet<ObjectGuid> members)
    {
        if (unit.Combat.Victim is { } victim && members.Contains(victim.Guid)) return true;
        if (!unit.Combat.HasThreatList) return false;
        foreach (ThreatEntry entry in unit.Combat.Threat.Entries)
            if (members.Contains(entry.Target.Guid)) return true;
        return false;
    }

    private static HashSet<ObjectGuid> MemberGuids(BotGroup group)
    {
        var guids = new HashSet<ObjectGuid>(group.Members.Count);
        foreach (Member member in group.Members) guids.Add(member.Guid);
        return guids;
    }

    /// <summary>
    /// The living things the leader would fight within the pull range: hostile creatures, and players the siege may engage. The route tether
    /// (<see cref="PlayerbotSiegeCommander.RouteTetherYards"/>): only what stands near the route <paramref name="routeFrom"/> to <paramref name="goal"/> counts, besides the objective and whatever fights the raid.
    /// Civilians and units more than <see cref="PlayerbotSiegeCommander.WaypointLevelYards"/> above or below the leader count only when they fight the raid; what the leader's group AI gave up as unreachable (<see cref="BotGroup.Shun"/>) does not count.
    /// </summary>
    private void ScanHostiles(BotGroup group, SiegeView view, uint now, Vector3 routeFrom, Vector3 goal)
    {
        Player leader = view.Leader;
        if (leader.Map is not { } map) return;
        GroupManager? groups = GroupManagerOrNull();
        HashSet<ObjectGuid>? members = null;
        foreach (ObjectGuid guid in leader.VisibleObjects)
        {
            Unit? unit = map.FindObject(guid) as Unit;
            float yards;
            bool objective = false;
            switch (unit)
            {
                case Creature creature:
                    // NpcFlags (gossip, quest giver, vendor) do not exempt a creature the raid is hostile to: in the live world every Undercity target
                    // carries them (Sylvanas 268435459, Varimathras 3, the guardians 1, the Royal Dreadguard and the Seekers 2). A friendly or
                    // neutral NPC is out by the hostility test below; the objective entry is a valid target whatever its type.
                    objective = creature.Entry == view.Raid.Siege.ObjectiveEntry;
                    // The objective is kept whatever the shun table says (a failed plan to it must not hide it).
                    if (!creature.IsInWorld || !creature.IsAlive || creature.IsInEvadeMode || !objective && creature.Template.CreatureType == 8
                        || !float.IsFinite(creature.X) || !float.IsFinite(creature.Y) || !float.IsFinite(creature.Z)
                        || !map.Combat.Hooks.CanAttack(leader, creature) || !map.Combat.Hooks.IsHostileTo(creature, leader)
                        || !objective && group.IsShunned(creature.Guid, now)) continue;
                    break;
                case Player other:
                    if (!PlayerbotHostility.MayEngage(leader, other, PlayerbotEngageReason.Siege, groups) || group.IsShunned(other.Guid, now)) continue;
                    break;
                default:
                    continue;
            }

            yards = Distance(leader, unit);
            if (yards > PlayerbotSiegeCommander.PullOffYards) continue;
            if (!objective)
            {
                // Left alone unless they fight the raid: civilians (a city is full of them), units on another level of the leader (3D distance picks up the floor below), and what stands off the route.
                bool civilian = unit is Creature npc && PlayerbotTargetFilter.IsCivilian(npc);
                bool otherLevel = PlayerbotTargetFilter.OnOtherLevel(new Vector3(unit.X, unit.Y, unit.Z), new Vector3(leader.X, leader.Y, leader.Z));
                if ((civilian || otherLevel || RouteYards(new Vector3(unit.X, unit.Y, unit.Z), routeFrom, goal) > PlayerbotSiegeCommander.RouteTetherYards)
                    && !FightsRaid(unit, members ??= MemberGuids(group))) continue;
            }

            view.Hostiles.Add((unit, yards));
        }

        view.Hostiles.Sort(static (a, b) =>
        {
            int fighting = (a.Unit.Combat.IsInCombat ? 0 : 1).CompareTo(b.Unit.Combat.IsInCombat ? 0 : 1);
            return fighting != 0 ? fighting : a.Yards.CompareTo(b.Yards);
        });
    }

    /// <summary>The hostile the raid icon <paramref name="icon"/> marks, or null (nothing marked, or what it marks is not a hostile in range).</summary>
    private static Unit? IconUnit(SiegeView view, byte icon)
    {
        ObjectGuid marked = view.Server.TargetIcons[icon];
        if (marked.IsEmpty) return null;
        foreach ((Unit unit, float _) in view.Hostiles)
            if (unit.Guid == marked) return unit;
        return null;
    }

    /// <summary>
    /// The objective's death ends the siege: the entry is seen alive at some tick, and later none is. Once won, each leader's closing raid
    /// warning goes out (they keep the 3 s gap) and the siege ends. True when it is over or ending (nothing is commanded this tick).
    /// </summary>
    private bool WatchObjective(PlayerbotSiege siege, Player? witness, uint now)
    {
        if (!siege.Won && witness is not null && witness.MapId == siege.MapId)
        {
            if (FindLivingOnMap(witness, siege.ObjectiveEntry) is { } living)
            {
                siege.ObjectiveSeen = true;
                siege.ObjectivePosition = new Vector3(living.X, living.Y, living.Z);
                siege.ObjectiveUnseen = false;
            }
            else if (siege.ObjectiveSeen)
            {
                // Absent says nothing while the grid it stood in is unloaded (a creature there is not in the creature system): the objective is
                // dead only when the grid at its last known position is loaded and none lives. Else the siege goes on, the objective unseen.
                bool unseen = !GridLoadedAt(witness, siege.ObjectivePosition);
                if (unseen != siege.ObjectiveUnseen) Note($"siege {siege.Id}: objective {siege.ObjectiveEntry} {(unseen ? "is unseen (its grid is not loaded)" : "is in a loaded grid again")}");
                siege.ObjectiveUnseen = unseen;
                if (!unseen)
                {
                    siege.Won = true;
                    siege.WonMs = Math.Max(1u, now);
                    Note($"siege {siege.Id}: objective {siege.ObjectiveEntry} is dead");
                }
            }
        }

        if (!siege.Won) return false;
        bool allSaid = true;
        foreach (SiegeView view in _siegeViews)
        {
            view.Raid.Order = PlayerbotSiegeOrder.Hold;
            view.Raid.Push = PlayerbotSiegePush.None;
            view.Raid.PushInForce = PlayerbotSiegePush.None;
            view.Raid.PullTarget = default;
            List<string> sent = view.Raid.Announcer.Sent;
            if (sent.Count > 0 && sent[^1] == PlayerbotSiegeCommander.CompleteLine) continue;
            if (view.Raid.Announcer.Offer(PlayerbotSiegeCommander.CompleteLine, now)) RaidWarning(view.Group, view.Leader, PlayerbotSiegeCommander.CompleteLine);
            else allSaid = false;
        }

        if (allSaid || unchecked(now - siege.WonMs) >= SiegeWinAnnounceMs) EndSiege(siege, "won");
        return true;
    }

    /// <summary>Decide one raid's order and hand it to the leader: icons, raid warning, destination, target.</summary>
    private void Command(PlayerbotSiege siege, SiegeView view, uint now)
    {
        PlayerbotSiegeRaid raid = view.Raid;
        PlayerbotSiegeFacts facts = view.Facts with
        {
            PeersAtWaypoint = _siegeUnled == 0 && _siegeViews.All(v => ReferenceEquals(v, view) || v.Facts.AtWaypoint),
        };
        PlayerbotSiegeDecision decision = PlayerbotSiegeCommander.Decide(facts);
        raid.PushInForce = NextPushInForce(raid.PushInForce, decision.Push, facts, raid.PushDeniedStep == siege.Step);
        // A retreat is counted where its latch begins, not on an order change: a raid that lost its leader holds meanwhile and resumes uncounted.
        if (decision.RetreatLatched && !raid.RetreatLatched)
        {
            raid.Retreats++;
            raid.RetreatedThisEngagement = true;
        }

        raid.Order = decision.Order;
        bool pushBegins = decision.Push != PlayerbotSiegePush.None && !raid.PushingThrough;
        raid.Push = decision.Push;
        if (pushBegins)
        {
            ClearMarks(view);
            raid.PushBestYards = -1f;
            raid.PushProgressMs = Math.Max(1u, now);
        }
        // A retreat latch that ends restarts the step deadline: while it held no progress was possible.
        if (raid.RetreatLatched && !decision.RetreatLatched) siege.StepSinceMs = Math.Max(1u, now);
        raid.RetreatLatched = decision.RetreatLatched;
        raid.PullLatched = decision.PullLatched;
        if (!decision.RetreatLatched)
        {
            raid.RallySinceMs = 0;
            raid.RetreatSinceMs = 0;
            raid.RetreatNoPath = false;
            raid.RallyClockRestarted = false;
        }
        else
        {
            if (raid.RetreatSinceMs == 0) raid.RetreatSinceMs = Math.Max(1u, now);
            if (facts.AtRally && raid.RallySinceMs == 0) raid.RallySinceMs = Math.Max(1u, now);
        }

        Unit? skull = null;
        Unit? cross = IconUnit(view, PlayerbotSiegeCommander.CrossIcon);
        if (decision.Order is PlayerbotSiegeOrder.Pull or PlayerbotSiegeOrder.AssignTank or PlayerbotSiegeOrder.Assault && view.Hostiles.Count > 0)
        {
            // The skull stays on its unit while that unit is a hostile in range, else the first of the sorted hostiles.
            skull = IconUnit(view, Party.PlayerbotParty.SkullIcon);
            if (skull is null || ReferenceEquals(skull, cross)) skull = view.Hostiles.Select(h => h.Unit).FirstOrDefault(u => !ReferenceEquals(u, cross));
            if (skull is null) skull = view.Hostiles[0].Unit;
            if (decision.Order == PlayerbotSiegeOrder.AssignTank && cross is null)
                cross = view.Hostiles.Select(h => h.Unit).FirstOrDefault(u => !ReferenceEquals(u, skull));
            Mark(view, Party.PlayerbotParty.SkullIcon, skull);
            if (cross is not null) Mark(view, PlayerbotSiegeCommander.CrossIcon, cross);
        }

        raid.PullTarget = skull?.Guid ?? default;

        // The tanks go for the cross while there is one, everybody else for the skull.
        foreach (Member member in view.Group.Members)
            member.FocusIcon = member.Role == PlayerbotGroupRole.Tank && cross is not null ? PlayerbotSiegeCommander.CrossIcon : Party.PlayerbotParty.SkullIcon;

        // Never stale: every order but the retreat's heads for the step's goal.
        raid.MoveTo = decision.Order is PlayerbotSiegeOrder.Retreat or PlayerbotSiegeOrder.Rally
            ? RetreatPointOf(siege)
            : siege.Step < siege.Waypoints.Count ? siege.Waypoints[siege.Step] : ObjectivePointOf(siege, view.Leader);

        string line = decision.Order switch
        {
            PlayerbotSiegeOrder.Hold => string.Create(CultureInfo.InvariantCulture, $"Holding at waypoint {siege.Step + 1} for the other raid"),
            PlayerbotSiegeOrder.Regroup => "Regrouping",
            PlayerbotSiegeOrder.Advance when decision.Push != PlayerbotSiegePush.None => string.Create(CultureInfo.InvariantCulture,
                $"Pushing through to waypoint {siege.Step + 1}"),
            PlayerbotSiegeOrder.Advance => siege.Step < siege.Waypoints.Count
                ? string.Create(CultureInfo.InvariantCulture, $"Advancing to waypoint {siege.Step + 1}") : "Advancing on the objective",
            PlayerbotSiegeOrder.Pull => "Pulling " + NameOf(skull),
            PlayerbotSiegeOrder.AssignTank => "Tanks on the cross, everyone else on the skull",
            PlayerbotSiegeOrder.Assault => "Assault the skull",
            PlayerbotSiegeOrder.Rally => siege.Step > 0 ? string.Create(CultureInfo.InvariantCulture, $"Rallying at waypoint {siege.Step}") : "Rallying at the start",
            _ => "Retreating",
        };
        raid.Detail = string.Create(CultureInfo.InvariantCulture,
            $"{decision.Order.ToString().ToLowerInvariant()} step={siege.Step}/{siege.Waypoints.Count} hostiles={facts.Hostiles} alive={facts.Alive}/{facts.Total} hp={facts.AverageHealthPct:F0}%{(decision.Push != PlayerbotSiegePush.None ? $" push={decision.Push.ToString().ToLowerInvariant()}" : string.Empty)}{(raid.PushDeniedStep == siege.Step ? " push=denied" : string.Empty)}{(facts.LagWaived ? " lag=waived" : string.Empty)} furthest={raid.FurthestWaypoint}{(facts.OffRoute ? " offroute" : string.Empty)} deadline={(siege.Step < siege.Waypoints.Count ? (StepDeadlineMs - Math.Min(StepDeadlineMs, unchecked(now - siege.StepSinceMs))) / 1000 : 0)}");
        raid.WatchLine = string.Create(CultureInfo.InvariantCulture,
            $"{decision.Order.ToString().ToLowerInvariant()} step {siege.Step}/{siege.Waypoints.Count}, alive {facts.Alive}/{facts.Total}");
        // A raid that has used up its retreats says so once, in place of the plan line of that decision.
        if (decision.Committed && !raid.CommitAnnounced)
        {
            if (raid.Announcer.Offer(PlayerbotSiegeCommander.CommitLine, now))
            {
                raid.CommitAnnounced = true;
                RaidWarning(view.Group, view.Leader, PlayerbotSiegeCommander.CommitLine);
            }
        }
        else if (raid.Announcer.Offer(line, now)) RaidWarning(view.Group, view.Leader, line);
    }

    private static string NameOf(Unit? unit) => unit switch
    {
        Creature creature => creature.Template.Name,
        Player player => player.Name,
        _ => "the target",
    };

    /// <summary>Where the leader walks once past the last waypoint: the nearest living objective, else the last waypoint.</summary>
    private static Vector3 ObjectivePointOf(PlayerbotSiege siege, Player leader)
        => FindLivingOnMap(leader, siege.ObjectiveEntry) is { } objective ? new Vector3(objective.X, objective.Y, objective.Z) : siege.Waypoints[^1];

    /// <summary>A push begins: the leader takes the skull and the cross off their units (MSG_RAID_TARGET_UPDATE with no guid), so no member keeps a mark to fight or comes back to one later.</summary>
    private void ClearMarks(SiegeView view)
    {
        foreach (byte icon in new[] { Party.PlayerbotParty.SkullIcon, PlayerbotSiegeCommander.CrossIcon })
        {
            if (view.Server.TargetIcons[icon].IsEmpty) continue;
            var writer = new PacketWriter(9);
            writer.WriteByte(icon);
            writer.WriteUInt64(0);
            LeaderAct(view.Group, view.Leader, WorldOpcode.MsgRaidTargetUpdate, writer.ToArray());
        }
    }

    /// <summary>The leader marks <paramref name="target"/> with <paramref name="icon"/> (MSG_RAID_TARGET_UPDATE: icon, guid) unless it already does.</summary>
    private void Mark(SiegeView view, byte icon, Unit target)
    {
        if (view.Server.TargetIcons[icon] == target.Guid) return;
        var writer = new PacketWriter(9);
        writer.WriteByte(icon);
        writer.WriteUInt64(target.Guid.Value);
        LeaderAct(view.Group, view.Leader, WorldOpcode.MsgRaidTargetUpdate, writer.ToArray());
    }

    /// <summary>The leader's raid warning (CMSG_MESSAGECHAT type raid warning; the server lets only a leader or assistant of a raid send it).</summary>
    private void RaidWarning(BotGroup group, Player leader, string text)
    {
        if (_shuttingDown) return;
        Language language = leader.Team == Team.Horde ? Language.Orcish : Language.Common;
        LeaderAct(group, leader, WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(ChatType.RaidWarning, language, text));
    }
}
