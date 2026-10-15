using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// Autonomous managed bots in battlegrounds (<c>World:Playerbots:Battlegrounds</c>, docs/areas/playbots-battlegrounds.md). World feature;
/// <see cref="ManagedPlayerbotFeature"/> calls it on the world thread every tick with its running bots, before any bot thinks.
/// <para>
/// <b>Who queues.</b> Every <see cref="ScanMs"/> each free bot (autonomous, not in a real player's party, not in or waiting for a bot-led
/// group, in no group, alive, out of combat, in no queue, no deserter, on a map of <see cref="PlayerbotOptions.AllowedMaps"/>) is looked
/// over: the battleground types it may join (<see cref="PlayerbotBattlegroundBrackets.Eligible"/>: Warsong Gulch and Arathi Basin only,
/// the level within the template, the objective content loaded, a battlemaster of its faction within
/// <see cref="PlayerbotBattlegroundOptions.MaxTravelYards"/>), and whether it goes (<see cref="PlayerbotBattlegroundDemand.Pick"/>: a real
/// player's queue first, else the per-minute chance, under the caps). The snapshot the caps read is rebuilt at every scan from the
/// manager itself: the real players waiting in each queue (not invited yet), the bots this coordinator committed, and the bots in each
/// running match that still has room. Only sessions of managed bots are ever acted for; a real player is read, never driven.
/// </para>
/// <para>
/// <b>The queue, like a client.</b> <see cref="PlayerbotBattlegroundQueueMachine"/> decides, every tick, from what the server's own
/// state says (the bot's queue slots, its invitation, the match it is bound to): walk to the battlemaster
/// (<see cref="PlayerbotBattlegroundAI.Travel"/>), CMSG_BATTLEMASTER_HELLO and CMSG_BATTLEMASTER_JOIN (vmangos BattleGroundHandler.cpp:40-264),
/// wait in the queue doing what the brain does, answer the invitation with CMSG_BATTLEFIELD_PORT (enter, or leave when the invited match
/// already holds <see cref="PlayerbotBattlegroundOptions.MaxBotsPerBattleground"/> bots; BattleGroundHandler.cpp:361-507), leave the queue
/// after <see cref="PlayerbotBattlegroundOptions.MaxQueueWaitMinutes"/>, play the match (<see cref="PlayerbotBattlegroundAI.Play"/>), and
/// leave it with CMSG_LEAVE_BATTLEFIELD once it is over (BattleGroundHandler.cpp:510-525; vmangos BattleBotAI.cpp:461-485 leaves on the
/// ended scoreboard). The server answers these as it answers any client; the coordinator reads the outcome from the manager, as a client
/// reads it from SMSG_BATTLEFIELD_STATUS. Back in the world the brain resumes what it was doing (its quests and memories kept).
/// </para>
/// </summary>
public sealed class PlayerbotBattlegroundCoordinator(IServiceProvider services, ILogger<PlayerbotBattlegroundCoordinator> logger) : IWorldFeature
{
    /// <summary>How often free bots are looked over and the demand snapshot is rebuilt.</summary>
    internal const uint ScanMs = 2_000;

    /// <summary>How long a type a bot gave up on (no join answer, a queue that never popped) is set aside for that bot.</summary>
    internal const uint SetAsideMs = PlayerbotBattlegroundQueueMachine.SetAsideMs;

    /// <summary>A leave the server did not act on (in combat while the match runs) is sent again after this long.</summary>
    internal const uint LeaveRetryMs = 5_000;

    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private readonly Dictionary<Guid, Tracked> _tracked = [];
    private readonly Dictionary<(Guid Bot, BattlegroundType Type), uint> _setAside = [];
    private readonly Dictionary<Guid, PlayerbotBattlegroundBot> _bots = [];
    private readonly HashSet<(BattlegroundType, PlayerbotBattlegroundRefusal)> _loggedRefusals = [];
    private readonly PlayerbotBattlegroundDemand _demand = new();
    private readonly Random _random = new(0x5EED);
    private WorldRuntime? _world;
    private uint _scanElapsed = ScanMs;
    private CreatureContent? _mastersOf;
    private int _mastersVersion = -1;
    private readonly Dictionary<uint, List<Battlemaster>> _masters = [];
    private readonly List<Guid> _gone = [];
    private readonly List<Tracked> _advancing = [];
    private readonly Dictionary<BattlegroundType, bool> _content = [];
    private (object? Objects, object? Triggers, object? Spells, object? Events) _contentOf;

    /// <summary>What happened so far: queued, entered, left after a match, invitations declined at the cap, queues given up (status and tests).</summary>
    internal (int Queued, int Entered, int Left, int Declined, int QueueTimeouts) Totals { get; private set; }

    /// <summary>What happened last to bots in battlegrounds, newest last (at most 32 lines; status and tests).</summary>
    internal List<string> Events { get; } = [];

    /// <summary>The refusals noted so far, once per type and reason (status and tests).</summary>
    internal IReadOnlyCollection<(BattlegroundType Type, PlayerbotBattlegroundRefusal Why)> Refusals => _loggedRefusals;

    public void Attach(WorldRuntime world) => _world = world;

    internal PlayerbotBattlegroundOptions Options => _options.Battlegrounds;

    private uint Now => _world?.NowMs ?? 0;

    // --- the feature's calls ------------------------------------------------------------------------------------------

    /// <summary>A running bot as the coordinator sees it (<see cref="Update"/>).</summary>
    internal readonly record struct PlayerbotBattlegroundBot(Guid BotId, WorldSession Session, PlayerbotBrain Brain, bool Free);

    /// <summary>One bot this coordinator follows: its phase, its battleground business and its AI.</summary>
    internal sealed class Tracked(Guid botId, string name)
    {
        public Guid BotId { get; } = botId;
        public string Name { get; set; } = name;
        public PlayerbotBattlegroundPhase Phase { get; set; }
        public uint PhaseSinceMs { get; set; }
        public BattlegroundType Type { get; set; }
        public int Bracket { get; set; }
        public Team Team { get; set; }
        public ObjectGuid Battlemaster { get; set; }
        public Vector3 MasterAt { get; set; }
        public bool HelloSent { get; set; }
        public bool JoinSent { get; set; }
        public uint QueuedSinceMs { get; set; }
        public uint LeaveSentMs { get; set; }
        public uint InstanceId { get; set; }
        public PlayerbotBattlegroundRole Role { get; set; }

        /// <summary>The bot's talent role is healer (read once, when it enters its match; the roles prefer healers as escorts).</summary>
        public bool Healer { get; set; }

        public PlayerbotBattlegroundAI? AI { get; set; }
    }

    /// <summary>
    /// One world tick (world thread): every followed bot's queue machine (its replies are a client's own and never wait for the action
    /// budget), and every <see cref="ScanMs"/> the demand snapshot, the roles in the running matches and the free bots' choices.
    /// </summary>
    internal void Update(IReadOnlyList<PlayerbotBattlegroundBot> bots, uint elapsedMs)
    {
        _bots.Clear();
        foreach (PlayerbotBattlegroundBot bot in bots) _bots[bot.BotId] = bot;
        if (Feature() is not { } feature) return;
        _gone.Clear();
        foreach (Guid id in _tracked.Keys)
            if (!_bots.ContainsKey(id)) _gone.Add(id);
        foreach (Guid gone in _gone) _tracked.Remove(gone);
        _advancing.Clear();
        _advancing.AddRange(_tracked.Values);
        foreach (Tracked tracked in _advancing) Advance(tracked, feature);
        _scanElapsed = unchecked(_scanElapsed + elapsedMs);
        if (_scanElapsed < ScanMs) return;
        uint since = _scanElapsed;
        _scanElapsed = 0;
        Scan(feature, since);
    }

    /// <summary>
    /// Test hook, not for production code: make the next tick scan the free bots (world thread), so a test can line an event up with a scan
    /// (PlayerbotBattlegroundWorldTests.PartyBot_OfARealPlayer_BesideTheBattlemaster_NeverQueues). The coordinator itself never calls it.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal void ScanAtNextTick() => _scanElapsed = ScanMs;

    /// <summary>Whether this coordinator drives <paramref name="botId"/> now (walking to a battlemaster, joining, entering, in a match, leaving).</summary>
    internal bool Drives(Guid botId) => _tracked.TryGetValue(botId, out Tracked? t) && PlayerbotBattlegroundQueueMachine.Drives(t.Phase);

    /// <summary>Whether <paramref name="botId"/> has battleground business (any phase but idle): other coordinators leave it alone.</summary>
    internal bool Holds(Guid botId) => _tracked.ContainsKey(botId);

    /// <summary>The followed bot (tests and status).</summary>
    internal Tracked? Find(Guid botId) => _tracked.GetValueOrDefault(botId);

    /// <summary>One driven tick of a bot (world thread, within the shared action budget like the brain).</summary>
    internal void UpdateMember(Guid botId, Player player, uint elapsedMs)
    {
        if (!_tracked.TryGetValue(botId, out Tracked? t) || t.AI is not { } ai || !_bots.TryGetValue(botId, out PlayerbotBattlegroundBot bot)
            || !bot.Session.IsManaged) return;
        switch (t.Phase)
        {
            case PlayerbotBattlegroundPhase.Travel:
                ai.Travel(player, t.MasterAt, elapsedMs);
                break;
            case PlayerbotBattlegroundPhase.InMatch when Feature()?.BattlegroundOf(player.Guid) is { } match && match.PlayerTeam(player.Guid) is { } team:
                ai.Play(player, match, team, t.Role, elapsedMs);
                break;
            default:
                // Joining at the battlemaster, porting in, leaving: the acknowledgements, and stand still.
                if (!PlayerbotMovementControl.Update(bot.Session, player) && (player.Movement.Flags & MovementFlags.MaskMoving) != 0)
                    PlayerbotMovementControl.Stop(bot.Session, player);
                break;
        }
    }

    /// <summary>The goal and target a driven bot reports: <see cref="PlayerbotGoalKind.Battleground"/> (or its corpse run) and its objective.</summary>
    internal (PlayerbotGoalKind Goal, uint TargetEntry)? GoalOf(Guid botId)
        => _tracked.TryGetValue(botId, out Tracked? t) && PlayerbotBattlegroundQueueMachine.Drives(t.Phase)
            ? (t.AI?.Goal ?? PlayerbotGoalKind.Battleground, t.AI?.TargetEntry ?? 0) : null;

    /// <summary>A bot stopped, became scripted or left the world: forget its battleground business (the server keeps its queue or match).</summary>
    internal void Forget(Guid botId) => _tracked.Remove(botId);

    /// <summary>The battleground part of a bot's status line: <c>bg=WarsongGulch:inmatch:attacker:taking-flag</c>, or null.</summary>
    internal string? Describe(Guid botId)
    {
        if (!_tracked.TryGetValue(botId, out Tracked? t)) return null;
        string phase = t.Phase.ToString().ToLowerInvariant();
        return t.Phase == PlayerbotBattlegroundPhase.InMatch
            ? string.Create(CultureInfo.InvariantCulture, $"bg={t.Type}:{phase}:{t.Role.ToString().ToLowerInvariant()}:{t.AI?.Activity ?? "idle"}")
            : string.Create(CultureInfo.InvariantCulture, $"bg={t.Type}:{phase}");
    }

    /// <summary>The <c>.playerbot battlegrounds</c> lines (world thread).</summary>
    internal IReadOnlyList<string> Report()
    {
        PlayerbotBattlegroundOptions o = _options.Battlegrounds;
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"Bot battlegrounds: {(o.Enabled ? "enabled" : "disabled")}, chance {o.QueueChancePerMinute}/min, at most {o.MaxBotsPerBattleground} bots per battleground; {_tracked.Count} bots busy; queued {Totals.Queued}, entered {Totals.Entered}, left {Totals.Left}, declined {Totals.Declined}, queues given up {Totals.QueueTimeouts}."),
        };
        foreach (Tracked t in _tracked.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{t.Name} {t.Type} bracket {t.Bracket} {t.Team} {t.Phase.ToString().ToLowerInvariant()} for {unchecked(Now - t.PhaseSinceMs) / 1000}s{(t.Phase == PlayerbotBattlegroundPhase.InMatch ? $" {t.Role.ToString().ToLowerInvariant()} {t.AI?.Activity}" : string.Empty)}"));
        foreach ((BattlegroundType type, PlayerbotBattlegroundRefusal why) in _loggedRefusals.OrderBy(r => r.Item1).ThenBy(r => r.Item2))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"refused {type}: {why}"));
        foreach (string line in Events.TakeLast(5)) lines.Add("last: " + line);
        return lines;
    }

    // --- the queue machine, every tick ----------------------------------------------------------------------------------

    private void Advance(Tracked t, BattlegroundFeature feature)
    {
        if (!_bots.TryGetValue(t.BotId, out PlayerbotBattlegroundBot bot) || bot.Session.Player is not { } player || !bot.Session.IsManaged)
        {
            _tracked.Remove(t.BotId);
            return;
        }

        t.Name = player.Name;
        PlayerbotBattlegroundFacts facts = Facts(t, bot, player, feature);
        PlayerbotBattlegroundPhase before = t.Phase;
        (PlayerbotBattlegroundPhase next, PlayerbotBattlegroundAction action) = PlayerbotBattlegroundQueueMachine.Next(before, facts);
        Do(t, bot, player, feature, action, facts);
        if (next != before)
        {
            t.Phase = next;
            t.PhaseSinceMs = Now;
            if (next == PlayerbotBattlegroundPhase.Queued && before == PlayerbotBattlegroundPhase.Join)
            {
                t.QueuedSinceMs = Now;
                Totals = (Totals.Queued + 1, Totals.Entered, Totals.Left, Totals.Declined, Totals.QueueTimeouts);
                Note($"{t.Name} queued for {t.Type} (bracket {t.Bracket})");
                logger.LogInformation("Playerbot {Bot} queued for {Type}, bracket {Bracket}", t.Name, t.Type, t.Bracket);
            }

            if (next == PlayerbotBattlegroundPhase.InMatch)
            {
                t.InstanceId = feature.BattlegroundOf(player.Guid)?.InstanceId ?? 0;
                t.Healer = TalentRole(bot.Session, player) == PlayerbotRole.Healer;
                Note($"{t.Name} is in {t.Type} match {t.InstanceId}");
            }

            // The brain takes the bot back whenever the coordinator stops driving it (queued, or done).
            if (PlayerbotBattlegroundQueueMachine.Drives(before) && !PlayerbotBattlegroundQueueMachine.Drives(next)) Release(t, bot, player);
            if (next == PlayerbotBattlegroundPhase.Idle) _tracked.Remove(t.BotId);
        }
        else if (t.Phase == PlayerbotBattlegroundPhase.Leaving && facts.Bound && unchecked(Now - t.LeaveSentMs) >= LeaveRetryMs)
        {
            // The handler refuses a leave in combat while the match runs (BattleGroundHandler.cpp:510-525): try again.
            Leave(t, bot, player);
        }
    }

    private PlayerbotBattlegroundFacts Facts(Tracked t, PlayerbotBattlegroundBot bot, Player player, BattlegroundFeature feature)
    {
        BattlegroundManager manager = feature.Manager;
        BattlegroundPlayerState state = manager.StateOf(player.Guid);
        var queue = (BattlegroundQueueType)(byte)t.Type;
        bool bound = state.InBattleground && state.Type == t.Type;
        Battleground? match = bound ? manager.GetBattleground(state.InstanceId, state.Type) : null;
        uint invited = state.InvitedInstance(queue);
        bool teleporting = services.GetService<TeleportFeature>()?.Teleports is { } teleports && teleports.IsBeingTeleported(player);
        bool arrived = match is not null && match.PlayerTeam(player.Guid) is not null && player.IsInWorld && player.Map is { } map
            && map.MapId == match.MapId && map.InstanceId == match.InstanceId;
        bool reach = t.Phase is PlayerbotBattlegroundPhase.Travel or PlayerbotBattlegroundPhase.Join && player.IsInWorld
            && player.Map?.FindObject(t.Battlemaster) is Creature { IsAlive: true } master && Distance(player, master) <= PlayerbotBattlegroundAI.InteractYards;
        return new PlayerbotBattlegroundFacts
        {
            InReach = reach,
            Unable = !player.IsInWorld || !player.IsAlive || teleporting,
            HelloSent = t.HelloSent,
            JoinSent = t.JoinSent,
            QueueSlot = state.SlotOf(queue) >= 0,
            InvitedInstance = invited,
            MatchHoldsCap = invited != 0 && PlayerbotBattlegroundDemand.MustDecline(BotsIn(manager.GetBattleground(invited, t.Type), invited, t.BotId, player.Guid),
                _options.Battlegrounds.MaxBotsPerBattleground),
            QueuedTooLong = t.Phase == PlayerbotBattlegroundPhase.Queued
                && unchecked(Now - t.QueuedSinceMs) >= (uint)Math.Clamp(_options.Battlegrounds.MaxQueueWaitMinutes, 1, 240) * 60_000u,
            Taken = !bot.Free || InOtherGroup(player),
            Arrived = arrived,
            Bound = bound,
            MatchOver = match is null || match.Status == BattlegroundStatus.WaitLeave,
            BackInWorld = player.IsInWorld && !teleporting && !state.InBattleground && player.Map?.Template is not { IsBattleground: true },
            InPhaseMs = unchecked(Now - t.PhaseSinceMs),
            MatchGone = bound && match is null,
            TravelRefused = t.Phase == PlayerbotBattlegroundPhase.Travel && t.AI?.TravelRefused == true,
        };
    }

    /// <summary>
    /// Whether the bot is in a group that is not a battleground raid, or holds a group invitation: a real player's party (or anyone's) has it,
    /// from the moment the invitation is accepted. The party AI's own flag follows only at the bot's next think (a budgeted rotation), too late
    /// for a port that goes out this tick.
    /// </summary>
    private bool InOtherGroup(Player player)
        => GroupManagerOrNull() is { } groups && (groups.GetGroup(player.Guid) is { IsBattlegroundGroup: false } || groups.GetInvite(player.Guid) is not null);

    /// <summary>The managed bots in <paramref name="match"/> (participants) and the followed bots porting into it, <paramref name="except"/> not counted.</summary>
    private int BotsIn(Battleground? match, uint instanceId, Guid except, ObjectGuid exceptGuid)
    {
        int count = 0;
        if (match is not null && _world is { } world)
            foreach (ObjectGuid guid in match.Players)
                if (guid != exceptGuid && world.FindOnlinePlayer(guid) is { Session: WorldSession { IsManaged: true } }) count++;
        foreach (Tracked other in _tracked.Values)
            if (other.BotId != except && other.Phase == PlayerbotBattlegroundPhase.Entering && other.InstanceId == instanceId) count++;
        return count;
    }

    private void Do(Tracked t, PlayerbotBattlegroundBot bot, Player player, BattlegroundFeature feature, PlayerbotBattlegroundAction action,
        PlayerbotBattlegroundFacts facts)
    {
        uint map = BattlegroundManager.MapOfType(t.Type);
        switch (action)
        {
            case PlayerbotBattlegroundAction.Hello:
                Act(bot.Session, WorldOpcode.CmsgBattlemasterHello, PlayerbotNavigation.GuidPayload(t.Battlemaster.Value));
                t.HelloSent = true;
                break;

            case PlayerbotBattlegroundAction.Join:
            {
                // CMSG_BATTLEMASTER_JOIN: u64 battlemaster, u32 map, u32 instance (0: the first one free), u8 join as group (BattlegroundPackets).
                var writer = new PacketWriter(17);
                writer.WriteUInt64(t.Battlemaster.Value);
                writer.WriteUInt32(map);
                writer.WriteUInt32(0);
                writer.WriteByte(0);
                Act(bot.Session, WorldOpcode.CmsgBattlemasterJoin, writer.ToArray());
                t.JoinSent = true;
                break;
            }

            case PlayerbotBattlegroundAction.PortEnter:
                t.InstanceId = facts.InvitedInstance;
                Port(bot.Session, map, enter: true);
                Totals = (Totals.Queued, Totals.Entered + 1, Totals.Left, Totals.Declined, Totals.QueueTimeouts);
                Note($"{t.Name} takes the invitation into {t.Type} match {t.InstanceId}");
                break;

            case PlayerbotBattlegroundAction.PortLeave:
                Port(bot.Session, map, enter: false);
                if (facts.MatchHoldsCap && facts.InvitedInstance != 0)
                {
                    Totals = (Totals.Queued, Totals.Entered, Totals.Left, Totals.Declined + 1, Totals.QueueTimeouts);
                    Note($"{t.Name} declines {t.Type} match {facts.InvitedInstance}: it holds {_options.Battlegrounds.MaxBotsPerBattleground} bots already");
                }
                else if (facts.QueuedTooLong)
                {
                    SetAside(t.BotId, t.Type);
                    Totals = (Totals.Queued, Totals.Entered, Totals.Left, Totals.Declined, Totals.QueueTimeouts + 1);
                    Note($"{t.Name} leaves the {t.Type} queue after {_options.Battlegrounds.MaxQueueWaitMinutes} minutes");
                }
                else Note($"{t.Name} leaves the {t.Type} queue: something else drives it now");
                break;

            case PlayerbotBattlegroundAction.LeaveBattlefield:
                Leave(t, bot, player);
                Totals = (Totals.Queued, Totals.Entered, Totals.Left + 1, Totals.Declined, Totals.QueueTimeouts);
                Note(facts.Taken ? $"{t.Name} leaves {t.Type} match {t.InstanceId}: something else has it now" : $"{t.Name} leaves {t.Type} match {t.InstanceId}: it is over");
                break;

            case PlayerbotBattlegroundAction.SetAside:
                SetAside(t.BotId, t.Type);
                Note(facts.TravelRefused ? $"{t.Name} could not walk to the {t.Type} battlemaster: set aside" : $"{t.Name} got no queue slot for {t.Type}: set aside");
                break;

            case PlayerbotBattlegroundAction.Release:
                if (t.Phase == PlayerbotBattlegroundPhase.Travel && facts.InPhaseMs > PlayerbotBattlegroundQueueMachine.TravelTimeoutMs)
                {
                    SetAside(t.BotId, t.Type);
                    Note($"{t.Name} timed out walking to the {t.Type} battlemaster: set aside");
                }
                break;
        }
    }

    private void Leave(Tracked t, PlayerbotBattlegroundBot bot, Player player)
    {
        Act(bot.Session, WorldOpcode.CmsgLeaveBattlefield, BitConverter.GetBytes(player.MapId));
        t.LeaveSentMs = Now;
    }

    /// <summary>CMSG_BATTLEFIELD_PORT: u32 map, u8 action (1 enter, 0 leave the queue or decline).</summary>
    private static void Port(WorldSession session, uint map, bool enter)
    {
        var writer = new PacketWriter(5);
        writer.WriteUInt32(map);
        writer.WriteByte(enter ? (byte)1 : (byte)0);
        Act(session, WorldOpcode.CmsgBattlefieldPort, writer.ToArray());
    }

    /// <summary>The coordinator lets the bot go: its AI drops what it did, and the brain resumes with its quests and memories.</summary>
    private static void Release(Tracked t, PlayerbotBattlegroundBot bot, Player player)
    {
        t.AI?.Release(player);
        bot.Brain.ResumeAfterGroup();
    }

    private void SetAside(Guid botId, BattlegroundType type) => _setAside[(botId, type)] = unchecked(Now + SetAsideMs);

    private bool IsSetAside(Guid botId, BattlegroundType type)
        => _setAside.TryGetValue((botId, type), out uint until) && unchecked((int)(until - Now)) > 0;

    // --- the scan ------------------------------------------------------------------------------------------------------

    private void Scan(BattlegroundFeature feature, uint sinceMs)
    {
        uint now = Now;
        foreach (((Guid, BattlegroundType) key, uint until) in _setAside.ToArray())
            if (unchecked((int)(until - now)) <= 0) _setAside.Remove(key);

        BattlegroundManager manager = feature.Manager;
        BuildDemand(manager);
        AssignRoles(feature);

        PlayerbotBattlegroundOptions options = _options.Battlegrounds;
        float chance = PlayerbotBattlegroundDemand.ChancePerThink(options.QueueChancePerMinute, sinceMs);
        bool anyRealWaiting = _demand.AnyRealWaiting;
        bool checkedRefusalsThisScan = false;
        foreach (PlayerbotBattlegroundBot bot in _bots.Values.OrderBy(b => b.BotId))
        {
            if (_tracked.ContainsKey(bot.BotId) || !bot.Free || !bot.Session.IsManaged || bot.Session.Player is not { IsInWorld: true } player) continue;
            if (Adopt(bot, player, feature)) continue;
            if (!options.Enabled || !MayQueue(player, feature)) continue;
            bool roll = chance > 0f && _random.NextDouble() < chance;
            if (!roll && !anyRealWaiting && checkedRefusalsThisScan) continue;
            checkedRefusalsThisScan = true;
            List<PlayerbotBattlegroundChoice> eligible = PlayerbotBattlegroundBrackets.Eligible(player.Level, manager.TemplateOf,
                type => HasObjectiveContent(feature, type) && (type != BattlegroundType.ArathiBasin || KnowsBannerSpell(player)),
                type => !IsSetAside(bot.BotId, type) && FindMaster(player, type) is not null, Refused);
            if (eligible.Count == 0 || (!roll && !anyRealWaiting)) continue;
            if (_demand.Pick(eligible, player.Team, manager.TemplateOf, options, roll, _random.NextDouble()) is not { } choice
                || FindMaster(player, choice.Type) is not { } master) continue;
            var tracked = new Tracked(bot.BotId, player.Name)
            {
                Phase = PlayerbotBattlegroundPhase.Travel, PhaseSinceMs = now, Type = choice.Type, Bracket = choice.Bracket, Team = player.Team,
                Battlemaster = master.Guid, MasterAt = master.At, AI = new PlayerbotBattlegroundAI(bot.Session, _options),
            };
            _tracked[bot.BotId] = tracked;
            _demand.AddBotCommitted(choice.Type, choice.Bracket, player.Team);
            Note($"{player.Name} goes to the {choice.Type} battlemaster {master.Name}");
        }
    }

    /// <summary>
    /// A free bot already queued for, or bound to, a match of a type the bots play (a world restart, a controller that let it go) is taken
    /// on where it stands: queued, or in its match. True when it was.
    /// </summary>
    private bool Adopt(PlayerbotBattlegroundBot bot, Player player, BattlegroundFeature feature)
    {
        BattlegroundPlayerState state = feature.Manager.StateOf(player.Guid);
        BattlegroundType type = BattlegroundType.None;
        var phase = PlayerbotBattlegroundPhase.Idle;
        if (state.InBattleground && PlayerbotBattlegroundBrackets.Supported(state.Type))
        {
            type = state.Type;
            phase = PlayerbotBattlegroundPhase.Entering;
        }
        else
        {
            foreach (BattlegroundType candidate in PlayerbotBattlegroundBrackets.Types)
                if (PlayerbotBattlegroundBrackets.Supported(candidate) && state.SlotOf((BattlegroundQueueType)(byte)candidate) >= 0)
                {
                    type = candidate;
                    phase = PlayerbotBattlegroundPhase.Queued;
                    break;
                }
        }

        if (phase == PlayerbotBattlegroundPhase.Idle) return false;
        BattlegroundTemplate? template = feature.Manager.TemplateOf(type);
        _tracked[bot.BotId] = new Tracked(bot.BotId, player.Name)
        {
            Phase = phase, PhaseSinceMs = Now, QueuedSinceMs = Now, Type = type, Team = player.Team,
            Bracket = template is null ? -1 : BattlegroundConstants.BracketOfLevel(player.Level, template.MinLevel),
            InstanceId = state.InBattleground ? state.InstanceId : 0, HelloSent = true, JoinSent = true,
            AI = new PlayerbotBattlegroundAI(bot.Session, _options),
        };
        Note($"{player.Name} was already {(phase == PlayerbotBattlegroundPhase.Queued ? "queued for" : "in")} {type}: taken on");
        return true;
    }

    /// <summary>A free bot that may start a queue now: alive, out of combat, on an allowed open-world map, in no group, queue or match, no deserter.</summary>
    private bool MayQueue(Player player, BattlegroundFeature feature)
    {
        if (!player.IsAlive || player.Combat.IsInCombat || player.Map is not { } map) return false;
        if (map.Template is { IsDungeon: true } or { IsBattleground: true }) return false;
        if (_options.AllowedMaps is { Length: > 0 } allowed && !allowed.Contains(map.MapId)) return false;
        if (InOtherGroup(player) || GroupManagerOrNull()?.GetGroup(player.Guid) is not null) return false;
        BattlegroundPlayerState state = feature.Manager.StateOf(player.Guid);
        if (state.InAnyQueue || state.InBattleground) return false;
        return !feature.IsDeserter(player);
    }

    private void Refused(BattlegroundType type, PlayerbotBattlegroundRefusal why)
    {
        // Logged once per type and reason: a level out of range or no battlemaster in reach is an ordinary per-bot fact.
        if (why is PlayerbotBattlegroundRefusal.LevelOutOfRange or PlayerbotBattlegroundRefusal.NoBattlemaster || !_loggedRefusals.Add((type, why))) return;
        Note($"bots do not queue for {type}: {why}");
        logger.LogInformation("Playerbots do not queue for {Type}: {Reason}", type, why);
    }

    // --- the demand snapshot -------------------------------------------------------------------------------------------

    /// <summary>
    /// The real players waiting in each queue (not invited), the bots this coordinator committed (walking, joining, queued, invited,
    /// porting), and the bots of each running match that still has room (a new queuer would be put into it); a match holding the cap in
    /// bots blocks its bracket.
    /// </summary>
    private void BuildDemand(BattlegroundManager manager)
    {
        _demand.Clear();
        if (_world is null) return;
        foreach (Player online in _world.OnlinePlayers)
        {
            if (online.Session is WorldSession { IsManaged: true }) continue;
            foreach (BattlegroundType type in PlayerbotBattlegroundBrackets.Types)
            {
                if (!PlayerbotBattlegroundBrackets.Supported(type) || manager.TemplateOf(type) is not { } template) continue;
                if (manager.QueuedGroupOf(online.Guid, type) is not { InvitedToInstanceId: 0 } group) continue;
                int bracket = BattlegroundConstants.BracketOfLevel(online.Level, template.MinLevel);
                if (bracket >= 0) _demand.AddRealWaiting(type, bracket, group.Team);
            }
        }

        foreach (Tracked t in _tracked.Values)
            if (t.Phase is PlayerbotBattlegroundPhase.Travel or PlayerbotBattlegroundPhase.Join or PlayerbotBattlegroundPhase.Queued
                or PlayerbotBattlegroundPhase.Invited or PlayerbotBattlegroundPhase.Entering)
                _demand.AddBotCommitted(t.Type, t.Bracket, t.Team);

        int cap = _options.Battlegrounds.MaxBotsPerBattleground;
        foreach (Battleground match in manager.RunningBattlegrounds)
        {
            if (!PlayerbotBattlegroundBrackets.Supported(match.Type) || match.Status == BattlegroundStatus.WaitLeave || !match.HasFreeSlots) continue;
            int bots = 0;
            foreach ((ObjectGuid guid, Team team) in match.Participants())
            {
                if (_world.FindOnlinePlayer(guid) is not { Session: WorldSession { IsManaged: true } }) continue;
                bots++;
                _demand.AddBotCommitted(match.Type, match.Bracket, team);
            }

            if (bots >= cap) _demand.BlockBracket(match.Type, match.Bracket);
        }
    }

    /// <summary>The roles of the followed bots in each match, per team (<see cref="PlayerbotBattlegroundRoles.Assign"/>; healers escort first).</summary>
    private void AssignRoles(BattlegroundFeature feature)
    {
        foreach (var team in _tracked.Values.Where(t => t.Phase == PlayerbotBattlegroundPhase.InMatch).GroupBy(t => (t.Type, t.InstanceId, t.Team)))
        {
            var members = new List<(ObjectGuid Guid, bool Healer, Tracked Tracked)>();
            foreach (Tracked t in team)
                if (_bots.TryGetValue(t.BotId, out PlayerbotBattlegroundBot bot) && bot.Session.Player is { } player)
                    members.Add((player.Guid, t.Healer, t));
            Dictionary<ObjectGuid, PlayerbotBattlegroundRole> roles = PlayerbotBattlegroundRoles.Assign(team.Key.Type, members.Select(m => (m.Guid, m.Healer)));
            foreach ((ObjectGuid guid, bool _, Tracked t) in members) t.Role = roles[guid];
        }
    }

    /// <summary>The talent role (vmangos AutoAssignRole) from the spell names the bot knows; reads the book, drains no packet.</summary>
    private static PlayerbotRole TalentRole(WorldSession session, Player player)
    {
        if (session.Services.GetService<SpellFeature>() is not { } feature) return PlayerbotRoles.Assign(player.Class, _ => false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (uint id in feature.Spellbook.GetSpells(player))
            if (feature.System.Store.Get(id) is { } spell) names.Add(spell.Name);
        return PlayerbotRoles.Assign(player.Class, id => feature.System.Store.Get(id) is { } signature && names.Contains(signature.Name));
    }

    // --- content ----------------------------------------------------------------------------------------------------

    /// <summary>A battlemaster spawn: its guid, where it stands, its name and its faction template.</summary>
    private sealed record Battlemaster(ObjectGuid Guid, BattlegroundType Type, Vector3 At, string Name, uint Faction);

    /// <summary>
    /// The nearest battlemaster of <paramref name="type"/> on the bot's map within <see cref="PlayerbotBattlegroundOptions.MaxTravelYards"/> that
    /// the bot may talk to (its faction is not hostile to the bot, as for a trainer: <see cref="PlayerbotTrainerDestinations.CanApproachTrainer"/>).
    /// The battlemasters are the creature spawns whose entry has a <c>battlemaster_entry</c> row (<see cref="BattlegroundFeature.Battlemasters"/>).
    /// </summary>
    private Battlemaster? FindMaster(Player player, BattlegroundType type)
    {
        if (player.Map is null || services.GetService<QuestNpcFeature>() is not { } npcs) return null;
        float reach = Math.Clamp(_options.Battlegrounds.MaxTravelYards, 100, 20_000);
        Battlemaster? best = null;
        float nearest = reach;
        foreach (Battlemaster master in MastersOn(player.MapId))
        {
            if (master.Type != type) continue;
            float distance = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), master.At);
            if (distance > nearest
                || !PlayerbotTrainerDestinations.CanApproachTrainer(player, npcs.Services, npcs.FactionTemplates, master.Faction)
                || PlayerbotNavigation.IsUnreachableOnFoot(player, master.At, out _, out _))
                continue;
            nearest = distance;
            best = master;
        }

        return best;
    }

    private List<Battlemaster> MastersOn(uint mapId)
    {
        if (services.GetService<CreatureWorldFeature>()?.Content is not { } content || Feature() is not { } feature) return [];
        if (!ReferenceEquals(content, _mastersOf) || content.DefinitionsVersion != _mastersVersion)
        {
            _masters.Clear();
            _mastersOf = content;
            _mastersVersion = content.DefinitionsVersion;
        }

        if (_masters.TryGetValue(mapId, out List<Battlemaster>? list)) return list;
        list = [];
        foreach (CreatureSpawn spawn in content.GetSpawns(mapId))
        {
            if (!feature.Battlemasters.TryGetValue(spawn.Entry, out BattlegroundType type) || content.FindTemplate(spawn.Entry) is not { } template) continue;
            if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) continue;
            list.Add(new Battlemaster(ObjectGuid.WithEntry(HighGuid.Unit, spawn.Entry, spawn.Guid), type, new Vector3(spawn.X, spawn.Y, spawn.Z),
                template.Name, template.Faction));
        }

        _masters[mapId] = list;
        return list;
    }

    /// <summary>
    /// Whether the objective a bot plays exists and the server plays it (cached until the object, trigger, spell or battleground content is
    /// reloaded): Warsong Gulch both flag stands on map 489 and both flag room triggers (3646, 3647); Arathi Basin node banners on map 529 (objects
    /// with a <c>gameobject_battleground</c> node event and a lock), every node's neutral banner one the match routes to the node click
    /// (<see cref="ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse"/>: a banner button, or a goober whose spell is a banner trigger spell), and the
    /// banner spell 21651 itself. Without it bots do not queue (fail closed: <see cref="PlayerbotBattlegroundRefusal.MissingContent"/>).
    /// </summary>
    private bool HasObjectiveContent(BattlegroundFeature feature, BattlegroundType type)
    {
        if (_world is null || services.GetService<GameObjectLootFeature>()?.Content is not { } objects) return false;
        WorldMaps maps = WorldMaps.Of(_world);
        object? spells = services.GetService<SpellFeature>()?.System.Store;
        (object?, object?, object?, object?) key = (objects, maps.AreaTriggers, spells, feature.Events);
        if (!Equals(key, _contentOf))
        {
            _content.Clear();
            _contentOf = key;
        }

        if (_content.TryGetValue(type, out bool known)) return known;
        bool present = type switch
        {
            BattlegroundType.WarsongGulch => WarsongContent(objects, maps),
            BattlegroundType.ArathiBasin => ArathiContent(objects, feature),
            _ => false,
        };
        _content[type] = present;
        return present;
    }

    private static bool WarsongContent(GameObjectContent objects, WorldMaps maps)
    {
        IReadOnlyList<GameObjectSpawn> spawns = objects.GetSpawns(BattlegroundManager.MapOfType(BattlegroundType.WarsongGulch));
        return spawns.Any(s => s.Entry == WarsongGulch.AllianceFlagBaseEntry) && spawns.Any(s => s.Entry == WarsongGulch.HordeFlagBaseEntry)
            && maps.FindAreaTrigger(WarsongGulch.AreaTriggerAllianceFlagSpawn) is not null
            && maps.FindAreaTrigger(WarsongGulch.AreaTriggerHordeFlagSpawn) is not null;
    }

    private bool ArathiContent(GameObjectContent objects, BattlegroundFeature feature)
    {
        if (services.GetService<SpellFeature>()?.System.Store.Get(PlayerbotBattlegroundAI.BannerSpell) is null) return false;
        var nodes = new Dictionary<byte, (int Neutral, int Routed)>();
        foreach (GameObjectSpawn spawn in objects.GetSpawns(BattlegroundManager.MapOfType(BattlegroundType.ArathiBasin)))
        {
            if (objects.FindTemplate(spawn.Entry) is not { } template || GameObjectLocks.LockIdOf(template) == 0) continue;
            foreach ((byte node, byte status) in feature.Events.ObjectEvents(spawn.Guid))
            {
                if (node >= ArathiBasin.NodeCount) continue;
                (int neutral, int routed) = nodes.GetValueOrDefault(node);
                if (status == ArathiBasin.StatusNeutral)
                {
                    neutral++;
                    if (ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse(template)) routed++;
                }

                nodes[node] = (neutral, routed);
            }
        }

        // Every node the content has must start from a neutral banner a click on which reaches the match.
        return nodes.Count > 0 && nodes.Values.All(n => n.Neutral > 0 && n.Routed == n.Neutral);
    }

    /// <summary>Whether the bot knows the banner spell 21651 (every new character learns it: <c>playercreateinfo_spell</c>).</summary>
    private static bool KnowsBannerSpell(Player player)
        => player.Session is WorldSession session && session.Services.GetService<SpellFeature>() is { } spells
            && spells.Spellbook.HasSpell(player, PlayerbotBattlegroundAI.BannerSpell);

    // --- helpers --------------------------------------------------------------------------------------------------------

    private BattlegroundFeature? Feature()
    {
        if (services.GetService<BattlegroundFeature>() is not { } feature) return null;
        try
        {
            _ = feature.Manager;
            return feature;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private Game.Groups.GroupManager? GroupManagerOrNull()
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

    /// <summary>A client's own battleground request: through its world handler, never refused for lack of the shared action budget.</summary>
    private static bool Act(WorldSession session, WorldOpcode opcode, byte[] payload)
        => session.IsManaged && Groups.PlayerbotGroupCoordinator.Act(session, opcode, payload, budgeted: false);

    private static float Distance(WorldObject a, WorldObject b)
        => Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z));

    private void Note(string line)
    {
        Events.Add(string.Create(CultureInfo.InvariantCulture, $"{Now / 1000}s {line}"));
        if (Events.Count > 32) Events.RemoveAt(0);
    }
}
