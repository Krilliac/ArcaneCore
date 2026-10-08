using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>The life of a bot-led group (<see cref="PlayerbotGroupCoordinator.BotGroup"/>).</summary>
internal enum PlayerbotGroupState : byte
{
    /// <summary>The leader's invitations are out; the group fills (and becomes a raid when the goal needs one).</summary>
    Forming,

    /// <summary>The members walk to the leader.</summary>
    Gathering,

    /// <summary>Together to the meeting point: the objective's spawn, or the instance entrance.</summary>
    Travelling,

    /// <summary>Through the entrance trigger, the leader first, into the group's instance.</summary>
    Entering,

    /// <summary>Clearing towards the objective and killing it.</summary>
    Engaging,

    /// <summary>Half the group or more fell in a fight: everyone alive retreats.</summary>
    Wiped,

    /// <summary>After a wipe: the dead are resurrected or run back, everyone rests, then the group gathers again.</summary>
    Regrouping,

    /// <summary>Done inside an instance: out through its exit.</summary>
    Leaving,

    /// <summary>Every member leaves the group (CMSG_GROUP_DISBAND); the brains take over again.</summary>
    Disbanding,
}

/// <summary>
/// Groups managed bots for content one bot cannot do (<c>World:Playerbots:Groups</c>, docs/areas/playbots-groups.md). World feature;
/// <see cref="ManagedPlayerbotFeature"/> calls it on the world thread every tick with its running bots.
/// <para>
/// <b>Needs.</b> Every <see cref="ScanMs"/> each free autonomous bot (not scripted, not in a real player's group, not grouped) is
/// looked over for a group goal: an unfinished creature objective of a quest flagged for a group (quest_template Type 1 elite, 81
/// dungeon, 62 raid, or SuggestedPlayers above one; <see cref="PlayerbotGroupContent.QuestGroupSize"/>), an objective that only spawns
/// inside an instance, or a quest objective the risk estimate passed over as too strong alone but feasible for a few
/// (<see cref="PlayerbotRisk.GroupSignal"/>). The objective is then held for the group: the brain goes on with what it can do alone.
/// </para>
/// <para>
/// <b>Formation.</b> Bots that want the same thing (<see cref="PlayerbotGroupGoal.Key"/>), of one team, on the meeting point's map within
/// <see cref="MaxMeetingYards"/> of it and high enough for its entrance are matched (<see cref="PlayerbotGroupContent.Match"/>: levels
/// within <see cref="PlayerbotGroupOptions.LevelRange"/>, the tanks and healers a group of three or more needs). The leader (the best
/// tank, else the bot that waited longest) invites the others through CMSG_GROUP_INVITE, each accepts through CMSG_GROUP_ACCEPT (the
/// party intake: <see cref="Party.PlayerbotPartyAI.AcceptsBotGroup"/>), so every observer sees an ordinary group form. A goal for more than
/// five is a raid: the leader converts after the first acceptance (CMSG_GROUP_RAID_CONVERT, vmangos Group::ConvertToRaid needs two
/// members) and spreads the tanks and healers over the subgroups (CMSG_GROUP_CHANGE_SUB_GROUP). The loot is round robin
/// (CMSG_LOOT_METHOD); a roll above the threshold is needed when the item is an upgrade the bot can wear, greeded otherwise.
/// A bot without partners after <see cref="PlayerbotGroupOptions.FormationTimeoutSeconds"/> sets the goal aside for
/// <see cref="SetAsideMs"/> and goes on alone.
/// </para>
/// <para>
/// <b>Doing it</b> (<see cref="PlayerbotGroupAI"/>, one per member): gather, travel, enter, engage; the goal is done when every member
/// that had the quest has the objective (or, without a quest, when the objective died); inside an instance the group then walks out
/// through the exit, and disbands. A wipe (half the group or more dead in a fight) retreats, regroups and tries again; after
/// <see cref="MaxFailures"/> failures (or <see cref="GoalTimeoutMs"/>) the goal is set aside for every member and the group disbands.
/// </para>
/// </summary>
public sealed class PlayerbotGroupCoordinator(IServiceProvider services, ILogger<PlayerbotGroupCoordinator> logger) : IWorldFeature
{
    /// <summary>How often free bots are looked over for group goals and matched.</summary>
    internal const uint ScanMs = 2_000;

    /// <summary>How long a goal that found no partners, or failed, is set aside.</summary>
    internal const uint SetAsideMs = PlayerbotSuspensions.SuspendMs;

    /// <summary>Failed attempts (wipes) before a group gives its goal up.</summary>
    internal const int MaxFailures = 2;

    /// <summary>The longest a group works on one goal.</summary>
    internal const uint GoalTimeoutMs = 1_800_000;

    /// <summary>The longest the members take to gather, travel, enter or leave (each).</summary>
    internal const uint StepTimeoutMs = 300_000;

    /// <summary>Invitations not answered within this are given up (the group is abandoned; the bots may be matched again).</summary>
    internal const uint InviteTimeoutMs = 30_000;

    /// <summary>A wipe lasts at least this long before the group regroups (the survivors' retreats start meanwhile).</summary>
    internal const uint WipeSettleMs = 3_000;

    /// <summary>A dead member waits this long for a resurrection while a living member could give one.</summary>
    internal const long DeadWaitMs = 60_000;

    /// <summary>A bot farther than this from a goal's meeting point is not matched for it.</summary>
    internal const float MaxMeetingYards = 1500f;

    /// <summary>The members count as gathered within this distance of the leader.</summary>
    internal const float GatherYards = 20f;

    /// <summary>A real player is invited from at most this far (<see cref="PlayerbotGroupOptions.InvitePlayers"/>).</summary>
    internal const float InvitePlayerYards = 100f;

    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private readonly Dictionary<Guid, Waiting> _waiting = [];
    private readonly List<BotGroup> _groups = [];
    private readonly Dictionary<Guid, BotGroup> _memberOf = [];
    private readonly Dictionary<(Guid Bot, (PlayerbotGroupGoalKind, uint) Key), uint> _setAside = [];
    private readonly Dictionary<ObjectGuid, uint> _declinedPlayers = [];
    private readonly Dictionary<Guid, PlayerbotGroupBot> _bots = [];
    private WorldRuntime? _world;
    private uint _scanElapsed = ScanMs;
    private int _nextId = 1;

    /// <summary>The groups formed so far, completed so far and given up so far (status and tests).</summary>
    internal (int Formed, int Completed, int Failed) Totals { get; private set; }

    /// <summary>What happened last to bot groups, newest last (at most 32 lines; status and tests).</summary>
    internal List<string> Events { get; } = [];

    public void Attach(WorldRuntime world) => _world = world;

    internal PlayerbotGroupOptions Options => _options.Groups;

    /// <summary>The groups running now (world thread; tests and the GM commands).</summary>
    internal IReadOnlyList<BotGroup> Groups => _groups;

    /// <summary>The bots waiting for partners, with their goal and since when (world thread).</summary>
    internal IEnumerable<(Guid BotId, string Name, PlayerbotGroupGoal Goal, uint SinceMs)> WaitingBots
        => _waiting.Select(w => (w.Key, w.Value.Name, w.Value.Goal, w.Value.SinceMs));

    /// <summary>Whether <paramref name="botId"/> set aside the goal with <paramref name="key"/> (tests and status).</summary>
    internal bool IsSetAside(Guid botId, (PlayerbotGroupGoalKind, uint) key)
        => _setAside.TryGetValue((botId, key), out uint until) && unchecked((int)(until - Now)) > 0;

    private uint Now => _world?.NowMs ?? 0;

    // --- the feature's calls ------------------------------------------------------------------------------------------

    /// <summary>A running bot as the coordinator sees it (<see cref="Update"/>).</summary>
    internal readonly record struct PlayerbotGroupBot(Guid BotId, WorldSession Session, PlayerbotBrain Brain, bool Free);

    /// <summary>
    /// One world tick (world thread): the running bots (<paramref name="bots"/>; <see cref="PlayerbotGroupBot.Free"/> when autonomous and
    /// not in a real player's group), the groups' states, and every <see cref="ScanMs"/> the needs and the matching.
    /// </summary>
    internal void Update(IReadOnlyList<PlayerbotGroupBot> bots, uint elapsedMs)
    {
        _bots.Clear();
        foreach (PlayerbotGroupBot bot in bots) _bots[bot.BotId] = bot;
        foreach (BotGroup group in _groups.ToArray()) Advance(group);
        _scanElapsed = unchecked(_scanElapsed + elapsedMs);
        if (_scanElapsed < ScanMs) return;
        _scanElapsed = 0;
        Scan();
    }

    /// <summary>Whether the group AI drives <paramref name="botId"/> now (it is a member of a bot-led group).</summary>
    internal bool Drives(Guid botId) => _memberOf.ContainsKey(botId);

    /// <summary>One tick of a member's group AI (world thread, within the shared action budget).</summary>
    internal void UpdateMember(Guid botId, Player player, uint elapsedMs)
    {
        if (!_memberOf.TryGetValue(botId, out BotGroup? group) || group.Find(botId) is not { AI: { } ai } member) return;
        ai.Update(player, group, member, elapsedMs);
    }

    /// <summary>The group AI of a member (inspection and tests).</summary>
    internal PlayerbotGroupAI? FindAI(Guid botId) => _memberOf.TryGetValue(botId, out BotGroup? group) ? group.Find(botId)?.AI : null;

    /// <summary>The group <paramref name="botId"/> belongs to (tests and status).</summary>
    internal BotGroup? GroupOf(Guid botId) => _memberOf.GetValueOrDefault(botId);

    /// <summary>The goal and activity a member reports: its group AI's.</summary>
    internal (PlayerbotGoalKind Goal, uint TargetEntry)? GoalOf(Guid botId)
        => FindAI(botId) is { } ai ? (ai.Goal, ai.TargetEntry) : null;

    /// <summary>
    /// The party intake asks whether <paramref name="bot"/> takes <paramref name="inviter"/>'s invitation whatever its invite policy: yes
    /// when both are in one forming group and the inviter leads it.
    /// </summary>
    internal bool AcceptsInvite(Player bot, Player inviter)
    {
        foreach (BotGroup group in _groups)
            if (group.State == PlayerbotGroupState.Forming && group.LeaderGuid == inviter.Guid && group.Members.Any(m => m.Guid == bot.Guid))
                return true;
        return false;
    }

    /// <summary>A bot of a bot-led group votes on a loot roll: need what it would wear as an upgrade, greed the rest. Null outside one.</summary>
    internal RollVote? LootVote(Player bot, uint itemId)
    {
        if (!_memberOf.ContainsKey(BotIdOf(bot.Guid))) return null;
        if (bot.Inventory.Templates.Find(itemId) is not { } template) return RollVote.Greed;
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(bot.Class, bot.Guid.Low).Weights;
        return PlayerbotItemScore.UpgradeGain(bot, template, weights) is > 0f ? RollVote.Need : RollVote.Greed;
    }

    /// <summary>A bot stopped, became scripted or left the world: it leaves its group (or its wait).</summary>
    internal void Forget(Guid botId)
    {
        _waiting.Remove(botId);
        if (_memberOf.TryGetValue(botId, out BotGroup? group)) DropMember(group, botId, "stopped");
    }

    /// <summary>The group part of a bot's status line: <c>group=3:tank:engaging</c>, <c>group=waiting:quest:990700</c>, or null.</summary>
    internal string? Describe(Guid botId)
    {
        if (_memberOf.TryGetValue(botId, out BotGroup? group) && group.Find(botId) is { } member)
            return string.Create(CultureInfo.InvariantCulture,
                $"group={group.Id}:{member.Role.ToString().ToLowerInvariant()}:{group.State.ToString().ToLowerInvariant()}{(group.LeaderBotId == botId ? ":leader" : string.Empty)}");
        if (_waiting.TryGetValue(botId, out Waiting? waiting))
            return string.Create(CultureInfo.InvariantCulture,
                $"group=waiting:{waiting.Goal.Kind.ToString().ToLowerInvariant()}:{waiting.Goal.Key.Value}:size{waiting.Goal.Size}");
        return null;
    }

    /// <summary>The <c>.playerbot groups</c> lines (world thread).</summary>
    internal IReadOnlyList<string> Report()
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"Bot groups: {(_options.Groups.Enabled ? "enabled" : "disabled")}, {_groups.Count} running (max {_options.Groups.MaxGroups}), {_waiting.Count} waiting; formed {Totals.Formed}, completed {Totals.Completed}, given up {Totals.Failed}."),
        };
        foreach (BotGroup group in _groups)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"group {group.Id} {group.Goal} state={group.State.ToString().ToLowerInvariant()}");
            text.Append(CultureInfo.InvariantCulture, $" {(group.Raid ? "raid" : "party")} failures={group.Failures} age={unchecked(Now - group.FormedMs) / 1000}s members=");
            text.AppendJoin(',', group.Members.Select(m =>
                $"{m.Name}({m.Role.ToString().ToLowerInvariant()}{(m.Guid == group.LeaderGuid ? ",leader" : string.Empty)}{(m.Real ? ",player" : string.Empty)}{(m.Joined ? string.Empty : ",invited")}{(group.Raid ? $",g{m.SubGroup + 1}" : string.Empty)})"));
            lines.Add(text.ToString());
        }

        foreach ((Guid _, Waiting waiting) in _waiting.OrderBy(w => w.Value.SinceMs))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"waiting {waiting.Name} {waiting.Goal} for {unchecked(Now - waiting.SinceMs) / 1000}s"));
        foreach (string line in Events.TakeLast(5)) lines.Add("last: " + line);
        return lines;
    }

    // --- needs and matching ---------------------------------------------------------------------------------------------

    private sealed class Waiting(string name, PlayerbotGroupGoal goal, uint since)
    {
        public string Name { get; } = name;
        public PlayerbotGroupGoal Goal { get; set; } = goal;
        public uint SinceMs { get; } = since;
    }

    private void Scan()
    {
        uint now = Now;
        foreach (((Guid, (PlayerbotGroupGoalKind, uint)) key, uint until) in _setAside.ToArray())
            if (unchecked((int)(until - now)) <= 0) _setAside.Remove(key);
        foreach ((ObjectGuid key, uint until) in _declinedPlayers.ToArray())
            if (unchecked((int)(until - now)) <= 0) _declinedPlayers.Remove(key);

        foreach (Guid waiting in _waiting.Keys.ToArray())
            if (!_bots.TryGetValue(waiting, out PlayerbotGroupBot bot) || !bot.Free || bot.Session.Player is not { } player || !player.IsInWorld)
                Unhold(waiting, null);

        foreach (PlayerbotGroupBot bot in _bots.Values.OrderBy(b => b.BotId))
        {
            if (!bot.Free || _memberOf.ContainsKey(bot.BotId) || bot.Session.Player is not { IsInWorld: true } player) continue;
            if (EvacuateIfStranded(bot, player)) continue;
            if (!_options.Groups.Enabled || !player.IsAlive || GroupManagerOrNull() is not { } groupManager) continue;
            if (groupManager.GetGroup(player.Guid) is { } stale && stale.Members.All(m => m.Guid == player.Guid || IsOnlineBot(m.Guid)))
            {
                // A group of bots nobody leads any more (a world restart restores the server groups but not the coordinator's): the
                // bot leaves it, as a client would. Only when every other member is a bot online: an offline member may be a real
                // player its party AI waits for (PlayerbotPartyAI, MasterTimeoutSeconds).
                Unhold(bot.BotId, null);
                Act(bot.Session, WorldOpcode.CmsgGroupDisband, [], budgeted: false);
                Note($"{player.Name} left a group of bots nobody leads");
                continue;
            }

            if (groupManager.GetGroup(player.Guid) is not null || groupManager.GetInvite(player.Guid) is not null)
            {
                Unhold(bot.BotId, null);
                continue;
            }

            if (_waiting.TryGetValue(bot.BotId, out Waiting? waiting))
            {
                // Still wanted? A finished objective, or one the bot no longer has, ends the wait.
                if (!StillNeeded(player, waiting.Goal))
                {
                    Unhold(bot.BotId, null);
                    continue;
                }

                if (unchecked(now - waiting.SinceMs) >= (uint)_options.Groups.FormationTimeoutSeconds * 1000u)
                {
                    SetAside(bot.BotId, waiting.Goal, suspendInBrain: false);
                    Note($"{player.Name} found no partners for {waiting.Goal} in {_options.Groups.FormationTimeoutSeconds}s: set aside");
                    Unhold(bot.BotId, waiting.Goal);
                }

                continue;
            }

            if (FindNeed(bot, player) is { } goal)
            {
                _waiting[bot.BotId] = new Waiting(player.Name, goal, now);
                bot.Brain.GroupHeld.Add((goal.QuestId, goal.ObjectiveEntry));
                bot.Brain.Risk.ClearGroupSignal();
                Note($"{player.Name} needs a group: {goal}");
            }
        }

        if (_options.Groups.Enabled) Match();
    }

    /// <summary>The objective of <paramref name="goal"/> is still unfinished for the bot (or, without a quest, still wanted).</summary>
    private bool StillNeeded(Player player, PlayerbotGroupGoal goal)
    {
        if (goal.QuestId == 0) return true;
        if (QuestState(player) is not { } state) return false;
        return state.Quests.Get(goal.QuestId) is { Status: QuestStatus.Incomplete } && !ObjectiveDone(player, goal.ObjectiveEntry);
    }

    private PlayerNpcState? QuestState(Player player)
        => services.GetService<QuestNpcFeature>()?.Services.StateOf(player) is { Loaded: true } state ? state : null;

    /// <summary>Whether every quest in <paramref name="player"/>'s log that asks for <paramref name="entry"/> has its count (true when none asks).</summary>
    internal bool ObjectiveDone(Player player, uint entry)
    {
        QuestNpcServices? quests = services.GetService<QuestNpcFeature>()?.Services;
        if (quests is null || QuestState(player) is not { } state || entry == 0) return true;
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses)
        {
            if (status.Status != QuestStatus.Incomplete || quests.Quests.Get(questId) is not { } quest) continue;
            for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
                if (quest.ReqCreatureOrGOId[index] == entry && status.CreatureOrGOCount[index] < quest.ReqCreatureOrGOCount[index]) return false;
        }

        return true;
    }

    /// <summary>The bot's group goal now (the first unfinished objective that needs one), or null.</summary>
    private PlayerbotGroupGoal? FindNeed(PlayerbotGroupBot bot, Player player)
    {
        QuestNpcServices? quests = services.GetService<QuestNpcFeature>()?.Services;
        CreatureContent? creatures = services.GetService<CreatureWorldFeature>()?.Content;
        if (quests is null || creatures is null || QuestState(player) is not { } state || player.Map is null) return null;
        WorldMaps maps = WorldMaps.Of(_world!);
        if (maps.Registry.Find(player.MapId) is { IsDungeon: true } || maps.Registry.Find(player.MapId) is { IsBattleground: true }) return null;

        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses.OrderBy(s => s.Key))
        {
            if (status.Status != QuestStatus.Incomplete || quests.Quests.Get(questId) is not { } quest) continue;
            if (bot.Brain.Suspensions.IsQuestSuspended(questId, Now)) continue;
            int flagged = PlayerbotGroupContent.QuestGroupSize(quest.Template);
            for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
            {
                int raw = quest.ReqCreatureOrGOId[index];
                if (raw <= 0 || status.CreatureOrGOCount[index] >= quest.ReqCreatureOrGOCount[index]) continue;
                uint entry = (uint)raw;
                if (GoalFor(player, maps, creatures, quest, entry, flagged) is { } goal && !IsSetAside(bot.BotId, goal.Key)) return goal;
            }
        }

        // The risk estimate passed over a quest objective as too strong alone but feasible for a few.
        if (bot.Brain.Risk.GroupSignal is { } signal && signal.MapId == player.MapId && unchecked(Now - signal.AtMs) < 60_000)
        {
            uint questId = QuestFor(player, signal.Entry);
            var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, questId, signal.Entry, player.MapId, player.MapId,
                signal.At, 0, signal.At, Math.Clamp(signal.Size, 2, PlayerbotGroupContent.PartySize), player.Level, "risk");
            if (!IsSetAside(bot.BotId, goal.Key)) return goal;
        }

        return null;
    }

    /// <summary>The quest in the bot's log that asks for <paramref name="entry"/> (0: none).</summary>
    private uint QuestFor(Player player, uint entry)
    {
        QuestNpcServices? quests = services.GetService<QuestNpcFeature>()?.Services;
        if (quests is null || QuestState(player) is not { } state) return 0;
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses.OrderBy(s => s.Key))
            if (status.Status == QuestStatus.Incomplete && quests.Quests.Get(questId) is { } quest && quest.ReqCreatureOrGOId.Contains((int)entry))
                return questId;
        return 0;
    }

    /// <summary>
    /// The group goal for one unfinished objective: inside an instance when the creature spawns only on dungeon maps (reached
    /// through an entrance trigger on the bot's continent), on the bot's map when the quest is flagged for a group. Null when one
    /// bot is enough or the place cannot be reached.
    /// </summary>
    private PlayerbotGroupGoal? GoalFor(Player player, WorldMaps maps, CreatureContent creatures, Quest quest, uint entry, int flagged)
    {
        IReadOnlyList<CreatureSpawn> here = creatures.GetSpawns(player.MapId, entry);
        if (here.Count > 0)
        {
            if (flagged < 2) return null;
            CreatureSpawn nearest = here.OrderBy(s => Vector2.DistanceSquared(new(s.X, s.Y), new(player.X, player.Y))).First();
            var at = new Vector3(nearest.X, nearest.Y, nearest.Z);
            bool raid = quest.Template.Type == PlayerbotGroupContent.QuestTypeRaid;
            if (raid && !_options.Groups.RaidsEnabled) return null;
            return new PlayerbotGroupGoal(raid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Quest, quest.Id, entry,
                player.MapId, player.MapId, at, 0, at, flagged, quest.MinLevel, "quest");
        }

        foreach (uint mapId in creatures.MapsWithSpawns.OrderBy(m => m))
        {
            if (maps.Registry.Find(mapId) is not { IsDungeon: true } instance) continue;
            IReadOnlyList<CreatureSpawn> inside = creatures.GetSpawns(mapId, entry);
            if (inside.Count == 0) continue;
            if (EntranceTo(maps, player.MapId, mapId, new Vector3(player.X, player.Y, player.Z)) is not { } entrance) continue;
            AreaTriggerTeleport teleport = maps.FindAreaTriggerTeleport(entrance.Id)!;
            int size = instance.IsRaid ? Math.Max(PlayerbotGroupContent.InstanceGroupSize(instance), flagged)
                : Math.Max(flagged >= 2 ? Math.Min(flagged, PlayerbotGroupContent.PartySize) : PlayerbotGroupContent.InstanceGroupSize(instance), 2);
            if (instance.IsRaid && !_options.Groups.RaidsEnabled) return null;
            CreatureSpawn spawn = inside.OrderBy(s => Vector2.DistanceSquared(new(s.X, s.Y), new(teleport.TargetX, teleport.TargetY))).First();
            return new PlayerbotGroupGoal(instance.IsRaid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Dungeon, quest.Id, entry,
                mapId, player.MapId, new Vector3(entrance.X, entrance.Y, entrance.Z), entrance.Id, new Vector3(spawn.X, spawn.Y, spawn.Z),
                size, Math.Max(teleport.RequiredLevel, quest.MinLevel), "instance");
        }

        return null;
    }

    /// <summary>The nearest area trigger on <paramref name="fromMap"/> whose teleport leads into <paramref name="instanceMap"/>.</summary>
    internal static AreaTriggerTemplate? EntranceTo(WorldMaps maps, uint fromMap, uint instanceMap, Vector3 near)
        => PlayerbotAreaTriggers.OnMap(maps, fromMap)
            .Where(t => maps.FindAreaTriggerTeleport(t.Id)?.TargetMap == instanceMap)
            .OrderBy(t => Vector3.DistanceSquared(new Vector3(t.X, t.Y, t.Z), near)).FirstOrDefault();

    /// <summary>The way out of an instance: a trigger on <paramref name="mapId"/> whose teleport leads off it, or null.</summary>
    internal static AreaTriggerTemplate? ExitOf(WorldRuntime world, uint mapId)
    {
        WorldMaps maps = WorldMaps.Of(world);
        return PlayerbotAreaTriggers.OnMap(maps, mapId)
            .FirstOrDefault(t => maps.FindAreaTriggerTeleport(t.Id) is { } teleport && teleport.TargetMap != mapId);
    }

    private void Match()
    {
        if (_groups.Count >= _options.Groups.MaxGroups || GroupManagerOrNull() is not { } groupManager) return;
        foreach (var wanting in _waiting
                     .Select(w => (BotId: w.Key, Waiting: w.Value))
                     .GroupBy(w => (w.Waiting.Goal.Key, Team: _bots[w.BotId].Session.Player?.Team ?? 0))
                     .OrderBy(g => g.Min(w => w.Waiting.SinceMs)).ToArray())
        {
            if (_groups.Count >= _options.Groups.MaxGroups) return;
            PlayerbotGroupGoal goal = wanting.OrderBy(w => w.Waiting.SinceMs).First().Waiting.Goal;
            int size = wanting.Max(w => w.Waiting.Goal.Size);
            var candidates = new List<PlayerbotGroupCandidate>();
            foreach ((Guid botId, Waiting waiting) in wanting)
            {
                if (_bots[botId].Session.Player is not { IsAlive: true } player || player.MapId != goal.MeetingMap) continue;
                if (player.Level < goal.MinLevel) continue;
                if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), goal.Meeting) > MaxMeetingYards) continue;
                candidates.Add(Candidate(botId, player, waiting.SinceMs));
            }

            PlayerbotGroupPlan? plan = PlayerbotGroupContent.Match(candidates, size, _options.Groups);
            if (plan is null && _options.Groups.InvitePlayers && candidates.Count >= 1)
                plan = MatchWithPlayer(candidates, size, goal, groupManager);
            if (plan is null) continue;
            Form(goal with { Size = size }, plan);
        }
    }

    private PlayerbotGroupCandidate Candidate(Guid botId, Player player, uint since)
    {
        PlayerbotRole talent = _bots.TryGetValue(botId, out PlayerbotGroupBot bot) ? TalentRole(bot.Session, player) : PlayerbotRole.MeleeDps;
        return new PlayerbotGroupCandidate(botId, player.Guid.Value, player.Name, player.Class, player.Level, (uint)player.Team, player.MapId,
            new Vector3(player.X, player.Y, player.Z), talent, Gear(player), since);
    }

    /// <summary>The talent role (vmangos AutoAssignRole) from the spell names the bot knows; reads the book, drains no packet.</summary>
    private static PlayerbotRole TalentRole(WorldSession session, Player player)
    {
        if (session.Services.GetService<Spells.SpellFeature>() is not { } feature) return PlayerbotRoles.Assign(player.Class, _ => false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (uint id in feature.Spellbook.GetSpells(player))
            if (feature.System.Store.Get(id) is { } spell) names.Add(spell.Name);
        return PlayerbotRoles.Assign(player.Class, id => feature.System.Store.Get(id) is { } signature && names.Contains(signature.Name));
    }

    /// <summary>The bot's gear: the item levels of what it wears.</summary>
    private static float Gear(Player player)
    {
        float total = 0;
        for (byte slot = 0; slot < Game.Items.InventorySlots.EquipmentEnd; slot++)
            if (player.Inventory.GetItem(Game.Items.InventorySlots.Bag0, slot) is { } item) total += item.Template.ItemLevel;
        return total;
    }

    /// <summary>
    /// <see cref="PlayerbotGroupOptions.InvitePlayers"/>: a group one short (a role or a member) may take a nearby real player of the
    /// same team, ungrouped, of a fitting level, who has not declined lately.
    /// </summary>
    private PlayerbotGroupPlan? MatchWithPlayer(List<PlayerbotGroupCandidate> candidates, int size, PlayerbotGroupGoal goal, GroupManager groupManager)
    {
        PlayerbotGroupCandidate oldest = candidates.OrderBy(c => c.NeedSinceMs).First();
        if (_bots[oldest.BotId].Session.Player is not { Map: { } map } anchor) return null;
        foreach (ObjectGuid guid in anchor.VisibleObjects)
        {
            if (map.FindObject(guid) is not Player { IsAlive: true } real || real.Session is not WorldSession { IsManaged: false }) continue;
            if (real.Team != anchor.Team || groupManager.GetGroup(real.Guid) is not null || groupManager.GetInvite(real.Guid) is not null) continue;
            if (_declinedPlayers.ContainsKey(real.Guid) || real.Level < goal.MinLevel) continue;
            if (Vector3.Distance(new Vector3(real.X, real.Y, real.Z), new Vector3(anchor.X, anchor.Y, anchor.Z)) > InvitePlayerYards) continue;
            var withPlayer = new List<PlayerbotGroupCandidate>(candidates)
            {
                new(Guid.Empty, real.Guid.Value, real.Name, real.Class, real.Level, (uint)real.Team, real.MapId,
                    new Vector3(real.X, real.Y, real.Z), PlayerbotRoles.Assign(real.Class, _ => false), Gear(real), uint.MaxValue),
            };
            // A real player never leads a bot group: the bots keep their goal.
            if (PlayerbotGroupContent.Match(withPlayer, size, _options.Groups) is { } plan && plan.Leader.BotId != Guid.Empty
                && plan.Members.Any(m => m.Bot.BotId == Guid.Empty))
                return plan;
        }

        return null;
    }

    private void Form(PlayerbotGroupGoal goal, PlayerbotGroupPlan plan)
    {
        var group = new BotGroup(_nextId++, goal, plan.Leader.BotId, new ObjectGuid(plan.Leader.Guid), Now);
        foreach ((PlayerbotGroupCandidate bot, PlayerbotGroupRole role, byte subGroup) in plan.Members)
        {
            bool real = bot.BotId == Guid.Empty;
            var member = new Member(bot.BotId, new ObjectGuid(bot.Guid), bot.Name, role, bot.TalentRole, subGroup, real)
            {
                Joined = bot.BotId == plan.Leader.BotId,
            };
            if (!real && _bots.TryGetValue(bot.BotId, out PlayerbotGroupBot running))
            {
                member.AI = new PlayerbotGroupAI(running.Session, _options);
                running.Brain.GroupHeld.Add((goal.QuestId, goal.ObjectiveEntry));
                _waiting.Remove(bot.BotId);
                _memberOf[bot.BotId] = group;
                if (goal.InInstance && running.Session.Player is { } player) PlayerbotAreaTriggers.AllowTeleports(player, true);
            }

            group.Members.Add(member);
        }

        group.HadQuest = goal.QuestId != 0 || group.Members.Any(m => !m.Real && _world?.FindOnlinePlayer(m.Guid) is { } p
            && QuestFor(p, goal.ObjectiveEntry) != 0);
        group.Approach = ApproachPoint(goal, plan.Leader.Position);
        _groups.Add(group);
        Totals = (Totals.Formed + 1, Totals.Completed, Totals.Failed);
        Note($"group {group.Id} formed for {goal}: leader {plan.Leader.Name}, "
             + string.Join(", ", group.Members.Select(m => $"{m.Name} {m.Role.ToString().ToLowerInvariant()}")));
        logger.LogInformation("Playerbot group {Group} formed for {Goal}, leader {Leader}, {Count} members", group.Id, goal, plan.Leader.Name, group.Members.Count);
    }

    // --- the group's life -------------------------------------------------------------------------------------------

    private void Advance(BotGroup group)
    {
        uint now = Now;
        // A bot that stopped, became scripted or joined a real player's group leaves this one.
        foreach (Member member in group.Members.Where(m => !m.Real).ToArray())
            if (!_bots.TryGetValue(member.BotId, out PlayerbotGroupBot bot) || bot.Session.Player is null)
                DropMember(group, member.BotId, "gone");
        if (!_groups.Contains(group)) return;
        if (group.LeaderPlayer(_world!) is not { } leader)
        {
            Finish(group, success: false, "leader-gone");
            return;
        }

        GroupManager? groupManager = GroupManagerOrNull();
        if (group.State != PlayerbotGroupState.Disbanding && group.State != PlayerbotGroupState.Forming && groupManager is not null)
        {
            // A member that left the server group (a real player leaving, a bot kicked) is no longer one.
            foreach (Member member in group.Members.Where(m => m.Joined && m.Guid != leader.Guid).ToArray())
                if (!groupManager.AreInSameGroup(leader.Guid, member.Guid)) DropMember(group, member.BotId, "left", member.Guid);
            if (!_groups.Contains(group)) return;
            if (group.Members.Count(m => !m.Real) < 2 && group.Goal.Size >= 2 && !group.Solo)
            {
                Finish(group, success: false, "too-few");
                return;
            }
        }

        if (group.State is not (PlayerbotGroupState.Forming or PlayerbotGroupState.Disbanding or PlayerbotGroupState.Leaving)
            && unchecked(now - group.FormedMs) > GoalTimeoutMs)
        {
            Finish(group, success: false, "timeout");
            return;
        }

        if (group.Members.Any(m => m.Joined && _world!.FindOnlinePlayer(m.Guid) is { IsAlive: true } p && p.Combat.IsInCombat))
            group.LastCombatMs = Math.Max(1u, now);

        switch (group.State)
        {
            case PlayerbotGroupState.Forming:
                AdvanceForming(group, leader, groupManager);
                break;
            case PlayerbotGroupState.Gathering:
                if (Gathered(group, leader)) Enter(group, PlayerbotGroupState.Travelling);
                else if (group.StepTimedOut(now)) Finish(group, success: false, "gather-timeout");
                break;
            case PlayerbotGroupState.Travelling:
                if (CheckWipe(group, leader)) break;
                if (leader.MapId == group.Goal.MeetingMap && Arrived(leader, group.Approach, 5f) || leader.MapId == group.Goal.MapId && group.Goal.InInstance)
                    Enter(group, group.Goal.InInstance ? PlayerbotGroupState.Entering : PlayerbotGroupState.Engaging);
                else if (group.StepTimedOut(now)) Finish(group, success: false, "travel-timeout");
                break;
            case PlayerbotGroupState.Entering:
                if (CheckWipe(group, leader)) break;
                if (group.Members.Where(m => m.Joined).All(m => _world!.FindOnlinePlayer(m.Guid) is not { } p || p.MapId == group.Goal.MapId || m.Real))
                    Enter(group, PlayerbotGroupState.Engaging);
                else if (group.StepTimedOut(now)) Finish(group, success: false, "enter-timeout");
                break;
            case PlayerbotGroupState.Engaging:
                if (Complete(group))
                {
                    Note($"group {group.Id} completed {group.Goal}");
                    group.Succeeded = true;
                    Enter(group, group.Goal.InInstance && leader.MapId == group.Goal.MapId ? PlayerbotGroupState.Leaving : PlayerbotGroupState.Disbanding);
                    break;
                }

                if (CheckWipe(group, leader)) break;
                // At the objective's spawn with nothing of it alive in sight (killed by someone else, waiting for its respawn).
                if (ObjectiveMissing(group, leader, now)) Finish(group, success: false, "objective-missing");
                break;
            case PlayerbotGroupState.Wiped:
                // The survivors get a moment to turn and run (the killer turns on its next victim at its next update).
                if (unchecked(now - group.StateSinceMs) >= WipeSettleMs && group.Members.Where(m => m.Joined).All(m => _world!.FindOnlinePlayer(m.Guid) is not { } p || !p.IsAlive || !p.Combat.IsInCombat))
                {
                    group.Failures++;
                    Note($"group {group.Id} wiped ({group.Failures}/{MaxFailures})");
                    if (group.Failures >= MaxFailures) Finish(group, success: false, "wiped");
                    else Enter(group, PlayerbotGroupState.Regrouping);
                }

                break;
            case PlayerbotGroupState.Regrouping:
                if (group.Members.Where(m => m.Joined && !m.Real).All(m => _world!.FindOnlinePlayer(m.Guid) is { IsAlive: true } p && !p.Combat.IsInCombat)
                    && !group.AnyResting(_world!))
                    Enter(group, PlayerbotGroupState.Gathering);
                else if (group.StepTimedOut(now)) Finish(group, success: false, "regroup-timeout");
                break;
            case PlayerbotGroupState.Leaving:
                if (group.Members.Where(m => m.Joined && !m.Real).All(m => _world!.FindOnlinePlayer(m.Guid) is not { } p
                        || WorldMaps.Of(_world!).Registry.Find(p.MapId) is not { IsDungeon: true }))
                    Enter(group, PlayerbotGroupState.Disbanding);
                else if (group.StepTimedOut(now)) BringOut(group);
                break;
            case PlayerbotGroupState.Disbanding:
                Disband(group);
                break;
        }
    }

    private void AdvanceForming(BotGroup group, Player leader, GroupManager? groupManager)
    {
        if (groupManager is null) return;
        uint now = Now;
        Group? formed = groupManager.GetGroup(leader.Guid);
        foreach (Member member in group.Members.Where(m => !m.Joined))
            if (formed?.IsMember(member.Guid) == true) member.Joined = true;

        bool needsRaid = group.Goal.Size > PlayerbotGroupContent.PartySize;
        if (needsRaid && formed is { IsRaid: false } && formed.MemberCount >= Group.MinMemberCount && formed.IsLeader(leader.Guid))
        {
            LeaderAct(group, leader, WorldOpcode.CmsgGroupRaidConvert, []);
            group.Raid = groupManager.GetGroup(leader.Guid)?.IsRaid == true;
        }

        // Invite the next ones (a party fills to five before it may become a raid; the raid takes the rest). Until the first
        // acceptance creates the group the leader is an invitee itself, and a second invitation is dropped (vmangos AddLeaderInvite):
        // one at a time until then.
        int room = formed is null ? 1 - group.Members.Count(m => m.Invited && !m.Joined)
            : (formed.IsRaid ? Group.MaxRaidSize : PlayerbotGroupContent.PartySize) - formed.MemberCount - group.Members.Count(m => m.Invited && !m.Joined);
        foreach (Member member in group.Members.Where(m => !m.Joined && !m.Invited).Take(Math.Max(0, room)).ToArray())
        {
            if (_world!.FindOnlinePlayer(member.Guid) is null) continue;
            member.Invited = true;
            member.InvitedMs = now;
            LeaderAct(group, leader, WorldOpcode.CmsgGroupInvite, Cstring(member.Name));
            if (groupManager.GetInvite(member.Guid) is null && !groupManager.AreInSameGroup(leader.Guid, member.Guid))
                member.Refused = true;
        }

        foreach (Member member in group.Members.Where(m => m.Real && m.Invited && !m.Joined))
            if (member.Refused || unchecked(now - member.InvitedMs) > InviteTimeoutMs || groupManager.GetInvite(member.Guid) is null && !(formed?.IsMember(member.Guid) ?? false))
            {
                _declinedPlayers[member.Guid] = unchecked(now + SetAsideMs);
                group.Members.Remove(member);
                Note($"group {group.Id}: {member.Name} did not join");
                break;
            }

        if (group.Members.Any(m => !m.Joined && (m.Refused || m.Invited && unchecked(now - m.InvitedMs) > InviteTimeoutMs)))
        {
            Finish(group, success: false, "invites-unanswered", setAside: false);
            return;
        }

        if (group.Members.Count < Math.Min(2, group.Goal.Size))
        {
            Finish(group, success: false, "too-few", setAside: false);
            return;
        }

        if (group.Members.All(m => m.Joined) && formed is not null)
        {
            if (needsRaid && !formed.IsRaid) return; // the conversion comes first
            group.Raid = formed.IsRaid;
            // Round robin (CMSG_LOOT_METHOD: method, master looter, threshold uncommon); then the raid's subgroups by role.
            LeaderAct(group, leader, WorldOpcode.CmsgLootMethod, LootMethodPayload((uint)LootMethod.RoundRobin, 0, Group.DefaultLootThreshold));
            if (formed.IsRaid)
                foreach (Member member in group.Members.Where(m => formed.Find(m.Guid) is { } slot && slot.SubGroup != m.SubGroup))
                    LeaderAct(group, leader, WorldOpcode.CmsgGroupChangeSubGroup, SubGroupPayload(member.Name, member.SubGroup));
            Note($"group {group.Id} complete: {formed.MemberCount} members{(formed.IsRaid ? ", raid" : string.Empty)}");
            Enter(group, PlayerbotGroupState.Gathering);
        }
    }

    /// <summary>
    /// Where the group stops before the content: <see cref="QuestApproachYards"/> short of the objective's spawn (outside its aggro) or
    /// <see cref="ApproachYards"/> short of the entrance trigger (outside its volume), on the side the leader comes from.
    /// </summary>
    internal static Vector3 ApproachPoint(PlayerbotGroupGoal goal, Vector3 from)
    {
        float yards = goal.InInstance ? ApproachYards : QuestApproachYards;
        Vector3 flat = new(from.X - goal.Meeting.X, from.Y - goal.Meeting.Y, 0);
        if (flat.Length() < yards) return from;
        return goal.Meeting + (Vector3.Normalize(flat) * yards);
    }

    /// <summary>How far short of an instance entrance the group gathers before it goes in (<see cref="ApproachPoint"/>).</summary>
    internal const float ApproachYards = 15f;

    /// <summary>How far short of an open-world objective's spawn the group gathers (beyond a creature's usual aggro radius).</summary>
    internal const float QuestApproachYards = 30f;

    private bool Gathered(BotGroup group, Player leader)
        => group.Members.Where(m => m.Joined && !m.Real && m.Guid != leader.Guid).All(m => _world!.FindOnlinePlayer(m.Guid) is not { } p
            || !p.IsAlive || ReferenceEquals(p.Map, leader.Map) && Distance(p, leader) <= GatherYards);

    private static bool Arrived(Player player, Vector3 point, float within)
        => Vector3.Distance(new Vector3(player.X, player.Y, player.Z), point) <= within;

    /// <summary>A wipe: every living member dead, or half the group or more dead while someone fights (<see cref="Party.PlayerbotPartyAI.IsWiping"/>).</summary>
    private bool CheckWipe(BotGroup group, Player leader)
    {
        int alive = 0, dead = 0;
        bool fighting = false;
        foreach (Member member in group.Members.Where(m => m.Joined))
        {
            if (_world!.FindOnlinePlayer(member.Guid) is not { } player) continue;
            if (player.IsAlive)
            {
                alive++;
                fighting |= player.Combat.IsInCombat;
            }
            else dead++;
        }

        if (alive + dead == 0 || dead == 0) return false;
        // The fight goes on, or has just ended with the members' deaths (the killer may not have turned on anyone yet).
        fighting |= group.InFight(Now);
        if (alive > 0 && (!fighting || dead * 2 < alive + dead)) return false;
        Note($"group {group.Id} is wiping: {dead} dead, {alive} alive");
        Enter(group, PlayerbotGroupState.Wiped);
        return true;
    }

    /// <summary>
    /// The goal is reached: every bot member that has a quest asking for the objective has its count, and at least one had the quest
    /// (or, without one, the group killed the objective creature it engaged).
    /// </summary>
    private bool Complete(BotGroup group)
    {
        group.UpdateObjective(_world!);
        if (!group.HadQuest) return group.ObjectiveKilled;
        foreach (Member member in group.Members.Where(m => m.Joined && !m.Real))
            if (_world!.FindOnlinePlayer(member.Guid) is { } player && !ObjectiveDone(player, group.Goal.ObjectiveEntry)) return false;
        return true;
    }

    /// <summary>The members did not get out of the instance in time: the teleport service brings them to the entrance outside.</summary>
    private void BringOut(BotGroup group)
    {
        if (_world is null || services.GetService<TeleportFeature>()?.Teleports is not { } teleports) return;
        foreach (Member member in group.Members.Where(m => m.Joined && !m.Real))
            if (_world.FindOnlinePlayer(member.Guid) is { IsAlive: true } player && player.MapId == group.Goal.MapId && !teleports.IsBeingTeleported(player))
                teleports.TeleportTo(player, group.Goal.MeetingMap, group.Goal.Meeting.X, group.Goal.Meeting.Y, group.Goal.Meeting.Z, 0f);
        group.StateSinceMs = Now;
        Note($"group {group.Id} could not walk out: brought to the entrance");
    }

    /// <summary>How long the group waits at an objective's spawn with none of it alive in sight before it gives the goal up.</summary>
    internal const uint ObjectiveMissingMs = 120_000;

    /// <summary>The leader stands at the objective's spawn and no living objective creature is in sight, for <see cref="ObjectiveMissingMs"/>.</summary>
    private bool ObjectiveMissing(BotGroup group, Player leader, uint now)
    {
        bool there = leader.MapId == group.Goal.MapId
            && Vector3.Distance(new Vector3(leader.X, leader.Y, leader.Z), group.Goal.Objective) <= 15f;
        bool seen = group.Goal.ObjectiveEntry != 0 && leader.Map is { } map && leader.VisibleObjects.Any(guid =>
            map.FindObject(guid) is Game.Creatures.Creature { IsAlive: true } creature && creature.Entry == group.Goal.ObjectiveEntry);
        if (!there || seen || leader.Combat.IsInCombat)
        {
            group.MissingSinceMs = 0;
            return false;
        }

        if (group.MissingSinceMs == 0) group.MissingSinceMs = Math.Max(1u, now);
        return unchecked(now - group.MissingSinceMs) > ObjectiveMissingMs;
    }

    private void Enter(BotGroup group, PlayerbotGroupState state)
    {
        group.State = state;
        group.StateSinceMs = Now;
        if (state == PlayerbotGroupState.Gathering) group.ObjectiveKilled = false;
    }

    /// <summary>End the group: done, or given up (the goal is then set aside for every bot member unless <paramref name="setAside"/> is off).</summary>
    private void Finish(BotGroup group, bool success, string reason, bool setAside = true)
    {
        if (!success && setAside)
            foreach (Member member in group.Members.Where(m => !m.Real))
                SetAside(member.BotId, group.Goal, suspendInBrain: true);
        if (!success) Totals = (Totals.Formed, Totals.Completed, Totals.Failed + 1);
        if (!success) Note($"group {group.Id} gave up {group.Goal}: {reason}");
        group.Succeeded = success;
        group.Reason = reason;
        // Inside an instance a failed group still walks out before it disbands.
        bool inside = group.Members.Any(m => !m.Real && _world?.FindOnlinePlayer(m.Guid) is { } p && p.MapId == group.Goal.MapId && group.Goal.InInstance);
        Enter(group, inside && group.State != PlayerbotGroupState.Leaving ? PlayerbotGroupState.Leaving : PlayerbotGroupState.Disbanding);
        if (group.State == PlayerbotGroupState.Disbanding) Disband(group);
    }

    /// <summary>Every bot member leaves the group (CMSG_GROUP_DISBAND, the client's leave) and goes back to its brain.</summary>
    private void Disband(BotGroup group)
    {
        if (!group.Succeeded && group.Reason is null) group.Reason = "disbanded";
        if (group.Succeeded && group.Reason is null)
        {
            group.Reason = "completed";
            Totals = (Totals.Formed, Totals.Completed + 1, Totals.Failed);
        }

        foreach (Member member in group.Members.Where(m => !m.Real).ToArray()) Release(group, member);
        _groups.Remove(group);
        Note($"group {group.Id} disbanded ({group.Reason})");
        logger.LogInformation("Playerbot group {Group} disbanded ({Reason})", group.Id, group.Reason);
    }

    private void Release(BotGroup group, Member member, ObjectGuid? leftAlready = null)
    {
        _memberOf.Remove(member.BotId);
        if (!_bots.TryGetValue(member.BotId, out PlayerbotGroupBot bot)) return;
        bot.Brain.GroupHeld.Remove((group.Goal.QuestId, group.Goal.ObjectiveEntry));
        if (bot.Session.Player is not { } player) return;
        member.AI?.Release(player);
        PlayerbotAreaTriggers.AllowTeleports(player, false);
        if (leftAlready is null && GroupManagerOrNull() is { } groupManager && (groupManager.GetGroup(player.Guid) is { } server && server.IsMember(player.Guid)
                || groupManager.GetInvite(player.Guid) is not null))
            Act(bot.Session, groupManager.GetInvite(player.Guid) is not null && groupManager.GetGroup(player.Guid) is null
                ? WorldOpcode.CmsgGroupDecline : WorldOpcode.CmsgGroupDisband, [], budgeted: false);
        bot.Brain.ResumeAfterGroup();
    }

    private void DropMember(BotGroup group, Guid botId, string reason, ObjectGuid? guid = null)
    {
        Member? member = botId == Guid.Empty ? group.Members.FirstOrDefault(m => m.Real && m.Guid == guid) : group.Find(botId);
        if (member is null) return;
        group.Members.Remove(member);
        if (!member.Real) Release(group, member, guid);
        Note($"group {group.Id}: {member.Name} {reason}");
        if (member.BotId == group.LeaderBotId && !member.Real) Finish(group, success: false, "leader-" + reason);
    }

    private void SetAside(Guid botId, PlayerbotGroupGoal goal, bool suspendInBrain)
    {
        _setAside[(botId, goal.Key)] = unchecked(Now + SetAsideMs);
        if (!suspendInBrain || !_bots.TryGetValue(botId, out PlayerbotGroupBot bot)) return;
        if (goal.QuestId != 0) bot.Brain.Suspensions.SuspendQuest(goal.QuestId, Now);
        bot.Brain.Suspensions.SuspendEntry(goal.ObjectiveEntry, Now);
    }

    /// <summary>The bot stops waiting (and the objective is no longer held for a group).</summary>
    private void Unhold(Guid botId, PlayerbotGroupGoal? goal)
    {
        if (!_waiting.Remove(botId, out Waiting? waiting)) return;
        if (_bots.TryGetValue(botId, out PlayerbotGroupBot bot))
            bot.Brain.GroupHeld.Remove(((goal ?? waiting.Goal).QuestId, (goal ?? waiting.Goal).ObjectiveEntry));
    }

    /// <summary>
    /// A bot that logged in (or was left) inside an instance without a group walks out through the exit on its own (a group of one in
    /// <see cref="PlayerbotGroupState.Leaving"/>): an autonomous brain has no way out of an instance.
    /// </summary>
    private bool EvacuateIfStranded(PlayerbotGroupBot bot, Player player)
    {
        if (!player.IsAlive || player.Map is not { } map || WorldMaps.Of(_world!).Registry.Find(player.MapId) is not { IsDungeon: true }) return false;
        if (GroupManagerOrNull()?.GetGroup(player.Guid) is not null) return false;
        if (ExitOf(_world!, player.MapId) is not { } exit || WorldMaps.Of(_world!).FindAreaTriggerTeleport(exit.Id) is not { } teleport) return false;
        var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Dungeon, 0, 0, player.MapId, teleport.TargetMap,
            new Vector3(teleport.TargetX, teleport.TargetY, teleport.TargetZ), 0, new Vector3(exit.X, exit.Y, exit.Z), 1, 0, "stranded");
        var group = new BotGroup(_nextId++, goal, bot.BotId, player.Guid, Now) { Solo = true, Succeeded = true, Reason = "evacuated" };
        group.Members.Add(new Member(bot.BotId, player.Guid, player.Name, PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, 0, false)
        {
            Joined = true, AI = new PlayerbotGroupAI(bot.Session, _options),
        });
        _groups.Add(group);
        _memberOf[bot.BotId] = group;
        PlayerbotAreaTriggers.AllowTeleports(player, true);
        Enter(group, PlayerbotGroupState.Leaving);
        Note($"{player.Name} is inside map {player.MapId} without a group: walking out");
        return true;
    }

    // --- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>A member that is a managed bot online.</summary>
    private bool IsOnlineBot(ObjectGuid guid) => _world?.FindOnlinePlayer(guid) is { Session: WorldSession { IsManaged: true } };

    private Guid BotIdOf(ObjectGuid guid)
    {
        foreach ((Guid botId, PlayerbotGroupBot bot) in _bots)
            if (bot.Session.Player?.Guid == guid) return botId;
        return Guid.Empty;
    }

    private void LeaderAct(BotGroup group, Player leader, WorldOpcode opcode, byte[] payload)
    {
        if (_bots.TryGetValue(group.LeaderBotId, out PlayerbotGroupBot bot) && bot.Session.Player?.Guid == leader.Guid)
            Act(bot.Session, opcode, payload, budgeted: false);
    }

    /// <summary>
    /// Run one client opcode through its world handler: <paramref name="budgeted"/> draws on the shared per-tick action budget like
    /// the brain's; group business (invitations, answers, leaving) is a client's own and never waits.
    /// </summary>
    internal static bool Act(WorldSession session, WorldOpcode opcode, byte[] payload, bool budgeted)
    {
        if (budgeted) return session.TryManagedAction(opcode, payload);
        ManagedActionBudget? budget = session.ManagedBudget;
        session.ManagedBudget = null;
        try
        {
            return session.TryManagedAction(opcode, payload);
        }
        finally
        {
            session.ManagedBudget = budget;
        }
    }

    private static byte[] Cstring(string text)
    {
        var writer = new PacketWriter(text.Length + 1);
        writer.WriteCString(text);
        return writer.ToArray();
    }

    private static byte[] LootMethodPayload(uint method, ulong master, uint threshold)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(method);
        writer.WriteUInt64(master);
        writer.WriteUInt32(threshold);
        return writer.ToArray();
    }

    private static byte[] SubGroupPayload(string name, byte subGroup)
    {
        var writer = new PacketWriter(name.Length + 2);
        writer.WriteCString(name);
        writer.WriteByte(subGroup);
        return writer.ToArray();
    }

    private GroupManager? GroupManagerOrNull()
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Groups;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void Note(string line)
    {
        Events.Add(string.Create(CultureInfo.InvariantCulture, $"{Now / 1000}s {line}"));
        if (Events.Count > 32) Events.RemoveAt(0);
    }

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));

    // --- the group --------------------------------------------------------------------------------------------------

    /// <summary>One bot-led group and its goal.</summary>
    internal sealed class BotGroup(int id, PlayerbotGroupGoal goal, Guid leaderBotId, ObjectGuid leaderGuid, uint formedMs)
    {
        public int Id { get; } = id;
        public PlayerbotGroupGoal Goal { get; } = goal;
        public Guid LeaderBotId { get; } = leaderBotId;
        public ObjectGuid LeaderGuid { get; } = leaderGuid;
        public uint FormedMs { get; } = formedMs;
        public List<Member> Members { get; } = [];
        public PlayerbotGroupState State { get; set; } = PlayerbotGroupState.Forming;
        public uint StateSinceMs { get; set; } = formedMs;
        public int Failures { get; set; }
        public bool Raid { get; set; }
        public bool Solo { get; init; }
        public bool HadQuest { get; set; }

        /// <summary>Where the group gathers before the content (<see cref="ApproachPoint"/>).</summary>
        public Vector3 Approach { get; set; }
        public bool ObjectiveKilled { get; set; }

        /// <summary>When a living member was last seen in combat (0: never).</summary>
        public uint LastCombatMs { get; set; }

        /// <summary>A member fought within the last <see cref="FightMemoryMs"/>.</summary>
        public bool InFight(uint now) => LastCombatMs != 0 && unchecked(now - LastCombatMs) <= FightMemoryMs;

        /// <summary>How long after the last member in combat the group still counts as fighting (a wipe; no resurrection yet).</summary>
        public const uint FightMemoryMs = 5_000;

        /// <summary>Since when the leader stands at the objective's spawn without the objective in sight (0: it does not).</summary>
        public uint MissingSinceMs { get; set; }
        public ObjectGuid Objective { get; private set; }
        public bool Succeeded { get; set; }
        public string? Reason { get; set; }

        public Member? Find(Guid botId) => botId == Guid.Empty ? null : Members.FirstOrDefault(m => m.BotId == botId);

        public Player? LeaderPlayer(WorldRuntime world) => world.FindOnlinePlayer(LeaderGuid);

        public bool StepTimedOut(uint now) => unchecked(now - StateSinceMs) > StepTimeoutMs;

        /// <summary>The leader engaged an objective creature: its death (seen by <see cref="ObjectiveKilled"/>) completes a goal without a quest.</summary>
        public void NoteObjective(Game.Creatures.Creature creature) => Objective = creature.Guid;

        /// <summary>Whether a bot member is eating or drinking now (the leader waits).</summary>
        public bool AnyResting(WorldRuntime world) => Members.Any(m => m.AI is { Resting: true });

        /// <summary>Whether a living member near <paramref name="dead"/> knows a resurrection spell (the dead member waits for it).</summary>
        public bool CanResurrect(WorldRuntime world, Player dead)
            => Members.Any(m => m.Guid != dead.Guid && m.AI is { } ai && world.FindOnlinePlayer(m.Guid) is { IsAlive: true } living
                && ReferenceEquals(living.Map, dead.Map) && ai.ResurrectionSpell(living) is not null);

        public void UpdateObjective(WorldRuntime world)
        {
            if (Objective.IsEmpty || ObjectiveKilled) return;
            foreach (Member member in Members)
                if (world.FindOnlinePlayer(member.Guid)?.Map?.FindObject(Objective) is Game.Creatures.Creature creature && !creature.IsAlive)
                    ObjectiveKilled = true;
        }
    }

    /// <summary>One member: a managed bot (with its group AI) or a real player (<see cref="Real"/>, never driven).</summary>
    internal sealed class Member(Guid botId, ObjectGuid guid, string name, PlayerbotGroupRole role, PlayerbotRole talentRole, byte subGroup, bool real)
    {
        public Guid BotId { get; } = botId;
        public ObjectGuid Guid { get; } = guid;
        public string Name { get; } = name;
        public PlayerbotGroupRole Role { get; } = role;
        public PlayerbotRole TalentRole { get; } = talentRole;
        public byte SubGroup { get; } = subGroup;
        public bool Real { get; } = real;
        public bool Joined { get; set; }
        public bool Invited { get; set; }
        public bool Refused { get; set; }
        public uint InvitedMs { get; set; }
        public PlayerbotGroupAI? AI { get; set; }
    }
}
