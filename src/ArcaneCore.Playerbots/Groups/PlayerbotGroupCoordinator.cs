using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
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
using ArcaneCore.World.Bots.Progression;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

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
/// <para>
/// <b>Runs</b> (<c>PlayerbotGroupCoordinator.Runs.cs</c>): besides quests, free bots of a fitting level group up on their own to run a
/// dungeon or raid wing (<see cref="PlayerbotRunCatalogue"/>); the group carries a <see cref="PlayerbotRunMachine"/> and ends after the
/// last boss, at <c>MaxWipes</c> or when it cannot go on.
/// </para>
/// </summary>
public sealed partial class PlayerbotGroupCoordinator(IServiceProvider services, ILogger<PlayerbotGroupCoordinator> logger) : IDetachableWorldFeature
{
    /// <summary>The bots' social memory seam (set by the feature on attach; null in a host without it). Group events are recorded through it.</summary>
    internal BotSocial.PlayerbotSocialRecorder? Social { get; set; }

    /// <summary>A member joined the server group: each managed bot among it and the members that joined before remembers the other as grouped (an invitation alone is not being grouped).</summary>
    private void NoteJoined(BotGroup group, Member joined)
    {
        BotSocial.PlayerbotSocialRecorder? social = Social;
        if (social is not { Enabled: true }) return;
        social.Guarded(() =>
        {
            foreach (Member other in group.Members)
            {
                if (ReferenceEquals(other, joined) || !other.Joined) continue;
                if (!joined.Real) social.Note(joined.Guid, other.Guid, BotSocial.PlayerbotSocialEvent.Grouped);
                if (!other.Real) social.Note(other.Guid, joined.Guid, BotSocial.PlayerbotSocialEvent.Grouped);
            }
        });
    }

    /// <summary>Record <paramref name="e"/> for every ordered pair of the group's members: each managed bot remembers each other member.</summary>
    private void NotePairs(BotGroup group, BotSocial.PlayerbotSocialEvent e)
    {
        BotSocial.PlayerbotSocialRecorder? social = Social;
        if (social is not { Enabled: true }) return;
        social.Guarded(() =>
        {
            foreach (Member a in group.Members)
            {
                if (a.Real) continue;
                foreach (Member b in group.Members)
                    if (!ReferenceEquals(a, b)) social.Note(a.Guid, b.Guid, e);
            }
        });
    }

    /// <summary>Every bot member remembers the blamed member: it died first and the group wiped, or it left the run before the end.</summary>
    private void NoteAgainst(BotGroup group, ObjectGuid blamed, BotSocial.PlayerbotSocialEvent e)
    {
        BotSocial.PlayerbotSocialRecorder? social = Social;
        if (social is not { Enabled: true } || blamed.IsEmpty) return;
        social.Guarded(() =>
        {
            foreach (Member a in group.Members)
                if (!a.Real && a.Guid != blamed) social.Note(a.Guid, blamed, e);
        });
    }

    /// <summary>Each bot member remembers the role every other bot member played in this group (replacing the role it noted before); a real player's role is not known.</summary>
    private void NoteRoles(BotGroup group)
    {
        BotSocial.PlayerbotSocialRecorder? social = Social;
        if (social is not { Enabled: true }) return;
        social.Guarded(() =>
        {
            foreach (Member a in group.Members)
            {
                if (a.Real) continue;
                foreach (Member b in group.Members)
                    if (!ReferenceEquals(a, b) && !b.Real) social.SetRole(a.Guid, b.Guid, RoleFlag(b.Role));
            }
        });
    }

    private static PlayerbotRelationFlags RoleFlag(PlayerbotGroupRole role) => role switch
    {
        PlayerbotGroupRole.Tank => PlayerbotRelationFlags.PrefersTank,
        PlayerbotGroupRole.Healer => PlayerbotRelationFlags.PrefersHealer,
        _ => PlayerbotRelationFlags.PrefersDamage,
    };

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

    /// <summary>How long the gathering waits for a missing member before it catches the member up (or goes on without it).</summary>
    internal const uint GatherCatchUpMs = 60_000;

    /// <summary>How many times the leaving state teleports stragglers to the entrance before it lets the group go.</summary>
    internal const int MaxBringOuts = 2;

    /// <summary>A real player is invited from at most this far (<see cref="PlayerbotGroupOptions.InvitePlayers"/>).</summary>
    internal const float InvitePlayerYards = 100f;

    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private readonly Dictionary<Guid, Waiting> _waiting = [];
    private readonly List<BotGroup> _groups = [];
    private readonly Dictionary<Guid, BotGroup> _memberOf = [];
    private volatile bool _shuttingDown; // set once by BeginShutdown from the stopping thread; read on the world thread
    private readonly Dictionary<(Guid Bot, (PlayerbotGroupGoalKind, uint) Key), uint> _setAside = [];
    private readonly Dictionary<ObjectGuid, uint> _declinedPlayers = [];
    private readonly Dictionary<Guid, PlayerbotGroupBot> _bots = [];
    private readonly Dictionary<ObjectGuid, Guid> _botByPlayer = [];
    private readonly List<PlayerbotGroupBot> _orderedBots = [];
    private readonly List<BotGroup> _groupsScratch = [];
    private readonly List<Member> _membersScratch = [];
    private readonly List<Guid> _waitingKeysScratch = [];
    private readonly List<(uint QuestId, QuestStatusData Status)> _questStatusesScratch = new(32);
    private readonly HashSet<string> _spellNamesScratch = new(StringComparer.Ordinal);
    private CreatureContent? _dungeonMapsCreatures;
    private MapRegistry? _dungeonMapsRegistry;
    private (uint MapId, MapTemplate Instance)[] _dungeonMapsWithSpawns = [];
    private WorldRuntime? _world;
    private uint _scanElapsed = ScanMs;
    private int _nextId = 1;
    private int _nextWaitingGroupId = 1;

    /// <summary>The groups formed so far, completed so far and given up so far (status and tests).</summary>
    internal (int Formed, int Completed, int Failed) Totals { get; private set; }

    /// <summary>What happened last to bot groups, newest last (at most 32 lines; status and tests).</summary>
    internal List<string> Events { get; } = [];

    private readonly List<(Guid BotId, string Name, string Report)> _stalls = [];

    /// <summary>Groups the stall watch found without progress so far (status and tests).</summary>
    internal int StallCount { get; private set; }

    /// <summary>
    /// The stall reports since the last call, one per stalled group (its leader's, in the brains' stall line shape:
    /// <c>stalled 120s: goal=Group target=114 quest=9 at x,y,z map 0</c>). <see cref="ManagedPlayerbotFeature"/> logs them as it logs a
    /// brain's stall, so bot QA (PlayerbotQaSignalParser) sees them.
    /// </summary>
    internal IReadOnlyList<(Guid BotId, string Name, string Report)> TakeStalls()
    {
        if (_stalls.Count == 0) return [];
        (Guid, string, string)[] taken = [.. _stalls];
        _stalls.Clear();
        return taken;
    }

    // --- lifecycle (docs/design/hot-reload-tiers.md, section 4.2) -------------------------------------------------------

    /// <summary>
    /// Stateless: a detach ends every bot-led group and wait (the bots leave their server groups and go back to their brains) and
    /// forgets the set-asides; the bots are matched again after the next attach. The encounters live in the core catalogue.
    /// </summary>
    public DetachKind DetachKind => DetachKind.Stateless;

    private FeatureSubscriptions? _subscriptions;
    private bool _detaching;

    /// <summary>Whether the coordinator is attached to a world (a detached one ignores the managed feature's calls).</summary>
    internal bool IsAttached => _world is not null;

    public void Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(nameof(PlayerbotGroupCoordinator)));

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(subscriptions);
        _subscriptions = subscriptions;
        // The raid runs' entrance waivers, consulted by the teleport handler for exactly as long as this coordinator is attached.
        if (services.GetService<ITeleportWaiverRegistry>() is { } waivers) subscriptions.Add(waivers.Register(new RunWaiver(this)));
        _world = world;
        _detaching = false;
        LoadEncounters();
    }

    // Members of this coordinator's own groups that were kicked since the last scan: leaving by a kick is not abandoning the run. An entry goes
    // when the scan consumes it or when its group ends; a kick anywhere else in the world is not kept (it can never be consumed).
    private readonly HashSet<ObjectGuid> _kicked = [];

    /// <summary>Kicks held for the next member scan (tests).</summary>
    internal int KickedPending => _kicked.Count;

    internal void OnMemberKicked(ObjectGuid kicker, ObjectGuid kicked)
    {
        if (_groups.Any(g => g.Members.Any(m => m.Guid == kicked))) _kicked.Add(kicked);
        Social?.Note(kicked, kicker, BotSocial.PlayerbotSocialEvent.Kicked);
    }

    public Task QuiesceAsync(CancellationToken cancellationToken) => Task.CompletedTask; // world-thread only: nothing runs in the background

    public void Detach(FeatureHandover handover)
    {
        _detaching = true;
        try
        {
            // A siege ends with the reload (memory only, never carried over): the watching GMs are told why.
            if (_siege is { } running) TellWatchers(running, $"Siege {running.Id} ended: stopped: reload");
            foreach (Guid botId in _memberOf.Keys.Concat(_waiting.Keys).Distinct().ToArray()) Forget(botId);
            foreach (BotGroup group in _groups.ToArray()) Finish(group, success: false, "coordinator-detached");
        }
        finally
        {
            _siege = null;
            _siegeMembers = [];
            _siegeWaypoints.Clear();
            _siegeWaypointMap = 0;
            _siegeViews.Clear();
            ResetWorldBosses();
            _groups.Clear();
            _memberOf.Clear();
            _waiting.Clear();
            ResetRecruiting();
            _setAside.Clear();
            ClearFit();
            _failStreaks.Clear();
            _declinedPlayers.Clear();
            _leaderChanges.Clear();
            _leadersWatched = null;
            _bots.Clear();
            _botByPlayer.Clear();
            _orderedBots.Clear();
            _groupsScratch.Clear();
            _membersScratch.Clear();
            _waitingKeysScratch.Clear();
            _questStatusesScratch.Clear();
            _spellNamesScratch.Clear();
            _stalls.Clear();
            _world = null;
            _subscriptions = null;
        }
    }

    /// <summary>The coordinator's area trigger waiver, registered with the core's waiver registry while it is attached.</summary>
    private sealed class RunWaiver(PlayerbotGroupCoordinator owner) : IAreaTriggerWaiver
    {
        public AreaTriggerWaivers Waives(Player player, AreaTriggerTeleport teleport) => owner.Waives(player, teleport);
    }

    internal PlayerbotGroupOptions Options => _options.Groups;

    /// <summary>The groups running now (world thread; tests and the GM commands).</summary>
    internal IReadOnlyList<BotGroup> Groups => _groups;

    /// <summary>The bots waiting for partners, with their goal and since when (world thread).</summary>
    internal IEnumerable<(Guid BotId, string Name, PlayerbotGroupGoal Goal, uint SinceMs)> WaitingBots
        => _waiting.Select(w => (w.Key, w.Value.Name, w.Value.Goal, w.Value.SinceMs));

    /// <summary>The goal of a waiting bot (world thread); false when it does not wait.</summary>
    internal bool TryGetWaitingGoal(Guid botId, out PlayerbotGroupGoal goal)
    {
        if (_waiting.TryGetValue(botId, out Waiting? waiting))
        {
            goal = waiting.Goal;
            return true;
        }

        goal = default!;
        return false;
    }

    /// <summary>Whether <paramref name="botId"/> set aside the goal with <paramref name="key"/> (tests and status).</summary>
    internal bool IsSetAside(Guid botId, (PlayerbotGroupGoalKind, uint) key)
        => _setAside.TryGetValue((botId, key), out uint until) && unchecked((int)(until - Now)) > 0;

    private uint Now => _world?.NowMs ?? 0;

    // --- the feature's calls ------------------------------------------------------------------------------------------

    /// <summary>A running bot as the coordinator sees it (<see cref="Update"/>); <see cref="Scripted"/> when a controller holds it.</summary>
    internal readonly record struct PlayerbotGroupBot(Guid BotId, WorldSession Session, PlayerbotBrain Brain, bool Free, bool Scripted = false);

    internal bool NeedsBotSnapshot(uint elapsedMs)
        => (_bots.Count == 0 && _groups.Count > 0) || _leaderChanges.Count > 0 || unchecked(_scanElapsed + elapsedMs) >= ScanMs;

    internal void AdvanceIdle(uint elapsedMs)
    {
        if (_world is null) return;
        TakeLeaderChanges();
        AdvanceGroups();
        _scanElapsed = unchecked(_scanElapsed + elapsedMs);
    }

    private void AdvanceGroups()
    {
        if (_groups.Count == 0)
        {
            if (_siege is not null) TickSiegeSafely(); // every raid is gone: the siege ends
            return;
        }

        _groupsScratch.Clear();
        _groupsScratch.AddRange(_groups);
        for (int i = 0; i < _groupsScratch.Count; i++)
        {
            BotGroup group = _groupsScratch[i];
            try
            {
                Advance(group);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.groups", ex);
                PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.CoordinatorFault,
                    string.Create(CultureInfo.InvariantCulture, $"group {group.Id} (leader {group.LeaderGuid.Value:X})"),
                    string.Create(CultureInfo.InvariantCulture, $"group {group.Id} coordinator update failed ({ex.GetType().Name}: {ex.Message})"),
                    _world?.Identity);
                logger.LogFault("playerbot.group-coordinator", ex, "Playerbot group {Group} failed during coordinator update", group.Id);
            }
        }

        if (_siege is not null) TickSiegeSafely();
    }

    private void RebuildBotSnapshot(IReadOnlyList<PlayerbotGroupBot> bots)
    {
        _bots.Clear();
        _bots.EnsureCapacity(bots.Count);
        _botByPlayer.Clear();
        _botByPlayer.EnsureCapacity(bots.Count);
        _orderedBots.Clear();
        _orderedBots.EnsureCapacity(bots.Count);
        bool ordered = true;
        Guid prev = Guid.Empty;
        for (int i = 0; i < bots.Count; i++)
        {
            PlayerbotGroupBot bot = bots[i];
            _bots[bot.BotId] = bot;
            if (bot.Session.Player is { } player) _botByPlayer[player.Guid] = bot.BotId;
            _orderedBots.Add(bot);
            if (ordered && bot.BotId.CompareTo(prev) < 0) ordered = false;
            prev = bot.BotId;
        }
        if (!ordered) _orderedBots.Sort(static (a, b) => a.BotId.CompareTo(b.BotId));
    }

    /// <summary>
    /// One world tick (world thread): the running bots (<paramref name="bots"/>; <see cref="PlayerbotGroupBot.Free"/> when autonomous and
    /// not in a real player's group), the groups' states, and every <see cref="ScanMs"/> the needs and the matching.
    /// </summary>
    internal void Update(IReadOnlyList<PlayerbotGroupBot> bots, uint elapsedMs)
    {
        if (_world is null) return; // detached
        uint nextScanElapsed = unchecked(_scanElapsed + elapsedMs);
        bool scanDue = nextScanElapsed >= ScanMs;
        // For large bot fleets (> 64), skip rebuilding the 4,500-entry dictionaries on non-scan ticks when the fleet count is unchanged
        // and no leader handoff is pending; active groups hold live session/brain references and Forget() removes stopped bots immediately.
        if (scanDue || bots.Count <= 64 || _bots.Count != bots.Count || _leaderChanges.Count > 0)
            RebuildBotSnapshot(bots);
        TakeLeaderChanges(); // leader handoffs (PlayerbotGroupCoordinator.Handoff.cs)
        AdvanceGroups();

        _scanElapsed = nextScanElapsed;
        if (!scanDue) return;
        _scanElapsed = 0;
        Scan();
    }

    /// <summary>Whether the group AI drives <paramref name="botId"/> now (it is a member of a bot-led group).</summary>
    internal bool Drives(Guid botId) => _memberOf.Count > 0 && _memberOf.ContainsKey(botId);

    /// <summary>Whether <paramref name="botId"/> is in a bot-led group or waits for partners (the battlegrounds coordinator leaves it alone).</summary>
    internal bool Involves(Guid botId) => (_memberOf.Count > 0 && _memberOf.ContainsKey(botId)) || (_waiting.Count > 0 && _waiting.ContainsKey(botId));

    /// <summary>One tick of a member's group AI (world thread, within the shared action budget).</summary>
    internal void UpdateMember(Guid botId, Player player, uint elapsedMs)
    {
        if (_memberOf.Count == 0 || !_memberOf.TryGetValue(botId, out BotGroup? group) || group.Find(botId) is not { AI: { } ai } member) return;
        ai.Update(player, group, member, elapsedMs);
    }

    /// <summary>The group AI of a member (inspection and tests).</summary>
    internal PlayerbotGroupAI? FindAI(Guid botId) => _memberOf.Count > 0 && _memberOf.TryGetValue(botId, out BotGroup? group) ? group.Find(botId)?.AI : null;

    /// <summary>The group <paramref name="botId"/> belongs to (tests and status).</summary>
    internal BotGroup? GroupOf(Guid botId) => _memberOf.Count > 0 ? _memberOf.GetValueOrDefault(botId) : null;

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

    /// <summary>The host began stopping its bots: groups still end and release their members, but a leader or member stopped by the shutdown is not a finding. Any thread; never reset.</summary>
    internal void BeginShutdown() => _shuttingDown = true;

    /// <summary>A bot stopped, became scripted or left the world: it leaves its group (or its wait).</summary>
    internal void Forget(Guid botId)
    {
        _waiting.Remove(botId);
        // The member is released first (Release): it reads the bot's session from the snapshot to send the leave and check it took.
        if (_memberOf.TryGetValue(botId, out BotGroup? group)) DropMember(group, botId, "stopped");
        if (_bots.Remove(botId, out PlayerbotGroupBot removed) && removed.Session.Player is { } player)
            _botByPlayer.Remove(player.Guid);
    }

    /// <summary>The group part of a bot's status line: <c>group=3:tank:engaging</c>, <c>group=waiting:quest:990700</c>, or null.</summary>
    internal string? Describe(Guid botId)
    {
        if (_memberOf.Count > 0 && _memberOf.TryGetValue(botId, out BotGroup? group) && group.Find(botId) is { } member)
            return member.Describe(group);
        if (_waiting.Count > 0 && _waiting.TryGetValue(botId, out Waiting? waiting))
            return waiting.Describe();
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
            text.Append(CultureInfo.InvariantCulture, $" {(group.Raid ? "raid" : "party")} failures={group.Failures} age={unchecked(Now - group.FormedMs) / 1000}s");
            if (group.Handoff is { } handoff)
                text.Append(CultureInfo.InvariantCulture, $" handoff-from={handoff.HandedByName}{(handoff.Sweep is { } sweep ? $" clear-list={sweep.Remaining}/{sweep.Initial}" : string.Empty)}");
            if (group.Run is { } reported) text.Append(' ').Append(DescribeRun(reported));
            if (group.WorldBoss is { } bossRaid)
                text.Append(CultureInfo.InvariantCulture, $" worldboss={bossRaid.Boss.Name}:{(bossRaid.Released ? "pulling" : bossRaid.PullHeld ? "held-" + bossRaid.Hold.ToString().ToLowerInvariant() : "approaching")}:wipes{bossRaid.Wipes}");
            if (group.Siege is { } sieged)
                text.Append(CultureInfo.InvariantCulture, $" siege={sieged.Siege.Id}:raid{sieged.Index}:{sieged.Order.ToString().ToLowerInvariant()}:step{sieged.Siege.Step}/{sieged.Siege.Waypoints.Count}");
            text.Append(" members=");
            text.AppendJoin(',', group.Members.Select(m =>
                $"{m.Name}({m.Role.ToString().ToLowerInvariant()}{(m.Guid == group.LeaderGuid ? ",leader" : string.Empty)}{(m.Real ? ",player" : string.Empty)}{(m.Joined ? string.Empty : ",invited")}{(group.Raid ? $",g{m.SubGroup + 1}" : string.Empty)})"));
            // What the leader did at its last think, and how long the group has gone without progress (PlayerbotGroupStallWatch).
            if (group.Find(group.LeaderBotId)?.AI is { } leading) text.Append(" step=").Append(leading.Step);
            if (group.Stall.Strikes > 0) text.Append(CultureInfo.InvariantCulture, $" stalled={group.Stall.Strikes}");
            lines.Add(text.ToString());
        }

        if (RunSummary() is { } runSummary) lines.Add(runSummary);
        lines.AddRange(WorldBossReport());
        lines.Add(HandoffSummary());
        foreach ((Guid _, Waiting waiting) in _waiting.OrderBy(w => w.Value.SinceMs))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"waiting {waiting.Name} {waiting.Goal} for {unchecked(Now - waiting.SinceMs) / 1000}s"));
        foreach (string line in Events.TakeLast(5)) lines.Add("last: " + line);
        return lines;
    }

    // --- needs and matching ---------------------------------------------------------------------------------------------

    /// <param name="name">The bot's name.</param>
    /// <param name="goal">What it waits for.</param>
    /// <param name="since">Since when (world ms).</param>
    /// <param name="groupId">The id of the waiting group (the bots that wait for the same goal and team): stable from the first member's wait to the last one's end
    /// (<see cref="WaitingGroupIdOf"/>), the identity recruiting rolls on, unlike <see cref="BotGroup.Id"/> which only exists once the group has formed.</param>
    private sealed class Waiting(string name, PlayerbotGroupGoal goal, uint since, int groupId)
    {
        public string Name { get; } = name;
        public int GroupId { get; } = groupId;
        public Team Team { get; init; }
        public PlayerbotGroupGoal Goal { get; set; } = goal;
        public uint SinceMs { get; } = since;

        /// <summary>When a waiter with only derived reasons is next checked for a remaining seed (<see cref="KeepsDerivedWait"/>).</summary>
        public uint NextSeedCheckMs { get; set; } = unchecked(since + SeedRecheckMs);
        private PlayerbotGroupGoal? _descGoal;
        private string? _desc;

        public string Describe()
        {
            if (_desc is not null && _descGoal == Goal) return _desc;
            _descGoal = Goal;
            return _desc = string.Create(CultureInfo.InvariantCulture,
                $"group=waiting:{Goal.Kind.ToString().ToLowerInvariant()}:{Goal.Key.Value}:size{Goal.Size}");
        }
    }

    private void Scan()
    {
        uint now = Now;
        foreach (((Guid, (PlayerbotGroupGoalKind, uint)) key, uint until) in _setAside)
            if (unchecked((int)(until - now)) <= 0) _setAside.Remove(key);
        foreach (var (streakKey, streak) in _failStreaks)
            if (unchecked(now - streak.LastMs) >= FailStreakMemoryMs) _failStreaks.Remove(streakKey);
        foreach ((ObjectGuid key, uint until) in _declinedPlayers)
            if (unchecked((int)(until - now)) <= 0) _declinedPlayers.Remove(key);
        PruneFit(now);

        if (_waiting.Count > 0)
        {
            _waitingKeysScratch.Clear();
            foreach (Guid waitingKey in _waiting.Keys) _waitingKeysScratch.Add(waitingKey);
            for (int i = 0; i < _waitingKeysScratch.Count; i++)
            {
                Guid waiting = _waitingKeysScratch[i];
                if (!_bots.TryGetValue(waiting, out PlayerbotGroupBot bot) || !bot.Free || bot.Session.Player is not { } player || !player.IsInWorld)
                    Unhold(waiting, null);
            }
        }

        GroupManager? groupManager = _options.Groups.Enabled ? GroupManagerOrNull() : null;
        QuestNpcServices? quests = _options.Groups.Enabled ? services.GetService<QuestNpcFeature>()?.Services : null;
        CreatureContent? creatures = _options.Groups.Enabled ? services.GetService<CreatureWorldFeature>()?.Content : null;
        WorldMaps? maps = _world is { } world ? WorldMaps.Of(world) : null;

        for (int i = 0; i < _orderedBots.Count; i++)
        {
            PlayerbotGroupBot bot = _orderedBots[i];
            if (!bot.Free || _memberOf.ContainsKey(bot.BotId) || bot.Session.Player is not { IsInWorld: true } player) continue;
            // A mage serving a portal request, or a bot on a mage leg, may stand in the short-lived party of the portal (party-only portals):
            // the coordinator leaves it alone until the service ends (PlayerbotMageService leaves that party itself).
            if (PlayerbotMageService.IsServing(player) || PlayerbotMageService.IsActive(player)) continue;
            if (EvacuateIfStranded(bot, player)) continue;
            if (!_options.Groups.Enabled || !player.IsAlive || groupManager is null) continue;
            // Never a battleground raid: the match owns it (BattlegroundFeature.LeaveBattlegroundRaid takes the bot out when it leaves).
            if (groupManager.GetGroup(player.Guid) is { IsBattlegroundGroup: false } stale
                && stale.Members.All(m => m.Guid == player.Guid || !IsRealOnline(m.Guid))
                && stale.Members.All(m => !_botByPlayer.TryGetValue(m.Guid, out Guid other) || !_memberOf.ContainsKey(other)))
            {
                // A group of bots nobody leads any more (a world restart restores the server groups but not the coordinator's, or a
                // member logged out after the coordinator dissolved its group): a free bot (not driven by PlayerbotPartyAI waiting for
                // an offline master) leaves it at once, as a client would.
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

            // A scenario stay keeps the bot out of quest-group matching (it may not walk to a dungeon): any wait it had ends.
            if (bot.Brain.Stay is not null)
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

                if (!KeepsDerivedWait(bot.BotId, player, waiting, now))
                {
                    Note($"{player.Name} has no companion or forming group left for {waiting.Goal}: not waiting");
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

            if (FindNeed(bot, player, quests, creatures, maps) is { } goal)
            {
                _waiting[bot.BotId] = new Waiting(player.Name, goal, now, WaitingGroupIdFor(goal, player.Team)) { Team = player.Team };
                bot.Brain.GroupHeld.Add((goal.QuestId, goal.ObjectiveEntry));
                bot.Brain.Risk.ClearGroupSignal();
                Note($"{player.Name} needs a group: {goal}");
                if (goal.ObjectiveItem != 0)
                    logger.LogInformation("Playerbot instance item goal: dungeon map {Map} quest {Quest} item {Item} from creature {Dropper}: none of its {Count} droppers spawns outside an instance",
                        goal.MapId, goal.QuestId, goal.ObjectiveItem, goal.ObjectiveEntry, DroppersOf(goal.ObjectiveItem).Count);
            }
        }

        if (_options.Groups.Enabled) Match();
        if (_options.Groups.Enabled) MentorSafely();
        if (_options.Groups.Enabled) StartRuns();
        ScanWorldBossesSafely();
    }

    /// <summary>The objective of <paramref name="goal"/> is still unfinished for the bot (or, without a quest, still wanted).</summary>
    private bool StillNeeded(Player player, PlayerbotGroupGoal goal)
    {
        if (goal.QuestId == 0) return true;
        if (QuestState(player) is not { } state) return false;
        return state.Quests.Get(goal.QuestId) is { Status: QuestStatus.Incomplete } && !GoalDone(player, goal);
    }

    /// <summary>
    /// Members share the goal's quests with each other, as players do once grouped: a bot member holding a sharable quest of the goal
    /// (the goal's quest, or one asking for its creature or item) that another member lacks and could take sends CMSG_PUSHQUESTTOPARTY
    /// (<see cref="PlayerbotGroupAI.PushQuest"/>) once every such member stands within the share distance (vmangos QUEST_SHARE_DISTANCE,
    /// 14 yards); the receivers' party intake accepts (<c>PlayerbotPartyAI</c>). The server applies vmangos' rules (sharable flag, the
    /// sharer on the quest, the receiver able to take it, the log not full, one offer at a time). Each quest is pushed once per group.
    /// </summary>
    private void ShareQuests(BotGroup group)
    {
        if (_world is not { } world || group.Run is not null || group.Handoff is not null || services.GetService<QuestNpcFeature>()?.Services is not { } quests) return;
        var joined = new List<(Member Member, Player Player)>();
        foreach (Member member in group.Members.Where(m => m.Joined))
            if (world.FindOnlinePlayer(member.Guid) is { IsAlive: true } player) joined.Add((member, player));
        foreach ((Member member, Player sharer) in joined)
        {
            if (member.Real || member.AI is not { } ai || quests.StateOf(sharer) is not { Loaded: true } state) continue;
            foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses.OrderBy(s => s.Key))
            {
                if (status.Rewarded || group.Shared.Contains(questId) || quests.Quests.Get(questId) is not { } quest
                    || !ForGoal(quest, group.Goal) || !quests.CanShareQuest(sharer, questId)) continue;
                var lacking = joined.Where(other => !ReferenceEquals(other.Player, sharer)
                    && quests.StateOf(other.Player)?.Quests.GetStatus(questId) == QuestStatus.None && quests.CanTakeQuest(other.Player, questId) == true).ToArray();
                if (lacking.Length == 0) continue;
                if (lacking.Any(other => !ReferenceEquals(other.Player.Map, sharer.Map)
                        || Distance(other.Player, sharer) >= QuestNpcServices.QuestShareDistance - 1f)) continue;
                if (!ai.PushQuest(questId)) continue;
                group.Shared.Add(questId);
                Note($"{sharer.Name} shares quest {questId} with {string.Join(", ", lacking.Select(other => other.Player.Name))}");
            }
        }
    }

    /// <summary>Whether <paramref name="quest"/> is part of <paramref name="goal"/>: the goal's quest, or one asking for its creature or item.</summary>
    private static bool ForGoal(Quest quest, PlayerbotGroupGoal goal)
        => quest.Id == goal.QuestId || (goal.ObjectiveEntry != 0 && quest.ReqCreatureOrGOId.Contains((int)goal.ObjectiveEntry))
            || (goal.ObjectiveItem != 0 && quest.ReqItemId.Contains(goal.ObjectiveItem));

    /// <summary>The goal's objective is done for the bot: its quest item count (<see cref="PlayerbotGroupGoal.ObjectiveItem"/>), else its kills.</summary>
    internal bool GoalDone(Player player, PlayerbotGroupGoal goal)
        => goal.ObjectiveItem != 0 ? ItemObjectiveDone(player, goal.ObjectiveItem) : ObjectiveDone(player, goal.ObjectiveEntry);

    /// <summary>Whether every unfinished quest in the bot's log that asks for <paramref name="item"/> has its count in the bags.</summary>
    internal bool ItemObjectiveDone(Player player, uint item)
    {
        QuestNpcServices? quests = services.GetService<QuestNpcFeature>()?.Services;
        if (quests is null || QuestState(player) is not { } state || item == 0) return true;
        uint have = player.Inventory.GetItemCount(item);
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses)
        {
            if (status.Status != QuestStatus.Incomplete || quests.Quests.Get(questId) is not { } quest) continue;
            for (int index = 0; index < quest.ReqItemId.Count; index++)
                if (quest.ReqItemId[index] == item && have < quest.ReqItemCount[index]) return false;
        }

        return true;
    }

    private LootContent? _dropIndexOf;

    /// <summary>The creatures that drop <paramref name="item"/> (<see cref="PlayerbotGroupContent.DropIndex"/>, rebuilt when the loot tables are reloaded).</summary>
    internal IReadOnlyList<uint> DroppersOf(uint item)
    {
        if (services.GetService<GameObjects.GameObjectLootFeature>()?.LootContent is not { } loot) return [];

        _dropIndexOf = loot; // InstanceOnlyItem's cache follows the loot content
        return PlayerbotDropIndex.DroppersOf(loot, item); // the one cache every dropper lookup shares
    }

    private LootContent? _instanceOnlyLoot;
    private CreatureContent? _instanceOnlyCreatures;
    private MapRegistry? _instanceOnlyMaps;
    private readonly Dictionary<uint, bool> _instanceOnly = [];

    /// <summary>
    /// Whether every dropper of <paramref name="item"/> that spawns anywhere spawns on an instance map
    /// (<see cref="PlayerbotGroupContent.DroppedOnlyInInstances"/>); cached per item until the loot, creature or map content is replaced.
    /// </summary>
    internal bool InstanceOnlyItem(uint item, CreatureContent creatures, WorldMaps maps)
    {
        IReadOnlyList<uint> droppers = DroppersOf(item); // refreshes the drop index when the loot tables were reloaded
        if (!ReferenceEquals(_instanceOnlyLoot, _dropIndexOf) || !ReferenceEquals(_instanceOnlyCreatures, creatures) || !ReferenceEquals(_instanceOnlyMaps, maps.Registry))
        {
            _instanceOnly.Clear();
            _instanceOnlyLoot = _dropIndexOf;
            _instanceOnlyCreatures = creatures;
            _instanceOnlyMaps = maps.Registry;
        }

        if (_instanceOnly.TryGetValue(item, out bool known)) return known;
        return _instanceOnly[item] = PlayerbotGroupContent.DroppedOnlyInInstances(droppers, creatures, map => maps.Registry.Find(map) is { IsDungeon: true });
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
    private PlayerbotGroupGoal? FindNeed(PlayerbotGroupBot bot, Player player, QuestNpcServices? quests = null, CreatureContent? creatures = null, WorldMaps? maps = null)
    {
        quests ??= services.GetService<QuestNpcFeature>()?.Services;
        creatures ??= services.GetService<CreatureWorldFeature>()?.Content;
        if (_world is not { } world || quests is null || creatures is null || player.Map is null) return null;
        if (quests.StateOf(player) is not { Loaded: true } state) return null;
        maps ??= WorldMaps.Of(world);
        if (maps.Registry.Find(player.MapId) is { IsDungeon: true } or { IsBattleground: true }) return null;

        uint now = Now;
        if (state.Quests.Statuses.Count > 0)
        {
            _questStatusesScratch.Clear();
            foreach ((uint qId, QuestStatusData qStatus) in state.Quests.Statuses)
            {
                if (qStatus.Status == QuestStatus.Incomplete)
                    _questStatusesScratch.Add((qId, qStatus));
            }
            if (_questStatusesScratch.Count > 1)
                _questStatusesScratch.Sort(static (a, b) => a.QuestId.CompareTo(b.QuestId));

            for (int qIdx = 0; qIdx < _questStatusesScratch.Count; qIdx++)
            {
                (uint questId, QuestStatusData status) = _questStatusesScratch[qIdx];
                // A quest of a game event that is not running is not pursued (vmangos Quest::IsActive; PlayerbotQuestGoals).
                if (quests.Quests.Get(questId) is not { IsActive: true } quest) continue;
                if (bot.Brain.Suspensions.IsQuestSuspended(questId, now)) continue;
                int flagged = PlayerbotGroupContent.QuestGroupSize(quest.Template);
                for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
                {
                    int raw = quest.ReqCreatureOrGOId[index];
                    if (raw <= 0 || status.CreatureOrGOCount[index] >= quest.ReqCreatureOrGOCount[index]) continue;
                    uint entry = (uint)raw;
                    if (GoalFor(bot.BotId, player, maps, creatures, quest, entry, flagged) is { } goal && !IsSetAside(bot.BotId, goal.Key)) return goal;
                }

                // A quest item dropped only inside instances (a dungeon quest that asks for a boss's head and nothing else): the goal is the
                // creature that drops it, in that instance, like a creature objective. An item with even one dropper outside the instances
                // (on a continent) is the brain's solo work, as before: it is no goal at all (InstanceOnlyItem).
                for (int index = 0; index < quest.ReqItemId.Count; index++)
                {
                    uint item = quest.ReqItemId[index];
                    if (item == 0 || quest.ReqItemCount[index] == 0 || player.Inventory.GetItemCount(item) >= quest.ReqItemCount[index]) continue;
                    if (!InstanceOnlyItem(item, creatures, maps)) continue;
                    // Every dropper's dungeon is a candidate; the dungeon fit takes the best of them (InstanceGoalFor).
                    if (InstanceGoalFor(bot.BotId, player, maps, creatures, quest, DroppersOf(item), flagged) is { } goal && !IsSetAside(bot.BotId, goal.Key))
                        return goal with { ObjectiveItem = item };
                }
            }
        }

        // The risk estimate passed over a quest objective as too strong alone but feasible for a few.
        if (bot.Brain.Risk.GroupSignal is { } signal && signal.MapId == player.MapId && unchecked(now - signal.AtMs) < 60_000)
        {
            uint questId = signal.QuestId != 0 ? signal.QuestId : QuestFor(player, signal.Entry, quests);
            var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, questId, signal.Entry, player.MapId, player.MapId,
                signal.At, 0, signal.At, Math.Clamp(signal.Size, 2, PlayerbotRecruitment.SignalLimit(_options.Groups)), player.Level, signal.Source);
            if (!IsSetAside(bot.BotId, goal.Key)) return goal;
        }

        return null;
    }

    /// <summary>The quest in the bot's log that asks for <paramref name="entry"/> (0: none).</summary>
    private uint QuestFor(Player player, uint entry, QuestNpcServices? quests = null)
    {
        quests ??= services.GetService<QuestNpcFeature>()?.Services;
        if (quests is null || quests.StateOf(player) is not { Loaded: true } state) return 0;
        uint bestQuestId = uint.MaxValue;
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses)
        {
            if (questId < bestQuestId && status.Status == QuestStatus.Incomplete && quests.Quests.Get(questId) is { } quest && quest.ReqCreatureOrGOId.Contains((int)entry))
                bestQuestId = questId;
        }
        return bestQuestId == uint.MaxValue ? 0 : bestQuestId;
    }

    private (uint MapId, MapTemplate Instance)[] GetDungeonMapsWithSpawns(CreatureContent creatures, MapRegistry registry)
    {
        if (ReferenceEquals(_dungeonMapsCreatures, creatures) && ReferenceEquals(_dungeonMapsRegistry, registry))
            return _dungeonMapsWithSpawns;
        var list = new List<(uint MapId, MapTemplate Instance)>();
        foreach (uint mapId in creatures.MapsWithSpawns)
        {
            if (registry.Find(mapId) is { IsDungeon: true } instance)
                list.Add((mapId, instance));
        }
        list.Sort(static (a, b) => a.MapId.CompareTo(b.MapId));
        _dungeonMapsWithSpawns = [.. list];
        _dungeonMapsCreatures = creatures;
        _dungeonMapsRegistry = registry;
        return _dungeonMapsWithSpawns;
    }

    private static CreatureSpawn NearestSpawn(IReadOnlyList<CreatureSpawn> spawns, float x, float y)
    {
        CreatureSpawn best = spawns[0];
        Vector2 target = new(x, y);
        float bestDistSq = Vector2.DistanceSquared(new(best.X, best.Y), target);
        for (int i = 1; i < spawns.Count; i++)
        {
            CreatureSpawn candidate = spawns[i];
            float d2 = Vector2.DistanceSquared(new(candidate.X, candidate.Y), target);
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// The group goal for one unfinished objective: inside an instance when the creature spawns only on dungeon maps (reached
    /// through an entrance trigger on the bot's continent), on the bot's map when the quest is flagged for a group. Null when one
    /// bot is enough or the place cannot be reached.
    /// </summary>
    private PlayerbotGroupGoal? GoalFor(Guid botId, Player player, WorldMaps maps, CreatureContent creatures, Quest quest, uint entry, int flagged)
    {
        IReadOnlyList<CreatureSpawn> here = creatures.GetSpawns(player.MapId, entry);
        if (here.Count > 0)
        {
            if (flagged < 2) return null;
            CreatureSpawn nearest = NearestSpawn(here, player.X, player.Y);
            var at = new Vector3(nearest.X, nearest.Y, nearest.Z);
            bool raid = quest.Template.Type == PlayerbotGroupContent.QuestTypeRaid;
            if (raid && !_options.Groups.RaidsEnabled) return null;
            return new PlayerbotGroupGoal(raid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Quest, quest.Id, entry,
                player.MapId, player.MapId, at, 0, at, flagged, quest.MinLevel, "quest");
        }

        _singleEntry[0] = entry;
        return InstanceGoalFor(botId, player, maps, creatures, quest, _singleEntry, flagged);
    }

    /// <summary>The nearest area trigger on <paramref name="fromMap"/> whose teleport leads into <paramref name="instanceMap"/>.</summary>
    internal static AreaTriggerTemplate? EntranceTo(WorldMaps maps, uint fromMap, uint instanceMap, Vector3 near)
    {
        IReadOnlyList<AreaTriggerTemplate> entrances = PlayerbotAreaTriggers.EntrancesTo(maps, fromMap, instanceMap);
        AreaTriggerTemplate? best = null;
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < entrances.Count; i++)
        {
            AreaTriggerTemplate t = entrances[i];
            float d2 = Vector3.DistanceSquared(new Vector3(t.X, t.Y, t.Z), near);
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                best = t;
            }
        }

        return best;
    }

    /// <summary>
    /// The exit a retrying run's leader has to leave through: the run is on its <see cref="PlayerbotRunStep.Retry"/> step (a wipe's regroup
    /// outside the instance) and <paramref name="leader"/> is standing inside the goal's dungeon, revived there. Null otherwise.
    /// </summary>
    internal static AreaTriggerTemplate? RetryExitOf(WorldRuntime world, BotGroup group, Player leader)
        => group.Run is { Step: PlayerbotRunStep.Retry } && leader.MapId == group.Goal.MapId
            && WorldMaps.Of(world).Registry.Find(leader.MapId) is { IsDungeon: true } ? ExitOf(world, leader.MapId) : null;

    /// <summary>The way out of an instance: a trigger on <paramref name="mapId"/> whose teleport leads off it, or null.</summary>
    internal static AreaTriggerTemplate? ExitOf(WorldRuntime world, uint mapId)
    {
        WorldMaps maps = WorldMaps.Of(world);
        return PlayerbotAreaTriggers.OnMap(maps, mapId)
            .FirstOrDefault(t => maps.FindAreaTriggerTeleport(t.Id) is { } teleport && teleport.TargetMap != mapId);
    }

    /// <summary>
    /// Group slots quest, instance-item and risk groups leave to dungeon runs. Without them <see cref="Match"/> (which runs before
    /// <see cref="StartRuns"/> in every scan) refilled every slot the moment one freed, and a run, which starts on a chance roll, never found
    /// one: in w29 four quest groups held the slots 97.6% of the time and no run ever formed. A fixed reserve is used rather than running
    /// <c>StartRuns</c> first: the roll then can only claim a slot when one is free. Runs are capped only by their own per-kind caps, so when those add up
    /// to <c>MaxGroups</c> or more, runs can still hold every slot and quest groups wait for a run to end (with the defaults one quest slot
    /// always stays free).
    /// </summary>
    internal const int ReservedRunSlots = 1;

    /// <summary>The most groups that are not runs at once: <c>MaxGroups</c> less <see cref="ReservedRunSlots"/> while runs can start and there is room to keep one.</summary>
    internal int QuestGroupLimit
    {
        get
        {
            PlayerbotRunOptions runs = _options.Groups.DungeonRuns;
            bool runsPossible = runs.Enabled && runs.StartChancePerMinute > 0d && (runs.MaxConcurrentRuns > 0 || runs.Raids.Enabled && runs.Raids.MaxConcurrentRaids > 0);
            return runsPossible && _options.Groups.MaxGroups > ReservedRunSlots ? _options.Groups.MaxGroups - ReservedRunSlots : _options.Groups.MaxGroups;
        }
    }

    private bool QuestGroupSlotFree() => OwnGroupCount < _options.Groups.MaxGroups && _groups.Count(g => g.Run is null && g.Handoff is null && g.Siege is null) < QuestGroupLimit;

    /// <summary>The groups the bots formed themselves: a handed-over group (a player's own, <see cref="BotGroup.Handoff"/>) takes no slot of <c>MaxGroups</c>.</summary>
    private int OwnGroupCount => _groups.Count(g => g.Handoff is null && g.Siege is null);

    private void Match()
    {
        PruneRecruiting(Now);
        PruneVolunteers(Now);
        // A group with a real player (a volunteer) takes no slot from the cap, so a full cap only stops the bots-only plans.
        if (!QuestGroupSlotFree())
        {
            NoteCapBlocked();
            if (_volunteers.Count == 0) return;
        }

        if (GroupManagerOrNull() is not { } groupManager) return;
        foreach (var wanting in _waiting
                     .Select(w => (BotId: w.Key, Waiting: w.Value))
                     .GroupBy(w => (w.Waiting.Goal.Key, Team: _bots[w.BotId].Session.Player?.Team ?? 0))
                     .OrderBy(g => g.Min(w => w.Waiting.SinceMs)).ToArray())
        {
            bool slotFree = QuestGroupSlotFree();
            if (!slotFree)
            {
                NoteCapBlocked();
                if (_volunteers.Count == 0) return;
            }

            if (!slotFree && !HasLiveVolunteer(wanting.Key.Key)) continue; // a full cap lets only a goal with a volunteer through
            PlayerbotGroupGoal goal = wanting.OrderBy(w => w.Waiting.SinceMs).First().Waiting.Goal;
            if (goal.InInstance)
            {
                // Bots waiting only on derived reasons (a companion, a forming group) never anchor a group; the goal is the longest-waiting anchor's.
                var anchor = wanting.Where(w => w.Waiting.Goal.Anchors).OrderBy(w => w.Waiting.SinceMs).FirstOrDefault();
                if (anchor.Waiting is null) continue;
                goal = anchor.Waiting.Goal;
            }

            int size = wanting.Max(w => w.Waiting.Goal.Size);
            List<PlayerbotGroupCandidate> candidates = BuildCandidates(wanting, goal);

            // A player who asked for the group (Volunteers.cs) comes first, ahead of surplus bots; it forms even with the cap full.
            ObjectGuid? volunteer = null;
            PlayerbotGroupPlan? plan = candidates.Count >= 1 ? MatchWithVolunteer(candidates, size, goal, groupManager, out volunteer) : null;
            if (plan is null && slotFree) plan = PlayerbotGroupContent.Match(candidates, size, _options.Groups, goal.InInstance);
            List<Guid> recruited = [];
            if (plan is null && slotFree && candidates.Count >= 1) plan = RecruitFor(candidates, size, goal, wanting.Key.Team, out recruited); // free bots nearby (Recruit.cs)
            if (plan is null && slotFree && _options.Groups.InvitePlayers && candidates.Count >= 1)
                plan = MatchWithPlayer(candidates, size, goal, groupManager);
            if (plan is not null && goal.InInstance && !PlanAnchored(plan))
            {
                plan = null;
                recruited.Clear();
            }

            if (plan is null) continue;
            Form(goal with { Size = size }, plan);
            if (volunteer is { } formedVolunteer && plan.Members.Any(m => m.Bot.Guid == formedVolunteer.Value)) _volunteers.Remove(formedVolunteer);
            if (recruited.Count > 0) NoteRecruited(plan, recruited);
        }
    }

    private PlayerbotGroupCandidate Candidate(Guid botId, Player player, uint since)
    {
        PlayerbotRole talent = _bots.TryGetValue(botId, out PlayerbotGroupBot bot) ? TalentRole(bot.Session, player) : PlayerbotRole.MeleeDps;
        return new PlayerbotGroupCandidate(botId, player.Guid.Value, player.Name, player.Class, player.Level, (uint)player.Team, player.MapId,
            new Vector3(player.X, player.Y, player.Z), talent, Gear(player), since);
    }

    /// <summary>The talent role (vmangos AutoAssignRole) from the spell names the bot knows; reads the book, drains no packet.</summary>
    private PlayerbotRole TalentRole(WorldSession session, Player player)
    {
        if (session.Services.GetService<Spells.SpellFeature>() is not { } feature) return PlayerbotRoles.Assign(player.Class, static _ => false);
        HashSet<string> names = _spellNamesScratch;
        names.Clear();
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

    private BotGroup Form(PlayerbotGroupGoal goal, PlayerbotGroupPlan plan, PlayerbotRunMachine? run = null)
    {
        var group = new BotGroup(_nextId++, goal, plan.Leader.BotId, new ObjectGuid(plan.Leader.Guid), Now) { Run = run };
        foreach ((PlayerbotGroupCandidate bot, PlayerbotGroupRole role, byte subGroup) in plan.Members)
        {
            bool real = bot.BotId == Guid.Empty;
            var member = new Member(bot.BotId, new ObjectGuid(bot.Guid), bot.Name, role, bot.TalentRole, subGroup, real)
            {
                Joined = bot.BotId == plan.Leader.BotId,
                Qualifying = bot.Qualifying,
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

        group.HadQuest = run is null && (goal.QuestId != 0 || group.Members.Any(m => !m.Real && _world?.FindOnlinePlayer(m.Guid) is { } p
            && QuestFor(p, goal.ObjectiveEntry) != 0));
        IPathfinder? pathfinder = _world is not null ? WorldCollision.Of(_world).Pathfinder : null;
        group.Approach = SnapToMesh(pathfinder, goal.MeetingMap, plan.Leader.Position, ApproachPoint(goal, plan.Leader.Position));
        _groups.Add(group);
        Totals = (Totals.Formed + 1, Totals.Completed, Totals.Failed);
        Note($"group {group.Id} formed for {goal}: leader {plan.Leader.Name}, "
             + string.Join(", ", group.Members.Select(m => $"{m.Name} {m.Role.ToString().ToLowerInvariant()}")));
        logger.LogInformation("Playerbot group {Group} formed for {Goal}, leader {Leader}, {Count} members", group.Id, goal, plan.Leader.Name, group.Members.Count);
        return group;
    }

    // --- the group's life -------------------------------------------------------------------------------------------

    private void Advance(BotGroup group)
    {
        if (_world is not { } world) return;
        uint now = Now;
        // A bot that stopped, became scripted or joined a real player's group leaves this one: the feature calls Forget for each of those
        // (a controller attaching, a leash, a stop). Here only a bot no longer running (out of the snapshot, or without a player) is gone.
        // Neither Free nor IsInWorld is asked: the snapshot of a member of an adopted party (Handoff) is taken with Free=false
        // while the bot is still PartyDriven on the tick it is adopted (TakeLeaderChanges adopts and Advance runs in the same Update; the
        // bot's next think then clears PartyDriven), and a member between maps (the instance portal, the walk out) is briefly out of the
        // world; both are still members.
        _membersScratch.Clear();
        for (int i = 0; i < group.Members.Count; i++)
        {
            Member m = group.Members[i];
            if (!m.Real) _membersScratch.Add(m);
        }
        for (int i = 0; i < _membersScratch.Count; i++)
        {
            Member member = _membersScratch[i];
            if (!_bots.TryGetValue(member.BotId, out PlayerbotGroupBot bot) || bot.Session.Player is null)
                DropMember(group, member.BotId, "gone");
        }
        if (!_groups.Contains(group)) return;
        if (group.LeaderPlayer(world) is not { } leader)
        {
            if (group.Siege is not null && AdoptServerLeader(group)) return;
            if (!_shuttingDown)
                PlayerbotGroupAnomalies.ReportNamed(group.Find(group.LeaderBotId)?.Name ?? "unknown", group.LeaderGuid, PlayerbotGroupAnomalies.LeaderGone,
                    string.Create(CultureInfo.InvariantCulture, $"group {group.Id} ({group.Goal}) lost its leader (not online) in state {group.State}; the group ends"),
                    AnomalySeverity.Info, _world?.Identity);
            Finish(group, success: false, "leader-gone");
            return;
        }

        GroupManager? groupManager = GroupManagerOrNull();
        if (WatchHandoff(group, leader, groupManager)) return;
        if (group.State != PlayerbotGroupState.Disbanding && group.State != PlayerbotGroupState.Forming && groupManager is not null)
        {
            // A member that left the server group (a real player leaving, a bot kicked) is no longer one.
            _membersScratch.Clear();
            for (int i = 0; i < group.Members.Count; i++)
            {
                Member m = group.Members[i];
                if (m.Joined && m.Guid != leader.Guid) _membersScratch.Add(m);
            }
            for (int i = 0; i < _membersScratch.Count; i++)
            {
                Member member = _membersScratch[i];
                if (!groupManager.AreInSameGroup(leader.Guid, member.Guid))
                {
                    // Leaving a run before its end, unkicked and with the leader still grouped (not a disband), is abandoning it.
                    bool kicked = _kicked.Remove(member.Guid);
                    if (!kicked && group.Run is { Finished: false } && !group.Succeeded && group.State != PlayerbotGroupState.Leaving && groupManager.GetGroup(leader.Guid) is not null)
                        NoteAgainst(group, member.Guid, BotSocial.PlayerbotSocialEvent.AbandonedRun);
                    DropMember(group, member.BotId, "left", member.Guid);
                }
            }
            if (!_groups.Contains(group)) return;
            int botMembers = 0, present = 0;
            bool realOffline = false;
            for (int i = 0; i < group.Members.Count; i++)
            {
                Member m = group.Members[i];
                if (!m.Real) { botMembers++; present++; }
                else if (!m.Joined) present++;                                   // still being invited: the Forming rules own it
                else if (world.FindOnlinePlayer(m.Guid) is not null || group.Succeeded || group.State == PlayerbotGroupState.Leaving) present++; // a finished run is not failed by a logout while the bots walk out
                else realOffline = true;                                          // the server group keeps a logged-out player; it does not count
            }
            if ((botMembers < 1 || present < 2) && group.Goal.Size >= 2 && !group.Solo) // a real player counts toward the two only while online
            {
                Finish(group, success: false, realOffline && botMembers >= 1 ? "player-gone" : "too-few");
                return;
            }
        }

        // A siege raid has no wipe, goal timeout or stall rules of the quest groups: the commander (PlayerbotGroupCoordinator.Siege.cs) runs it.
        if (group.Siege is not null && group.State == PlayerbotGroupState.Engaging)
        {
            AdvanceSiegeRaid(group, leader, now);
            return;
        }

        SyncRun(group);
        if (MentorTimedOut(group, now))
        {
            Finish(group, success: false, "mentor-timeout", setAside: false);
            return;
        }

        if (group.State is not (PlayerbotGroupState.Forming or PlayerbotGroupState.Disbanding or PlayerbotGroupState.Leaving)
            && unchecked(now - group.FormedMs) > GoalTimeoutOf(group))
        {
            if (group.Run is { } timedOut) timedOut.Apply(PlayerbotRunSignal.Timeout("run"));
            Finish(group, success: false, group.Run is null ? "timeout" : "run-timeout");
            return;
        }

        if (group.RunFault is { } fault)
        {
            if (group.Run is { Finished: false } faulty) faulty.Apply(PlayerbotRunSignal.NoPath(fault));
            Finish(group, success: false, fault);
            return;
        }

        if (group.Members.Any(m => m.Joined && world.FindOnlinePlayer(m.Guid) is { IsAlive: true } p && p.Combat.IsInCombat))
            group.LastCombatMs = Math.Max(1u, now);

        if (group.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging && !group.InFight(now))
            ShareQuests(group);
        if (WatchStall(group, leader, now)) return;
        uint stepLimit = StepLimitOf(group);
        ObserveOutcomes(group, leader, now, stepLimit);

        switch (group.State)
        {
            case PlayerbotGroupState.Forming:
                AdvanceForming(group, leader, groupManager);
                break;
            case PlayerbotGroupState.Gathering:
                if (Gathered(group, leader) || (group.WorldBoss is null ? CatchUpGathering(group, leader, now) : WorldBossGathered(group, leader, now))) Enter(group, PlayerbotGroupState.Travelling);
                else if (group.StepTimedOut(now, stepLimit))
                {
                    ReportTimeout(group, leader, "gather-timeout");
                    Finish(group, success: false, "gather-timeout");
                }
                break;
            case PlayerbotGroupState.Travelling:
                if (CheckWipe(group, leader)) break;
                if (group.WorldBoss is not null && AdvanceWorldBoss(group, leader, now)) break;
                if (leader.MapId == group.Goal.MeetingMap && (group.ApproachReached || Arrived(leader, group.Approach, 5f) || WithinApproachRing(leader, group)) || leader.MapId == group.Goal.MapId && group.Goal.InInstance)
                {
                    // Every member that qualified has left: nobody in the group is in the band or has a reason to go in.
                    if (group.Goal.InInstance && !group.Members.Any(m => m.Qualifying))
                    {
                        Finish(group, success: false, "fit-lost");
                        break;
                    }

                    group.Run?.Apply(PlayerbotRunSignal.Arrived);
                    Enter(group, group.Goal.InInstance ? PlayerbotGroupState.Entering : PlayerbotGroupState.Engaging);
                }
                else if (group.StepTimedOut(now, stepLimit))
                {
                    ReportTimeout(group, leader, "travel-timeout");
                    Finish(group, success: false, "travel-timeout");
                }
                break;
            case PlayerbotGroupState.Entering:
                if (CheckWipe(group, leader)) break;
                if (group.Members.Where(m => m.Joined).All(m => world.FindOnlinePlayer(m.Guid) is not { } p || p.MapId == group.Goal.MapId || m.Real))
                {
                    group.Run?.Apply(PlayerbotRunSignal.AllInside);
                    Enter(group, PlayerbotGroupState.Engaging);
                }
                else if (group.StepTimedOut(now, stepLimit)) Finish(group, success: false, group.Run is null ? "enter-timeout" : "enter-refused-or-timeout");
                break;
            case PlayerbotGroupState.Engaging:
                if (group.Run is { } run)
                {
                    AdvanceRun(group, leader, run, now);
                    break;
                }

                if (group.Handoff?.Sweep is { } sweep)
                {
                    AdvanceSweep(group, leader, sweep, now);
                    break;
                }

                if (group.WorldBoss is not null)
                {
                    if (group.WorldBoss.KilledAtMs == 0 && CheckWipe(group, leader)) break;
                    AdvanceWorldBoss(group, leader, now);
                    break;
                }

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
                if (unchecked(now - group.StateSinceMs) >= WipeSettleMs && group.Members.Where(m => m.Joined).All(m => world.FindOnlinePlayer(m.Guid) is not { } p || !p.IsAlive || !p.Combat.IsInCombat))
                {
                    if (group.Run is { } wiped)
                    {
                        group.Failures = wiped.Wipes;
                        Note($"group {group.Id} wiped ({wiped.Wipes}/{wiped.MaxWipes})");
                        if (wiped.Step == PlayerbotRunStep.GiveUp) Finish(group, success: false, wiped.Reason ?? "wiped");
                        else Enter(group, PlayerbotGroupState.Regrouping);
                        break;
                    }

                    if (group.WorldBoss is { } raid)
                    {
                        group.Failures = ++raid.Wipes;
                        Note($"group {group.Id} wiped ({raid.Wipes}/{Wb.MaxWipes})");
                        if (raid.Wipes >= Wb.MaxWipes)
                        {
                            NoteWorldBossWipedOut(group, raid);
                            Finish(group, success: false, "wiped");
                        }
                        else
                        {
                            ResetWorldBossPull(raid);
                            Enter(group, PlayerbotGroupState.Regrouping);
                        }

                        break;
                    }

                    group.Failures++;
                    Note($"group {group.Id} wiped ({group.Failures}/{MaxFailures})");
                    if (group.Failures >= MaxFailures) Finish(group, success: false, "wiped");
                    else Enter(group, PlayerbotGroupState.Regrouping);
                }

                break;
            case PlayerbotGroupState.Regrouping:
                if (group.Members.Where(m => m.Joined && !m.Real).All(m => world.FindOnlinePlayer(m.Guid) is { IsAlive: true } p && !p.Combat.IsInCombat)
                    && !group.AnyResting(world))
                {
                    group.Run?.Apply(PlayerbotRunSignal.Regrouped);
                    Enter(group, PlayerbotGroupState.Gathering);
                }
                else if (group.StepTimedOut(now, stepLimit)) Finish(group, success: false, "regroup-timeout");
                break;
            case PlayerbotGroupState.Leaving:
                if (group.Members.Where(m => m.Joined && !m.Real).All(m => world.FindOnlinePlayer(m.Guid) is not { } p
                        || WorldMaps.Of(world).Registry.Find(p.MapId) is not { IsDungeon: true }))
                {
                    group.Run?.Apply(PlayerbotRunSignal.Left);
                    Enter(group, PlayerbotGroupState.Disbanding);
                }
                else if (group.StepTimedOut(now, stepLimit))
                {
                    if (group.BringOuts < MaxBringOuts) BringOut(group);
                    else
                    {
                        group.Run?.Apply(PlayerbotRunSignal.Left);
                        Enter(group, PlayerbotGroupState.Disbanding);
                    }
                }
                break;
            case PlayerbotGroupState.Disbanding:
                Disband(group);
                break;
        }
    }

    /// <summary>
    /// The outcome checks of <see cref="PlayerbotGroupAnomalies"/>, once per group per tick: the state and run clocks (a few compares) and,
    /// once a second, the members. Reports only; the group's own bounds and give-ups are untouched.
    /// </summary>
    internal void ObserveOutcomes(BotGroup group, Player leader, uint now, uint stepLimit)
    {
        uint travelLimit = (uint)Math.Max(1, _options.Groups.DungeonRuns.TravelTimeoutMinutes) * 60_000u;
        PlayerbotGroupFinding found = group.Watch.Observe(group.State, group.StateEnteredMs, stepLimit, GoalTimeoutOf(group), travelLimit, group.Run, now);
        if (found != PlayerbotGroupFinding.None) ReportFindings(group, leader, found, now, stepLimit, travelLimit);
        if (group.Watch.MemberPassDue(now)) WatchMembers(group, leader, now);
    }

    private void ReportFindings(BotGroup group, Player leader, PlayerbotGroupFinding found, uint now, uint stepLimit, uint travelLimit)
    {
        string step = group.Find(group.LeaderBotId)?.AI?.Step ?? "none";
        if ((found & PlayerbotGroupFinding.StateStuck) != 0)
        {
            uint budget = PlayerbotGroupWatch.StateBudgetMs(group.State, stepLimit, GoalTimeoutOf(group));
            PlayerbotGroupAnomalies.Report(leader, PlayerbotGroupAnomalies.StateStuck, string.Create(CultureInfo.InvariantCulture,
                $"group {group.Id} ({group.Goal}) has been in state {group.State} for {PlayerbotGroupWatch.Seconds(group.StateEnteredMs, now)}s, past its {budget / 1000}s budget (leader step {step}, {group.Members.Count} members)"));
        }

        if (group.Run is not { } run) return;
        if ((found & PlayerbotGroupFinding.RunStepStuck) != 0)
            PlayerbotGroupAnomalies.Report(leader, PlayerbotGroupAnomalies.RunStepStuck, string.Create(CultureInfo.InvariantCulture,
                $"run {run.Plan.Label} has been in step {run.Step} for {group.Watch.RunStepSeconds(now)}s, past its {PlayerbotGroupWatch.RunStepBudgetMs(run.Step, travelLimit, StepTimeoutMs) / 1000}s budget (group state {group.State}, leader step {step}, {run.Killed}/{run.BossCount} bosses)"));
        if ((found & PlayerbotGroupFinding.RouteStepIncomplete) != 0)
            PlayerbotGroupAnomalies.Report(leader, PlayerbotGroupAnomalies.RouteStepIncomplete, string.Create(CultureInfo.InvariantCulture,
                $"run {run.Plan.Label} has killed no boss for {group.Watch.RouteSeconds(now)}s on its way to {run.CurrentBoss?.Name ?? "its last boss"} ({run.Killed}/{run.BossCount} bosses, step {run.Step}, group state {group.State}, {run.PullsLeft} packs left, leader step {step})"));
    }

    /// <summary>Members that stay dead, or stay behind while the group travels or fights (once a second per group).</summary>
    internal void WatchMembers(BotGroup group, Player leader, uint now)
    {
        if (_world is not { } world) return;
        foreach (Member member in group.Members)
        {
            if (member.Real || !member.Joined) continue;
            if (world.FindOnlinePlayer(member.Guid) is not { } player)
            {
                member.Watch.Reset();
                continue;
            }

            if (member.Watch.ObserveDead(!player.IsAlive, now, out long deadMs))
                PlayerbotGroupAnomalies.Report(player, PlayerbotGroupAnomalies.MemberUnrecovered, string.Create(CultureInfo.InvariantCulture,
                    $"{member.Role.ToString().ToLowerInvariant()} of group {group.Id} has been dead {deadMs / 1000}s while the group is {group.State} (ghost {(player.Flags & PlayerFlags.Ghost) != 0}, step {member.AI?.Step ?? "none"})"));
            if (member.Guid == leader.Guid) continue;
            bool mapsKnown = player.Map is not null && leader.Map is not null;
            bool sameMap = mapsKnown && ReferenceEquals(player.Map, leader.Map);
            bool behind = PlayerbotMemberWatch.IsLeftBehind(group.State, player.IsAlive, mapsKnown, sameMap, sameMap ? Distance(player, leader) : 0f);
            if (member.Watch.ObserveBehind(behind, now, out long behindMs))
                PlayerbotGroupAnomalies.Report(player, PlayerbotGroupAnomalies.MemberLeftBehind, string.Create(CultureInfo.InvariantCulture,
                    $"{member.Role.ToString().ToLowerInvariant()} of group {group.Id} has been {(sameMap ? $"{Distance(player, leader):F0} yd from" : $"on map {player.MapId} away from")} leader {leader.Name} (map {leader.MapId}) for {behindMs / 1000}s while the group is {group.State} (step {member.AI?.Step ?? "none"})"));
        }
    }

    /// <summary>The bots whose invitation went unanswered, one report each (the group is about to be given up).</summary>
    private void ReportUnansweredInvites(BotGroup group, uint now)
    {
        foreach (Member member in group.Members)
        {
            if (member.Real || member.Joined || !(member.Refused || member.Invited && unchecked(now - member.InvitedMs) > InviteTimeoutMs)) continue;
            Player? player = _world?.FindOnlinePlayer(member.Guid);
            string reason = player is null ? "the bot is not online"
                : member.Refused ? "no invitation was pending after CMSG_GROUP_INVITE and the bot is not in the leader's group"
                : string.Create(CultureInfo.InvariantCulture, $"invited {unchecked(now - member.InvitedMs) / 1000}s ago, still no answer");
            string detail = string.Create(CultureInfo.InvariantCulture, $"{member.Name}'s invitation to group {group.Id} ({group.Goal}) went unanswered: {reason}");
            if (player is not null) PlayerbotGroupAnomalies.Report(player, PlayerbotGroupAnomalies.InviteUnanswered, detail);
            else PlayerbotGroupAnomalies.ReportNamed(member.Name, member.Guid, PlayerbotGroupAnomalies.InviteUnanswered, detail, origin: _world?.Identity);
        }
    }

    private void AdvanceForming(BotGroup group, Player leader, GroupManager? groupManager)
    {
        if (groupManager is null || _world is not { } world) return;
        uint now = Now;
        Group? formed = groupManager.GetGroup(leader.Guid);
        foreach (Member member in group.Members.Where(m => !m.Joined))
            if (formed?.IsMember(member.Guid) == true)
            {
                member.Joined = true;
                NoteJoined(group, member);
            }

        bool needsRaid = group.Goal.Size > PlayerbotGroupContent.PartySize || group.Siege is not null;
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
            if (world.FindOnlinePlayer(member.Guid) is null)
            {
                if (unchecked(now - group.StateSinceMs) > InviteTimeoutMs)
                    member.Refused = true;
                continue;
            }
            member.Invited = true;
            member.InvitedMs = now;
            LeaderAct(group, leader, WorldOpcode.CmsgGroupInvite, Cstring(member.Name));
            if (groupManager.GetInvite(member.Guid) is null && !groupManager.AreInSameGroup(leader.Guid, member.Guid))
                member.Refused = true;
        }

        foreach (Member member in group.Members.Where(m => m.Real && m.Invited && !m.Joined).ToArray())
            if (member.Refused || unchecked(now - member.InvitedMs) > InviteTimeoutMs || groupManager.GetInvite(member.Guid) is null && !(formed?.IsMember(member.Guid) ?? false))
            {
                _declinedPlayers[member.Guid] = unchecked(now + SetAsideMs);
                group.Members.Remove(member);
                Note($"group {group.Id}: {member.Name} did not join");
            }

        if (group.Members.Any(m => !m.Joined && (m.Refused || m.Invited && unchecked(now - m.InvitedMs) > InviteTimeoutMs))
            && !(group.WorldBoss is not null && DropUnansweredInvitees(group, now)))
        {
            ReportUnansweredInvites(group, now);
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
            Enter(group, group.Siege is not null ? PlayerbotGroupState.Engaging : PlayerbotGroupState.Gathering);
        }
    }

    /// <summary>
    /// Where the group stops before the content: <see cref="QuestApproachYards"/> short of the objective's spawn (outside its aggro) or
    /// <see cref="ApproachYards"/> short of the entrance trigger (outside its volume), on the side the leader comes from.
    /// </summary>
    internal static Vector3 ApproachPoint(PlayerbotGroupGoal goal, Vector3 from)
        => ApproachPoint(goal, from, goal.InInstance ? ApproachYards : QuestApproachYards);

    /// <summary>The approach point <paramref name="yards"/> short of the goal's meeting point on the side <paramref name="from"/> lies (<paramref name="from"/> itself when it is nearer).</summary>
    internal static Vector3 ApproachPoint(PlayerbotGroupGoal goal, Vector3 from, float yards)
    {
        Vector3 flat = new(from.X - goal.Meeting.X, from.Y - goal.Meeting.Y, 0);
        if (flat.Length() < yards) return from;
        return goal.Meeting + (Vector3.Normalize(flat) * yards);
    }

    /// <summary>
    /// The approach point on the navigation mesh: when a ray along the mesh from <paramref name="from"/> to <paramref name="point"/> hits a
    /// boundary before <paramref name="point"/>, the last point of the mesh on that ray (<see cref="MeshRaycastResult.Point"/>); otherwise
    /// <paramref name="point"/> itself.
    /// </summary>
    internal static Vector3 SnapToMesh(IPathfinder? pathfinder, uint mapId, Vector3 from, Vector3 point)
        => pathfinder is IMeshPointQuery mesh && mesh.Raycast(mapId, from, point) is { Available: true, Hit: true } hit ? hit.Point : point;

    /// <summary>How far short of an instance entrance the group gathers before it goes in (<see cref="ApproachPoint"/>).</summary>
    internal const float ApproachYards = 15f;

    /// <summary>How far short of an open-world objective's spawn the group gathers (beyond a creature's usual aggro radius).</summary>
    internal const float QuestApproachYards = 30f;

    private bool Gathered(BotGroup group, Player leader)
        => _world is { } world && RetryExitOf(world, group, leader) is null // a retry regroups outside: a leader still inside has not gathered anyone
            && group.Members.Where(m => m.Joined && !m.Real && m.Guid != leader.Guid).All(m => world.FindOnlinePlayer(m.Guid) is not { } p
            || !p.IsAlive || ReferenceEquals(p.Map, leader.Map) && Distance(p, leader) <= GatherYards);

    /// <summary>
    /// Past <see cref="GatherCatchUpMs"/> in <see cref="PlayerbotGroupState.Gathering"/>: bring each missing member to the leader with the
    /// follow catch-up teleport (<see cref="PlayerbotGroupAI.CatchUpNow"/>), and when none is still pending let the gathering go on.
    /// </summary>
    private bool CatchUpGathering(BotGroup group, Player leader, uint now)
    {
        if (_world is not { } world || unchecked(now - group.StateSinceMs) <= GatherCatchUpMs) return false;
        bool pending = false;
        foreach (Member member in group.Members)
        {
            if (member.Real || !member.Joined || member.Guid == leader.Guid) continue;
            if (world.FindOnlinePlayer(member.Guid) is not { IsAlive: true } player) continue;
            if (ReferenceEquals(player.Map, leader.Map) && Distance(player, leader) <= GatherYards) continue;
            if ((member.AI?.CatchUpNow(player, leader, group) ?? PlayerbotCatchUpResult.Unable) == PlayerbotCatchUpResult.Pending)
                pending = true;
        }

        return !pending;
    }

    /// <summary>
    /// The leader is already as near the content as the approach point is (within the approach distance of the meeting point and
    /// <see cref="ApproachSlackYards"/>, flat), whichever side it came from. The approach point is fixed at formation on the side the
    /// leader stood on then (<see cref="ApproachPoint"/>), but a leader can be somewhere else by the time it travels: a waiting bot keeps
    /// exploring, and a teleport (an area trigger, a summon) moves it. From the far side it walks through the content, past the approach
    /// point it never reaches, and the group fights in the Travelling state for good (the state change to Engaging never comes).
    /// </summary>
    private static bool WithinApproachRing(Player leader, BotGroup group)
    {
        float yards = (group.WorldBoss is { } raid ? raid.RallyYards : group.Goal.InInstance ? ApproachYards : QuestApproachYards) + ApproachSlackYards;
        float dx = leader.X - group.Goal.Meeting.X, dy = leader.Y - group.Goal.Meeting.Y;
        return (dx * dx) + (dy * dy) <= yards * yards && MathF.Abs(leader.Z - group.Goal.Meeting.Z) <= ApproachHeightYards;
    }

    /// <summary>
    /// How far above or below the meeting point (the objective spawn's or the entrance trigger's own height) the leader may stand and
    /// still be in <see cref="WithinApproachRing"/>: slopes inside the ring rise a few yards, a cliff top or another floor does not count.
    /// </summary>
    internal const float ApproachHeightYards = 10f;

    /// <summary>How far inside the approach point's distance from the meeting point the leader counts as arrived (<see cref="WithinApproachRing"/>).</summary>
    internal const float ApproachSlackYards = 5f;

    private static bool Arrived(Player player, Vector3 point, float within)
        => Vector3.Distance(new Vector3(player.X, player.Y, player.Z), point) <= within;

    /// <summary>A wipe: every living member dead, or half the group or more dead while someone fights (<see cref="Party.PlayerbotPartyAI.IsWiping"/>).</summary>
    private bool CheckWipe(BotGroup group, Player leader)
    {
        if (_world is not { } world) return false;
        int alive = 0, dead = 0;
        bool fighting = false;
        // A handed-over group counts its bots only: the player who handed the lead over may be anywhere, alive or dead.
        foreach (Member member in group.Members.Where(m => m.Joined && !(m.Real && group.Handoff is not null)))
        {
            if (world.FindOnlinePlayer(member.Guid) is not { } player) continue;
            if (player.IsAlive)
            {
                alive++;
                fighting |= player.Combat.IsInCombat;
            }
            else
            {
                dead++;
                // The first member found dead at a check. A Unit exposes no time of death (Combat.DeathTimer is a countdown that restarts on release),
                // so when several die between two checks the one earliest in the member list stands for them.
                if (group.FirstDeath.IsEmpty) group.FirstDeath = member.Guid;
            }
        }

        if (dead == 0) group.FirstDeath = default;
        if (alive + dead == 0 || dead == 0) return false;
        // The fight goes on, or has just ended with the members' deaths (the killer may not have turned on anyone yet).
        fighting |= group.InFight(Now);
        if (alive > 0 && (!fighting || dead * 2 < alive + dead)) return false;
        Note($"group {group.Id} is wiping: {dead} dead, {alive} alive");
        group.Run?.Apply(PlayerbotRunSignal.Wipe);
        if (group.State != PlayerbotGroupState.Wiped)
        {
            NotePairs(group, BotSocial.PlayerbotSocialEvent.GroupWiped);
            NoteAgainst(group, group.FirstDeath, BotSocial.PlayerbotSocialEvent.DiedFirstInWipe);
            NoteRoles(group);
        }

        group.FirstDeath = default;
        Enter(group, PlayerbotGroupState.Wiped);
        return true;
    }

    /// <summary>
    /// The goal is reached: every bot member that has a quest asking for the objective has its count, and at least one had the quest
    /// (or, without one, the group killed the objective creature it engaged).
    /// </summary>
    private bool Complete(BotGroup group)
    {
        if (_world is not { } world) return false;
        group.UpdateObjective(world);
        if (!group.HadQuest) return group.ObjectiveKilled;
        foreach (Member member in group.Members.Where(m => m.Joined && !m.Real))
            if (world.FindOnlinePlayer(member.Guid) is { } player && !GoalDone(player, group.Goal)) return false;
        return true;
    }

    /// <summary>The members the stall watch looks at, refilled every tick (world thread).</summary>
    private readonly List<PlayerbotGroupStallMember> _stallMembers = [];

    /// <summary>
    /// The group-level stall watch (<see cref="PlayerbotGroupStallWatch"/>): bot members that make no progress for
    /// <see cref="PlayerbotOptions.StallSeconds"/> make the leader give up its target (set aside for the group for <see cref="SetAsideMs"/>)
    /// and choose again, with one stall report for bot QA (<see cref="TakeStalls"/>); a further bound without progress gives the goal up and
    /// dissolves the group (reason <c>stalled</c>; the goal is set aside for every member). Never a group with a real player in it, nor a
    /// stranded bot's walk out (<see cref="BotGroup.Solo"/>, it has its own bound), nor the states that wait on a bound of their own:
    /// forming (the invitations), gathering and regrouping (the leader holds for its members under their step bounds), a wipe (the survivors'
    /// retreats), leaving (the walk out, then <see cref="BringOut"/>; a finished run is not failed for its stragglers), the leader waiting at the
    /// objective's spawn (<see cref="ObjectiveMissing"/>) and disbanding. True when the group was dissolved.
    /// </summary>
    private bool WatchStall(BotGroup group, Player leader, uint now)
    {
        // A handed-over group is watched although a real player is in it: the bots lead it, and the player is never waited for.
        if (_world is not { } world || group.Solo || group.Members.Any(m => m.Real) && group.Handoff is null || group.MissingSinceMs != 0
            || group.State is PlayerbotGroupState.Forming or PlayerbotGroupState.Gathering or PlayerbotGroupState.Regrouping
                or PlayerbotGroupState.Wiped or PlayerbotGroupState.Leaving or PlayerbotGroupState.Disbanding
            || group.WorldBoss is { Waiting: true })
        {
            group.Stall.Reset();
            return false;
        }

        List<PlayerbotGroupStallMember> members = _stallMembers;
        members.Clear();
        foreach (Member member in group.Members)
        {
            if (!member.Joined || member.AI is not { } ai || world.FindOnlinePlayer(member.Guid) is not { } player) continue;
            members.Add(new PlayerbotGroupStallMember(member.BotId, player.MapId, new Vector3(player.X, player.Y, player.Z),
                player.Combat.IsInCombat || ai.Resting || ai.Looting, ai.TargetUnit?.Guid.Value ?? ai.LastTargetGuid.Value));
        }

        PlayerbotGroupStallVerdict verdict = group.Stall.Observe(group.State, members, now, (uint)Math.Max(1, _options.StallSeconds) * 1000u);
        if (verdict == PlayerbotGroupStallVerdict.None) return false;

        PlayerbotGroupAI? leaderAI = group.Find(group.LeaderBotId)?.AI;
        Game.Creatures.Creature? target = leaderAI?.Target;
        ObjectGuid targetGuid = target?.Guid ?? leaderAI?.LastTargetGuid ?? default;
        uint targetEntry = target?.Entry ?? leaderAI?.LastTargetEntry ?? 0;
        uint seconds = group.Stall.IdleMs / 1000;
        string step = leaderAI?.Step ?? "none";
        string state = group.State.ToString().ToLowerInvariant();
        if (verdict == PlayerbotGroupStallVerdict.Retarget)
        {
            StallCount++;
            // The objective is the target only while the group engages it; elsewhere (gathering, travelling, regrouping) it has none.
            uint entry = group.State == PlayerbotGroupState.Engaging ? (targetEntry != 0 ? targetEntry : group.ObjectiveEntry) : 0;
            _stalls.Add((group.LeaderBotId, leader.Name, string.Create(CultureInfo.InvariantCulture,
                $"stalled {seconds}s: goal={PlayerbotGoalKind.Group} target={entry} quest={group.Goal.QuestId} at {leader.X:F1},{leader.Y:F1},{leader.Z:F1} map {leader.MapId}")));
            if (!targetGuid.IsEmpty) group.Shun(targetGuid, unchecked(now + SetAsideMs));
            leaderAI?.Retarget(leader);
            string what = targetEntry != 0 ? string.Create(CultureInfo.InvariantCulture, $"gives up target {targetEntry} and re-targets") : "re-targets";
            Note(string.Create(CultureInfo.InvariantCulture, $"group {group.Id} stalled {seconds}s in {state} (leader {step}): {what}"));
            logger.LogWarning("Playerbot group {Group} stalled {Seconds}s in {State} (leader {Leader}: {Step}): {What}",
                group.Id, seconds, state, leader.Name, step, what);
            return false;
        }

        Note(string.Create(CultureInfo.InvariantCulture, $"group {group.Id} stalled {seconds}s in {state} (leader {step}) after re-targeting: dissolves"));
        logger.LogWarning("Playerbot group {Group} stalled {Seconds}s in {State} after re-targeting (leader {Leader}: {Step}): it gives up {Goal} and dissolves",
            group.Id, seconds, state, leader.Name, step, group.Goal);
        Finish(group, success: false, "stalled");
        return true;
    }

    /// <summary>The members did not get out of the instance in time: the teleport service brings them to the entrance outside.</summary>
    private void BringOut(BotGroup group)
    {
        if (_world is null || services.GetService<TeleportFeature>()?.Teleports is not { } teleports) return;
        foreach (Member member in group.Members.Where(m => m.Joined && !m.Real))
            if (_world.FindOnlinePlayer(member.Guid) is { IsAlive: true } player && player.MapId == group.Goal.MapId && !teleports.IsBeingTeleported(player))
                teleports.TeleportTo(player, group.Goal.MeetingMap, group.Goal.Meeting.X, group.Goal.Meeting.Y, group.Goal.Meeting.Z, 0f);
        group.BringOuts++;
        group.StateSinceMs = Now;
        Note($"group {group.Id} could not walk out: brought to the entrance");
    }

    /// <summary>How long the group waits at an objective's spawn with none of it alive in sight before it gives the goal up.</summary>
    internal const uint ObjectiveMissingMs = 120_000;

    /// <summary>
    /// The leader stands at the objective's spawn and no living objective creature is in sight, for <see cref="ObjectiveMissingMs"/>. For a
    /// run's boss "in sight" is "alive on the map": a boss that patrols away from its spawn is not missing, one that is dead or was never
    /// spawned (a saved instance) is (see <see cref="FindLivingOnMap"/>).
    /// </summary>
    internal bool ObjectiveMissing(BotGroup group, Player leader, uint now)
    {
        bool there = leader.MapId == group.Goal.MapId
            && Vector3.Distance(new Vector3(leader.X, leader.Y, leader.Z), group.ObjectivePosition) <= 15f;
        bool seen = group.ObjectiveEntry != 0 && leader.Map is { } map && leader.VisibleObjects.Any(guid =>
            map.FindObject(guid) is Game.Creatures.Creature { IsAlive: true } creature && creature.Entry == group.ObjectiveEntry);
        if (!seen && there && group.Run is not null && group.ObjectiveEntry != 0)
            seen = FindLivingOnMap(leader, group.ObjectiveEntry) is not null || !GridLoadedAt(leader, group.ObjectivePosition);
        if (!there || seen || leader.Combat.IsInCombat)
        {
            group.MissingSinceMs = 0;
            return false;
        }

        if (group.MissingSinceMs == 0) group.MissingSinceMs = Math.Max(1u, now);
        return unchecked(now - group.MissingSinceMs) > ObjectiveMissingMs;
    }

    /// <summary>
    /// The living creature with <paramref name="entry"/> anywhere on <paramref name="player"/>'s map (the grids that are loaded: the
    /// creature system holds a spawn alive, as a corpse or hidden until its respawn), or null: none is alive, or it never spawned.
    /// </summary>
    internal static Game.Creatures.Creature? FindLivingOnMap(Player player, uint entry)
    {
        if (entry == 0 || player.Map?.FindUpdater<Game.Creatures.CreatureMapSystem>() is not { } system) return null;
        foreach (Game.Creatures.Creature creature in system.Creatures)
            if (creature.Entry == entry && creature.IsAlive) return creature;
        return null;
    }

    /// <summary>Whether the grid at <paramref name="at"/> is loaded on the player's map (a creature in an unloaded grid is not in the creature system: absent says nothing).</summary>
    private static bool GridLoadedAt(Player player, Vector3 at)
        => player.Map?.FindUpdater<Game.Creatures.CreatureMapSystem>() is { } system && system.IsGridLoaded(at.X, at.Y);

    private void Enter(BotGroup group, PlayerbotGroupState state)
    {
        group.State = state;
        group.StateSinceMs = Now;
        group.StateEnteredMs = group.StateSinceMs;
        group.StepBudgetMs = 0;
        group.BringOuts = 0;
        if (state is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling) BudgetTravel(group, state);
        if (state == PlayerbotGroupState.Gathering) group.ObjectiveKilled = false;
        if (state == PlayerbotGroupState.Wiped && group.Run is not null) group.ClearObjective();
        if (state == PlayerbotGroupState.Travelling)
        {
            group.ApproachReached = false;
            TeleportToEntranceIfConfigured(group);
        }

        if (group.WorldBoss is not null) OnWorldBossState(group, state);
    }

    /// <summary>End the group: done, or given up (the goal is then set aside for every bot member unless <paramref name="setAside"/> is off).</summary>
    private void Finish(BotGroup group, bool success, string reason, bool setAside = true)
    {
        if (success) NoteSuccess(group);

        // A handed-over group never walks out or disbands (that would take the bots out of the player's group): it is released in place.
        if (group.Handoff is { } handoff)
        {
            EndHandoff(group, success, reason, handBackTo: handoff.HandedBy);
            return;
        }

        if (!success && setAside)
            foreach (Member member in group.Members.Where(m => !m.Real))
            {
                if (group.Run is { } run) SetRunAside(member.BotId, run, reason);
                else SetAside(member.BotId, group.Goal, suspendInBrain: true, failed: true);
            }

        if (!success) group.Run?.Apply(PlayerbotRunSignal.Abort(reason));
        if (!success) Totals = (Totals.Formed, Totals.Completed, Totals.Failed + 1);
        if (!success) Note($"group {group.Id} gave up {group.Goal}: {reason}");
        group.Succeeded = success;
        group.Reason = reason;
        // Inside an instance a failed group still walks out before it disbands.
        bool inside = group.Members.Any(m => !m.Real && _world?.FindOnlinePlayer(m.Guid) is { } p && p.MapId == group.Goal.MapId && group.Goal.InInstance);
        Enter(group, inside && group.State != PlayerbotGroupState.Leaving ? PlayerbotGroupState.Leaving : PlayerbotGroupState.Disbanding);
        if (group.State == PlayerbotGroupState.Disbanding) Disband(group);
    }

    /// <summary>
    /// The group succeeded: every member pair remembers it (trust, affinity) and the roles played. Once per group, from whichever end of a success
    /// reaches it first: the ordinary completions set <see cref="BotGroup.Succeeded"/> and walk to <see cref="Disband"/> without going through
    /// <see cref="Finish"/>, so the disband and the handoff end note it too.
    /// </summary>
    private void NoteSuccess(BotGroup group)
    {
        if (group.SuccessNoted) return;
        group.SuccessNoted = true;
        NotePairs(group, BotSocial.PlayerbotSocialEvent.GroupSucceeded);
        NoteRoles(group);
    }

    /// <summary>Every bot member leaves the group (CMSG_GROUP_DISBAND, the client's leave) and goes back to its brain.</summary>
    private void Disband(BotGroup group)
    {
        if (group.Succeeded) NoteSuccess(group);
        if (!group.Succeeded && group.Reason is null) group.Reason = "disbanded";
        if (group.Succeeded && group.Reason is null)
        {
            group.Reason = "completed";
            Totals = (Totals.Formed, Totals.Completed + 1, Totals.Failed);
        }

        // A goal that was done starts afresh: its earlier failures no longer lengthen a later set-aside.
        if (group.Succeeded && group.Run is null)
            foreach (Member member in group.Members.Where(m => !m.Real)) _failStreaks.Remove((member.BotId, group.Goal.Key));
        ResetCompletedRun(group);
        foreach (Member member in group.Members.Where(m => !m.Real).ToArray()) Release(group, member);
        foreach (Member member in group.Members) _kicked.Remove(member.Guid); // a kick the scan never reached (the group ended first)
        _groups.Remove(group);
        NoteRunEnded(group);
        NoteWorldBossEnded(group);
        Note($"group {group.Id} disbanded ({group.Reason})");
        logger.LogInformation("Playerbot group {Group} disbanded ({Reason})", group.Id, group.Reason);
    }

    private void Release(BotGroup group, Member member, ObjectGuid? leftAlready = null)
    {
        _memberOf.Remove(member.BotId);
        _kicked.Remove(member.Guid);
        if (!_bots.TryGetValue(member.BotId, out PlayerbotGroupBot bot)) return;
        bot.Brain.GroupHeld.Remove((group.Goal.QuestId, group.Goal.ObjectiveEntry));
        if (bot.Session.Player is not { } player) return;
        member.AI?.Release(player);
        PlayerbotAreaTriggers.AllowTeleports(player, false);
        if (leftAlready is null && GroupManagerOrNull() is { } groupManager && (groupManager.GetGroup(player.Guid) is { } server && server.IsMember(player.Guid)
                || groupManager.GetInvite(player.Guid) is not null))
        {
            bool ran = Act(bot.Session, groupManager.GetInvite(player.Guid) is not null && groupManager.GetGroup(player.Guid) is null
                ? WorldOpcode.CmsgGroupDecline : WorldOpcode.CmsgGroupDisband, [], budgeted: false);
            // Outcome: the bot released from the group is out of the server's group, or no brain and no matching can have it again.
            // Not reported during the shutdown (BeginShutdown): the bots are being stopped, so the leave may legitimately not complete, and the bot may genuinely
            // still be in a persisted server group. That is cleaned up on the next boot only if every other member of that group is an online bot.
            if (!_shuttingDown && (ran || player.IsInWorld) && (groupManager.GetGroup(player.Guid) is { } still && still.IsMember(player.Guid) || groupManager.GetInvite(player.Guid) is not null))
                PlayerbotGroupAnomalies.Report(player, PlayerbotGroupAnomalies.MemberStillGrouped, string.Create(CultureInfo.InvariantCulture,
                    $"released from group {group.Id} ({group.Reason ?? "no reason"}) but still in the server's group or invited to one (leave dispatched {ran})"));
        }
        bot.Brain.ResumeAfterGroup();
    }

    private void DropMember(BotGroup group, Guid botId, string reason, ObjectGuid? guid = null)
    {
        Member? member = botId == Guid.Empty ? group.Members.FirstOrDefault(m => m.Real && m.Guid == guid) : group.Find(botId);
        if (member is null) return;
        group.Members.Remove(member);
        if (!member.Real) Release(group, member, guid);
        Note($"group {group.Id}: {member.Name} {reason}");
        if (member.BotId == group.LeaderBotId && !member.Real)
        {
            if (group.Siege is not null && !_detaching && AdoptServerLeader(group)) return;
            if (!_shuttingDown && !_detaching)
                PlayerbotGroupAnomalies.ReportNamed(member.Name, member.Guid, PlayerbotGroupAnomalies.LeaderGone,
                    string.Create(CultureInfo.InvariantCulture, $"group {group.Id} ({group.Goal}) lost its leader ({reason}) in state {group.State}; the group ends"),
                    AnomalySeverity.Info, _world?.Identity);
            Finish(group, success: false, "leader-" + reason);
        }
    }

    /// <summary>The longest a repeatedly failed goal is set aside (<see cref="EscalatedSetAsideMs"/>).</summary>
    internal const uint MaxSetAsideMs = 7_200_000;

    /// <summary>
    /// How long a goal is set aside after its <paramref name="failures"/>th failure in a row for one bot: <see cref="SetAsideMs"/> for the
    /// first, doubled for each further one, at most <see cref="MaxSetAsideMs"/> (10, 20, 40, 80, 120 minutes). Before, every failure
    /// set it aside for the same ten minutes and one leader tried one quest twelve times.
    /// </summary>
    internal static uint EscalatedSetAsideMs(int failures)
        => failures <= 1 ? SetAsideMs : (uint)Math.Min((ulong)MaxSetAsideMs, (ulong)SetAsideMs << Math.Min(failures - 1, 16));

    /// <summary>Failures in a row per bot and goal key, and when the last one was (pruned in <see cref="Scan"/> once the streak is stale).</summary>
    private readonly Dictionary<(Guid Bot, (PlayerbotGroupGoalKind, uint) Key), (int Failures, uint LastMs)> _failStreaks = [];

    /// <summary>A failure streak older than this is forgotten: the goal got a fresh start (twice the longest set-aside).</summary>
    private const uint FailStreakMemoryMs = MaxSetAsideMs * 2;

    /// <summary>How long (ms) <paramref name="botId"/> still sets the goal with <paramref name="key"/> aside (0: not set aside; tests and status).</summary>
    internal uint SetAsideLeftMs(Guid botId, (PlayerbotGroupGoalKind, uint) key)
        => _setAside.TryGetValue((botId, key), out uint until) && unchecked((int)(until - Now)) > 0 ? unchecked(until - Now) : 0;

    internal void SetAside(Guid botId, PlayerbotGroupGoal goal, bool suspendInBrain, bool failed = false)
    {
        uint ms = SetAsideMs;
        if (failed)
        {
            var streakKey = (botId, goal.Key);
            int failures = _failStreaks.TryGetValue(streakKey, out var streak) && unchecked(Now - streak.LastMs) < FailStreakMemoryMs ? streak.Failures + 1 : 1;
            _failStreaks[streakKey] = (failures, Now);
            ms = EscalatedSetAsideMs(failures);
        }

        _setAside[(botId, goal.Key)] = unchecked(Now + ms);
        NoteStruggle(botId, goal);
        if (!suspendInBrain || _world is null || !_bots.TryGetValue(botId, out PlayerbotGroupBot bot)) return;
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
        if (_world is not { } world || !player.IsAlive || player.Map is not { } map || WorldMaps.Of(world).Registry.Find(player.MapId) is not { IsDungeon: true }) return false;
        if (GroupManagerOrNull()?.GetGroup(player.Guid) is not null) return false;
        if (ExitOf(world, player.MapId) is not { } exit || WorldMaps.Of(world).FindAreaTriggerTeleport(exit.Id) is not { } teleport) return false;
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
        if (_botByPlayer.TryGetValue(guid, out Guid botId)) return botId;
        foreach ((Guid id, PlayerbotGroupBot bot) in _bots)
            if (bot.Session.Player?.Guid == guid) return id;
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
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            return null;
        }
    }

    private void Note(string line)
    {
        Events.Add(string.Create(CultureInfo.InvariantCulture, $"{Now / 1000}s {line}"));
        if (Events.Count > 32) Events.RemoveAt(0);
    }

    // --- the group --------------------------------------------------------------------------------------------------

    /// <summary>One bot-led group and its goal.</summary>
    internal sealed class BotGroup(int id, PlayerbotGroupGoal goal, Guid leaderBotId, ObjectGuid leaderGuid, uint formedMs)
    {
        public int Id { get; } = id;
        public PlayerbotGroupGoal Goal { get; } = goal;
        public Guid LeaderBotId { get; set; } = leaderBotId;
        public ObjectGuid LeaderGuid { get; set; } = leaderGuid;
        public uint FormedMs { get; } = formedMs;
        public List<Member> Members { get; } = [];
        public PlayerbotGroupState State { get; set; } = PlayerbotGroupState.Forming;
        public uint StateSinceMs { get; set; } = formedMs;

        /// <summary>When the group entered its state; unlike <see cref="StateSinceMs"/> a step's own retry (<c>BringOut</c>) does not move it.</summary>
        public uint StateEnteredMs { get; set; } = formedMs;

        /// <summary>The outcome clocks of the group (<see cref="PlayerbotGroupWatch"/>).</summary>
        public PlayerbotGroupWatch Watch { get; } = new();

        /// <summary>The distance-scaled limit of the current gathering or travelling step of an instance group (0: the plain step limit; <see cref="BudgetTravel"/>).</summary>
        public uint StepBudgetMs { get; set; }

        /// <summary>Time granted to travel steps beyond <see cref="StepTimeoutMs"/>; the group's overall bound grows by it.</summary>
        public uint ExtraTimeMs { get; set; }
        public int Failures { get; set; }
        public bool Raid { get; set; }
        public bool Solo { get; init; }
        public bool HadQuest { get; set; }

        /// <summary>The dungeon or raid run this group is on (null for a quest group).</summary>
        public PlayerbotRunMachine? Run { get; set; }

        /// <summary>A player's own group whose lead was handed to a bot inside an instance (<c>PlayerbotGroupCoordinator.Handoff.cs</c>), else null.</summary>
        public PlayerbotHandoff? Handoff { get; init; }

        /// <summary>The siege raid this group is (<c>PlayerbotGroupCoordinator.Siege.cs</c>), else null.</summary>
        public PlayerbotSiegeRaid? Siege { get; set; }

        /// <summary>The world-boss raid this group is (<c>PlayerbotGroupCoordinator.WorldBoss.cs</c>), else null.</summary>
        public PlayerbotWorldBossRaid? WorldBoss { get; set; }

        /// <summary>A reason the group AI found that the run cannot go on (no path to the boss or the entrance); the coordinator gives the run up.</summary>
        public string? RunFault { get; set; }

        /// <summary>When the last boss died and the group began to wait for its loot (0: not yet).</summary>
        public uint RunDoneMs { get; set; }

        /// <summary>The creature the group is after now: the run's current boss, else the goal's objective.</summary>
        public uint ObjectiveEntry => Run is { } run ? run.CurrentBoss?.Entry ?? 0 : Goal.ObjectiveEntry;

        /// <summary>Where that creature stands.</summary>
        public Vector3 ObjectivePosition => Run is { } run ? run.CurrentBoss?.Position ?? Goal.Objective : Goal.Objective;

        /// <summary>The quests a member already pushed to the group (<see cref="ShareQuests"/>).</summary>
        public HashSet<uint> Shared { get; } = [];

        /// <summary>Where the group gathers before the content (<see cref="ApproachPoint"/>).</summary>
        public Vector3 Approach { get; set; }

        /// <summary>The leader walked toward <see cref="Approach"/> without getting nearer for <see cref="PlayerbotGroupAI.ApproachNoProgressMs"/>.</summary>
        public bool ApproachReached { get; set; }

        /// <summary>How many times <see cref="BringOut"/> has run in the current <see cref="PlayerbotGroupState.Leaving"/> state.</summary>
        public int BringOuts { get; set; }
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

        /// <summary>The success has been recorded in the members' social memory (once per group).</summary>
        public bool SuccessNoted { get; set; }

        /// <summary>The first member seen dead in the current fight (empty while nobody is dead); the social memory blames it for a wipe.</summary>
        public ObjectGuid FirstDeath { get; set; }
        public string? Reason { get; set; }

        public Member? Find(Guid botId)
        {
            if (botId == Guid.Empty) return null;
            foreach (Member m in Members)
                if (m.BotId == botId) return m;
            return null;
        }

        public Player? LeaderPlayer(WorldRuntime world) => world.FindOnlinePlayer(LeaderGuid);

        public bool StepTimedOut(uint now) => StepTimedOut(now, StepTimeoutMs);

        /// <summary>The state has lasted more than <paramref name="limitMs"/>.</summary>
        public bool StepTimedOut(uint now, uint limitMs) => unchecked(now - StateSinceMs) > limitMs;

        /// <summary>The leader engaged an objective creature: its death (seen by <see cref="ObjectiveKilled"/>) completes a goal without a quest.</summary>
        public void NoteObjective(Game.Creatures.Creature creature)
        {
            Objective = creature.Guid;
            ObjectiveCreature = creature.Entry;
        }

        /// <summary>The entry of the creature <see cref="Objective"/> names.</summary>
        public uint ObjectiveCreature { get; private set; }

        /// <summary>Forget the engaged objective (a run goes on to its next boss).</summary>
        public void ClearObjective()
        {
            Objective = default;
            ObjectiveCreature = 0;
            ObjectiveKilled = false;
            MissingSinceMs = 0;
        }

        /// <summary>The group's stall watch (<see cref="WatchStall"/>).</summary>
        public PlayerbotGroupStallWatch Stall { get; } = new();

        private readonly Dictionary<ObjectGuid, uint> _shunned = [];

        /// <summary>
        /// Creatures the group gave up as targets (its stall watch: no progress against them), until when; the leader neither keeps nor
        /// pulls them meanwhile (one attacking a member is still fought).
        /// </summary>
        public bool IsShunned(ObjectGuid creature, uint now)
        {
            if (!_shunned.TryGetValue(creature, out uint until)) return false;
            if (unchecked((int)(until - now)) > 0) return true;
            _shunned.Remove(creature);
            return false;
        }

        /// <summary>Give <paramref name="creature"/> up as a target until <paramref name="until"/>.</summary>
        public void Shun(ObjectGuid creature, uint until)
        {
            if (_shunned.Count >= 64) _shunned.Clear();
            _shunned[creature] = until;
        }

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

        /// <summary>Whether the member qualified for an instance goal when the group formed (in band, or a direct reason; <see cref="PlayerbotGroupCandidate.Qualifying"/>).</summary>
        public bool Qualifying { get; init; } = true;
        public bool Invited { get; set; }
        public bool Refused { get; set; }
        public uint InvitedMs { get; set; }
        public PlayerbotGroupAI? AI { get; set; }

        public byte FocusIcon { get; set; } = Party.PlayerbotParty.SkullIcon;

        /// <summary>The member's outcome clocks (<see cref="PlayerbotMemberWatch"/>).</summary>
        public PlayerbotMemberWatch Watch { get; } = new();

        private (int GroupId, PlayerbotGroupRole Role, PlayerbotGroupState State, bool IsLeader, bool HasRun, bool HasHandoff) _descKey;
        private string? _desc;

        public string Describe(BotGroup group)
        {
            var key = (group.Id, Role, group.State, group.LeaderBotId == BotId, group.Run is not null, group.Handoff is not null);
            if (_desc is not null && _descKey == key) return _desc;
            _descKey = key;
            return _desc = string.Create(CultureInfo.InvariantCulture,
                $"group={group.Id}:{Role.ToString().ToLowerInvariant()}:{group.State.ToString().ToLowerInvariant()}{(key.Item4 ? ":leader" : string.Empty)}{(key.Item5 ? ":run" : string.Empty)}{(key.Item6 ? ":handoff" : string.Empty)}");
        }
    }
}
