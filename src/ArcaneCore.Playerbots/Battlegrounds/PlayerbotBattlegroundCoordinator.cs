using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
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
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

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
public sealed class PlayerbotBattlegroundCoordinator(IServiceProvider services, ILogger<PlayerbotBattlegroundCoordinator> logger) : IDetachableWorldFeature
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
    private readonly List<PlayerbotBattlegroundBot> _orderedBots = [];
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

    /// <summary>The world ticks this coordinator has seen (the Warsong Gulch plans are made once per match, team and tick).</summary>
    private ulong _tick;

    /// <summary>The Warsong Gulch plan of each match and team, made at most once per tick (<see cref="WarsongOrder"/>).</summary>
    private readonly Dictionary<(uint Instance, Team Team), (ulong Tick, Dictionary<ObjectGuid, PlayerbotWsgOrder> Orders)> _wsgPlans = [];

    /// <summary>
    /// The settled Warsong Gulch midfield of each match and team (<see cref="ResolveWarsongMidfield(Map, Vector3, Vector3, out bool)"/>): only a
    /// point the mesh accepted with every tile read is kept; one resolved while tiles were pending, or a fallback, is asked again next tick.
    /// </summary>
    private readonly Dictionary<(uint Instance, Team Team), Vector3> _wsgMidfields = [];

    /// <summary>The Warsong Gulch flag stand spawns (Alliance, Horde) of the loaded object content (the stands are despawned while their flag is away).</summary>
    private (object? Of, Vector3? Alliance, Vector3? Horde) _wsgStands;

    /// <summary>
    /// The battlemasters whose route was refused from a zone, until when (world ms): a refused route is not walked again from that zone
    /// for <see cref="RefusedMasterMs"/> (live: Gitrol, Silverpine Forest, sent 62 times to the Undercity battlemaster the mesh does not join).
    /// </summary>
    private readonly Dictionary<(ObjectGuid Master, uint Zone), uint> _refusedMasters = [];

    /// <summary>How long a battlemaster whose route was refused is left alone from the bot's zone.</summary>
    internal const uint RefusedMasterMs = 3_600_000;

    /// <summary>What happened so far: queued, entered, left after a match, invitations declined at the cap, queues given up (status and tests).</summary>
    internal (int Queued, int Entered, int Left, int Declined, int QueueTimeouts) Totals { get; private set; }

    /// <summary>What happened last to bots in battlegrounds, newest last (at most 32 lines; status and tests).</summary>
    internal List<string> Events { get; } = [];

    /// <summary>The refusals noted so far, once per type and reason (status and tests).</summary>
    internal IReadOnlyCollection<(BattlegroundType Type, PlayerbotBattlegroundRefusal Why)> Refusals => _loggedRefusals;

    // --- lifecycle (docs/design/hot-reload-tiers.md, section 4.2) -------------------------------------------------------

    /// <summary>
    /// Stateless: a detach lets every followed bot go (a bot in a match is sent CMSG_LEAVE_BATTLEFIELD once, best effort; a queue is left to
    /// time out) and its AI drops what it did; after the next attach the free bots are looked over again.
    /// </summary>
    public DetachKind DetachKind => DetachKind.Stateless;

    /// <summary>Whether the coordinator is attached to a world (a detached one ignores the managed feature's calls).</summary>
    internal bool IsAttached => _world is not null;

    public void Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(nameof(PlayerbotBattlegroundCoordinator)));

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
    }

    public Task QuiesceAsync(CancellationToken cancellationToken) => Task.CompletedTask; // world-thread only: nothing runs in the background

    public void Detach(FeatureHandover handover)
    {
        try
        {
            foreach (Tracked t in _tracked.Values.ToArray())
            {
                if (!_bots.TryGetValue(t.BotId, out PlayerbotBattlegroundBot bot) || bot.Session.Player is not { } player) continue;
                if (t.Phase == PlayerbotBattlegroundPhase.InMatch && _world is not null) Leave(t, bot, player);
                Release(t, bot, player);
            }
        }
        finally
        {
            _tracked.Clear();
            _bots.Clear();
            _orderedBots.Clear();
            _setAside.Clear();
            _wsgPlans.Clear();
            _wsgMidfields.Clear();
            _refusedMasters.Clear();
            _scanElapsed = ScanMs;
            _world = null;
        }
    }

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

        /// <summary>The walk to the battlemaster as planned: its straight distance from where the bot started, scaled for the way round (<see cref="PlayerbotBattlegroundQueueMachine.TravelTimeoutFor"/>).</summary>
        public float TravelYards { get; set; }

        /// <summary>The least distance to the battlemaster so far (negative: none seen yet) and when it was reached: the walk's progress watch (<see cref="PlayerbotBattlegroundQueueMachine.TravelStalled"/>).</summary>
        public float BestYardsToMaster { get; set; } = -1f;
        public uint BestYardsAtMs { get; set; }

        /// <summary>The zone the bot started the walk in: a refusal of the master is for this zone too, not only for the one the walk stalled in.</summary>
        public uint StartZone { get; set; }
        public bool HelloSent { get; set; }
        public bool JoinSent { get; set; }
        public uint QueuedSinceMs { get; set; }
        public uint LeaveSentMs { get; set; }
        public uint InstanceId { get; set; }
        public PlayerbotBattlegroundRole Role { get; set; }

        /// <summary>The bot's talent role is healer (read once, when it enters its match; the roles prefer healers as escorts).</summary>
        public bool Healer { get; set; }

        public PlayerbotBattlegroundAI? AI { get; set; }

        private (BattlegroundType Type, PlayerbotBattlegroundPhase Phase, PlayerbotBattlegroundRole Role, string? Activity) _descKey;
        private string? _desc;

        public string Describe()
        {
            string? activity = Phase == PlayerbotBattlegroundPhase.InMatch ? (AI?.Activity ?? "idle") : null;
            var key = (Type, Phase, Role, activity);
            if (_desc is not null && _descKey == key) return _desc;
            _descKey = key;
            string phase = Phase.ToString().ToLowerInvariant();
            return _desc = Phase == PlayerbotBattlegroundPhase.InMatch
                ? string.Create(CultureInfo.InvariantCulture, $"bg={Type}:{phase}:{Role.ToString().ToLowerInvariant()}:{activity}")
                : string.Create(CultureInfo.InvariantCulture, $"bg={Type}:{phase}");
        }
    }

    internal bool NeedsBotSnapshot(uint elapsedMs)
        => _tracked.Count > 0 || unchecked(_scanElapsed + elapsedMs) >= ScanMs;

    internal void AdvanceIdle(uint elapsedMs)
    {
        if (_world is null) return;
        _tick++;
        if (Feature() is null) return;
        _scanElapsed = unchecked(_scanElapsed + elapsedMs);
    }

    /// <summary>
    /// One world tick (world thread): every followed bot's queue machine (its replies are a client's own and never wait for the action
    /// budget), and every <see cref="ScanMs"/> the demand snapshot, the roles in the running matches and the free bots' choices.
    /// </summary>
    internal void Update(IReadOnlyList<PlayerbotBattlegroundBot> bots, uint elapsedMs)
    {
        if (_world is null) return; // detached
        _bots.Clear();
        _bots.EnsureCapacity(bots.Count);
        _orderedBots.Clear();
        _orderedBots.EnsureCapacity(bots.Count);
        _tick++;
        bool ordered = true;
        Guid prev = Guid.Empty;
        foreach (PlayerbotBattlegroundBot bot in bots)
        {
            _bots[bot.BotId] = bot;
            _orderedBots.Add(bot);
            if (ordered && bot.BotId.CompareTo(prev) < 0) ordered = false;
            prev = bot.BotId;
        }
        if (!ordered) _orderedBots.Sort(static (a, b) => a.BotId.CompareTo(b.BotId));
        if (Feature() is not { } feature) return;
        if (_tracked.Count > 0)
        {
            _gone.Clear();
            foreach (Guid id in _tracked.Keys)
                if (!_bots.ContainsKey(id)) _gone.Add(id);
            foreach (Guid gone in _gone) _tracked.Remove(gone);
            _advancing.Clear();
            _advancing.AddRange(_tracked.Values);
            foreach (Tracked tracked in _advancing) Advance(tracked, feature);
        }
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
    internal bool Drives(Guid botId) => _tracked.Count > 0 && _tracked.TryGetValue(botId, out Tracked? t) && PlayerbotBattlegroundQueueMachine.Drives(t.Phase);

    /// <summary>Whether <paramref name="botId"/> has battleground business (any phase but idle): other coordinators leave it alone.</summary>
    internal bool Holds(Guid botId) => _tracked.Count > 0 && _tracked.ContainsKey(botId);

    /// <summary>The followed bot (tests and status).</summary>
    internal Tracked? Find(Guid botId) => _tracked.Count > 0 ? _tracked.GetValueOrDefault(botId) : null;

    /// <summary>One driven tick of a bot (world thread, within the shared action budget like the brain).</summary>
    internal void UpdateMember(Guid botId, Player player, uint elapsedMs)
    {
        if (_tracked.Count == 0 || !_tracked.TryGetValue(botId, out Tracked? t) || t.AI is not { } ai || !_bots.TryGetValue(botId, out PlayerbotBattlegroundBot bot)
            || !bot.Session.IsManaged) return;
        switch (t.Phase)
        {
            case PlayerbotBattlegroundPhase.Travel:
                // The master is met where it stands now: a waypoint walker (spawns 190500 and 190516 of the emissary 14991, game event 20) leaves its spawn point.
                // The goal follows the live creature once it moved more than a few yards, so the walk is not replanned at every step.
                if (player.Map?.FindObject(t.Battlemaster) is Creature { IsAlive: true } live)
                    FollowMaster(t, new Vector3(player.X, player.Y, player.Z), new Vector3(live.X, live.Y, live.Z));
                ai.Travel(player, t.MasterAt, elapsedMs);
                break;
            case PlayerbotBattlegroundPhase.InMatch when Feature()?.BattlegroundOf(player.Guid) is { } match && match.PlayerTeam(player.Guid) is { } team:
                ai.Play(player, match, team, t.Role, elapsedMs, match is WarsongGulch wsg ? WarsongOrder(wsg, team, player) : null);
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
        => _tracked.Count > 0 && _tracked.TryGetValue(botId, out Tracked? t) && PlayerbotBattlegroundQueueMachine.Drives(t.Phase)
            ? (t.AI?.Goal ?? PlayerbotGoalKind.Battleground, t.AI?.TargetEntry ?? 0) : null;

    /// <summary>A bot stopped, became scripted or left the world: forget its battleground business (the server keeps its queue or match).</summary>
    internal void Forget(Guid botId) => _tracked.Remove(botId);

    /// <summary>The battleground part of a bot's status line: <c>bg=WarsongGulch:inmatch:attacker:taking-flag</c>, or null.</summary>
    internal string? Describe(Guid botId)
        => _tracked.Count == 0 || !_tracked.TryGetValue(botId, out Tracked? t) ? null : t.Describe();

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
        Do(t, bot, player, feature, before, next, action, facts);
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
        // A walk that does not close on the master for minutes is refused like one the navigation refused (the existing refusal path).
        bool stalled = t.Phase == PlayerbotBattlegroundPhase.Travel && player.IsInWorld
            && WatchTravel(t, new Vector3(player.X, player.Y, player.Z), PlayerbotBattlegroundAI.IsAttacked(player), Now);

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
            TravelRefused = t.Phase == PlayerbotBattlegroundPhase.Travel && (t.AI?.TravelRefused == true || stalled),
            TravelYards = t.TravelYards,
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

    private void Do(Tracked t, PlayerbotBattlegroundBot bot, Player player, BattlegroundFeature feature,
        PlayerbotBattlegroundPhase before, PlayerbotBattlegroundPhase next, PlayerbotBattlegroundAction action,
        PlayerbotBattlegroundFacts facts)
    {
        uint map = BattlegroundManager.MapOfType(t.Type);
        var queue = (BattlegroundQueueType)(byte)t.Type;
        switch (action)
        {
            case PlayerbotBattlegroundAction.Hello:
            {
                bool sent = Act(bot.Session, WorldOpcode.CmsgBattlemasterHello, PlayerbotNavigation.GuidPayload(t.Battlemaster.Value));
                t.HelloSent = true;
                if (!sent && !bot.Session.IsInChaosProbe)
                    PlayerbotAnomalies.Report(player, "bg", "queue-action-failed",
                        string.Create(CultureInfo.InvariantCulture, $"battlemaster hello for {t.Type} ({t.Battlemaster}) was not dispatched"));
                break;
            }

            case PlayerbotBattlegroundAction.Join:
            {
                // CMSG_BATTLEMASTER_JOIN: u64 battlemaster, u32 map, u32 instance (0: the first one free), u8 join as group (BattlegroundPackets).
                var writer = new PacketWriter(17);
                writer.WriteUInt64(t.Battlemaster.Value);
                writer.WriteUInt32(map);
                writer.WriteUInt32(0);
                writer.WriteByte(0);
                bool sent = Act(bot.Session, WorldOpcode.CmsgBattlemasterJoin, writer.ToArray());
                t.JoinSent = true;
                if (!sent && !bot.Session.IsInChaosProbe)
                    PlayerbotAnomalies.Report(player, "bg", "queue-action-failed",
                        string.Create(CultureInfo.InvariantCulture, $"battlemaster join for {t.Type} ({t.Battlemaster}) was not dispatched"));
                break;
            }

            case PlayerbotBattlegroundAction.PortEnter:
            {
                t.InstanceId = facts.InvitedInstance;
                bool sent = Port(bot.Session, map, enter: true);
                BattlegroundPlayerState postState = feature.Manager.StateOf(player.Guid);
                if (!bot.Session.IsInChaosProbe && (!sent || (!postState.InBattleground && postState.InvitedInstance(queue) == facts.InvitedInstance)))
                {
                    PlayerbotAnomalies.Report(player, "bg", "invite-not-accepted",
                        $"battleground invite port to {t.Type} match {t.InstanceId} was not accepted");
                }
                Totals = (Totals.Queued, Totals.Entered + 1, Totals.Left, Totals.Declined, Totals.QueueTimeouts);
                Note($"{t.Name} takes the invitation into {t.Type} match {t.InstanceId}");
                break;
            }

            case PlayerbotBattlegroundAction.PortLeave:
            {
                bool hadOpponentOrStillQueued = HasOpposingQueueActivity(t, player, feature.Manager);
                bool sent = Port(bot.Session, map, enter: false);
                bool stillQueued = feature.Manager.StateOf(player.Guid).SlotOf(queue) >= 0;
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
                    if (!bot.Session.IsInChaosProbe && (hadOpponentOrStillQueued || !sent || stillQueued))
                    {
                        PlayerbotAnomalies.Report(player, "bg", "queue-stuck",
                            $"battleground queue for {t.Type} (bracket {t.Bracket}) timed out after {_options.Battlegrounds.MaxQueueWaitMinutes}m");
                    }
                }
                else Note($"{t.Name} leaves the {t.Type} queue: something else drives it now");
                break;
            }

            case PlayerbotBattlegroundAction.LeaveBattlefield:
                Leave(t, bot, player);
                Totals = (Totals.Queued, Totals.Entered, Totals.Left + 1, Totals.Declined, Totals.QueueTimeouts);
                Note(facts.Taken ? $"{t.Name} leaves {t.Type} match {t.InstanceId}: something else has it now" : $"{t.Name} leaves {t.Type} match {t.InstanceId}: it is over");
                break;

            case PlayerbotBattlegroundAction.SetAside:
                SetAside(t.BotId, t.Type);
                if (facts.TravelRefused) RefuseMaster(t, player);
                Note(facts.TravelRefused ? $"{t.Name} could not walk to the {t.Type} battlemaster: set aside" : $"{t.Name} got no queue slot for {t.Type}: set aside");
                if (!bot.Session.IsInChaosProbe)
                {
                    if (facts.TravelRefused)
                        PlayerbotAnomalies.Report(player, "bg", "objective-unreachable", $"could not walk to {t.Type} battlemaster {t.Battlemaster}");
                    else
                        PlayerbotAnomalies.Report(player, "bg", "queue-stuck", $"battlemaster join for {t.Type} (bracket {t.Bracket}) produced no queue slot");
                }
                break;

            case PlayerbotBattlegroundAction.Release:
                if (t.Phase == PlayerbotBattlegroundPhase.Travel && facts.InPhaseMs > PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(facts.TravelYards))
                {
                    SetAside(t.BotId, t.Type);
                    RefuseMaster(t, player);
                    Note($"{t.Name} timed out walking to the {t.Type} battlemaster: set aside");
                    if (!bot.Session.IsInChaosProbe)
                        PlayerbotAnomalies.Report(player, "bg", "objective-unreachable", $"timed out walking to {t.Type} battlemaster {t.Battlemaster}");
                }
                else if (!bot.Session.IsInChaosProbe)
                {
                    if (before == PlayerbotBattlegroundPhase.Invited && next == PlayerbotBattlegroundPhase.Idle && !facts.Taken)
                    {
                        PlayerbotAnomalies.Report(player, "bg", "invite-not-accepted",
                            $"battleground invite for {t.Type} expired or timed out before acceptance");
                    }
                    else if (before == PlayerbotBattlegroundPhase.Entering && next == PlayerbotBattlegroundPhase.Idle && !facts.Arrived)
                    {
                        PlayerbotAnomalies.Report(player, "bg", "invite-not-accepted",
                            $"battleground port to {t.Type} match {t.InstanceId} did not arrive before timeout");
                    }
                }
                break;
        }
    }

    private bool HasOpposingQueueActivity(Tracked t, Player player, BattlegroundManager manager)
    {
        if (t.Bracket < 0 || manager.TemplateOf(t.Type) is not { } template) return false;
        foreach (Battleground match in manager.RunningBattlegrounds)
        {
            if (match.Type == t.Type && match.Bracket == t.Bracket
                && match.Status is BattlegroundStatus.WaitJoin or BattlegroundStatus.InProgress
                && match.FreeSlotsForTeam(t.Team) > 0)
                return true;
        }

        int minPerTeam = (int)Math.Max(1u, template.MinPlayersPerTeam);
        int allianceOnline = 0;
        int hordeOnline = 0;
        if (_world is not null)
        {
            foreach (Player online in _world.OnlinePlayers)
            {
                if (BattlegroundConstants.BracketOfLevel(online.Level, template.MinLevel) != t.Bracket) continue;
                if (manager.QueuedGroupOf(online.Guid, t.Type) is not { InvitedToInstanceId: 0 } group) continue;
                if (group.Team == Team.Horde) hordeOnline++;
                else allianceOnline++;
            }
        }

        return allianceOnline >= minPerTeam && hordeOnline >= minPerTeam;
    }

    private void Leave(Tracked t, PlayerbotBattlegroundBot bot, Player player)
    {
        bool sent = Act(bot.Session, WorldOpcode.CmsgLeaveBattlefield, BitConverter.GetBytes(player.MapId));
        t.LeaveSentMs = Now;
        if (!sent && !bot.Session.IsInChaosProbe)
            PlayerbotAnomalies.Report(player, "bg", "queue-action-failed",
                string.Create(CultureInfo.InvariantCulture, $"leave-battlefield for {t.Type} match {t.InstanceId} was not dispatched"));
    }

    /// <summary>CMSG_BATTLEFIELD_PORT: u32 map, u8 action (1 enter, 0 leave the queue or decline).</summary>
    private static bool Port(WorldSession session, uint map, bool enter)
    {
        var writer = new PacketWriter(5);
        writer.WriteUInt32(map);
        writer.WriteByte(enter ? (byte)1 : (byte)0);
        return Act(session, WorldOpcode.CmsgBattlefieldPort, writer.ToArray());
    }

    /// <summary>The coordinator lets the bot go: its AI drops what it did, and the brain resumes with its quests and memories.</summary>
    private static void Release(Tracked t, PlayerbotBattlegroundBot bot, Player player)
    {
        t.AI?.Release(player);
        bot.Brain.ResumeAfterGroup();
    }

    private void SetAside(Guid botId, BattlegroundType type) => _setAside[(botId, type)] = unchecked(Now + SetAsideMs);

    /// <summary>The walk from the bot's zone to the battlemaster of <paramref name="t"/> failed: no bot walks there for <see cref="RefusedMasterMs"/> from the zone the walk started in (<see cref="Tracked.StartZone"/>: the bots still there stop being sent) or from the one it failed in.</summary>
    private void RefuseMaster(Tracked t, Player player)
    {
        uint until = unchecked(Now + RefusedMasterMs);
        _refusedMasters[(t.Battlemaster, player.ZoneId)] = until;
        _refusedMasters[(t.Battlemaster, t.StartZone)] = until;
    }

    /// <summary>
    /// One look at the walk's progress (<see cref="PlayerbotBattlegroundQueueMachine.TravelStalled"/>): true when the bot at <paramref name="at"/>
    /// has come no closer to the master's goal for the walk's stall time. A fight on the road (<paramref name="fighting"/>) stops the clock.
    /// </summary>
    internal static bool WatchTravel(Tracked t, Vector3 at, bool fighting, uint now)
    {
        float best = t.BestYardsToMaster;
        uint bestAt = t.BestYardsAtMs;
        bool stalled = PlayerbotBattlegroundQueueMachine.TravelStalled(Vector3.Distance(at, t.MasterAt), fighting,
            PlayerbotBattlegroundQueueMachine.TravelStallFor(t.TravelYards), ref best, ref bestAt, now);
        t.BestYardsToMaster = best;
        t.BestYardsAtMs = bestAt;
        return stalled;
    }

    /// <summary>
    /// The walk's goal follows the live battlemaster at <paramref name="live"/>: once it is more than <see cref="MasterMovedYards"/> from the goal
    /// (a bot within <see cref="NearMasterYards"/> of it follows every half yard, so that its stop within the interaction distance is a stop
    /// within reach of the creature, not of where it was). The progress watch's mark moves with the goal: its best distance is shifted by the
    /// change of the bot's distance to the goal (clamped at zero) and its clock is left alone, so a goal pacing away and back from a bot that
    /// stands still is still a stall, while a goal that walks the road away from a bot that closes on it is no stall.
    /// </summary>
    internal static void FollowMaster(Tracked t, Vector3 at, Vector3 live)
    {
        float threshold = Vector3.Distance(at, live) <= NearMasterYards ? NearMasterMovedYards : MasterMovedYards;
        if (Vector3.Distance(live, t.MasterAt) <= threshold) return;
        float change = Vector3.Distance(at, live) - Vector3.Distance(at, t.MasterAt);
        t.MasterAt = live;
        if (t.BestYardsToMaster >= 0f && float.IsFinite(change)) t.BestYardsToMaster = MathF.Max(0f, t.BestYardsToMaster + change);
    }

    /// <summary>Whether the walk from <paramref name="zone"/> to <paramref name="master"/> failed lately.</summary>
    internal bool IsMasterRefused(ObjectGuid master, uint zone)
        => _refusedMasters.TryGetValue((master, zone), out uint until) && unchecked((int)(until - Now)) > 0;

    private bool IsSetAside(Guid botId, BattlegroundType type)
        => _setAside.TryGetValue((botId, type), out uint until) && unchecked((int)(until - Now)) > 0;

    // --- the scan ------------------------------------------------------------------------------------------------------

    private void Scan(BattlegroundFeature feature, uint sinceMs)
    {
        uint now = Now;
        foreach (((Guid, BattlegroundType) key, uint until) in _setAside.ToArray())
            if (unchecked((int)(until - now)) <= 0) _setAside.Remove(key);
        foreach (((ObjectGuid, uint) key, uint until) in _refusedMasters.ToArray())
            if (unchecked((int)(until - now)) <= 0) _refusedMasters.Remove(key);
        foreach (KeyValuePair<(uint, Team), (ulong Tick, Dictionary<ObjectGuid, PlayerbotWsgOrder> Orders)> plan in _wsgPlans.ToArray())
            if (_tick - plan.Value.Tick > 1) _wsgPlans.Remove(plan.Key);
        foreach ((uint, Team) key in _wsgMidfields.Keys.ToArray())
            if (!_wsgPlans.ContainsKey(key)) _wsgMidfields.Remove(key);

        BattlegroundManager manager = feature.Manager;
        BuildDemand(manager);
        AssignRoles(feature);

        PlayerbotBattlegroundOptions options = _options.Battlegrounds;
        float chance = PlayerbotBattlegroundDemand.ChancePerThink(options.QueueChancePerMinute, sinceMs);
        bool anyRealWaiting = _demand.AnyRealWaiting || (options.PreferFillingRealPlayerQueues && options.QueueChancePerMinute > 0f && _demand.AnyBotWaiting);
        bool checkedRefusalsThisScan = false;
        foreach (PlayerbotBattlegroundBot bot in _orderedBots)
        {
            if (_tracked.ContainsKey(bot.BotId) || !bot.Free || !bot.Session.IsManaged || bot.Session.Player is not { IsInWorld: true } player) continue;
            if (Adopt(bot, player, feature)) continue;
            if (bot.Brain.Stay is not null) continue; // waiting for an event scenario: not queued
            if (!options.Enabled || !MayQueue(player, feature)) continue;
            bool roll = chance > 0f && _random.NextDouble() < chance;
            if (!roll && !anyRealWaiting && checkedRefusalsThisScan) continue;
            checkedRefusalsThisScan = true;
            List<PlayerbotBattlegroundChoice> eligible = PlayerbotBattlegroundBrackets.Eligible(player.Level, manager.TemplateOf,
                type => HasObjectiveContent(feature, type) && (type is not (BattlegroundType.ArathiBasin or BattlegroundType.AlteracValley) || KnowsBannerSpell(player)),
                type => !IsSetAside(bot.BotId, type) && FindMaster(player, type) is not null, Refused);
            if (eligible.Count == 0 || (!roll && !anyRealWaiting)) continue;
            if (_demand.Pick(eligible, player.Team, manager.TemplateOf, options, roll, _random.NextDouble()) is not { } choice
                || FindMaster(player, choice.Type) is not { } master) continue;
            var tracked = new Tracked(bot.BotId, player.Name)
            {
                Phase = PlayerbotBattlegroundPhase.Travel, PhaseSinceMs = now, Type = choice.Type, Bracket = choice.Bracket, Team = player.Team,
                Battlemaster = master.Guid, MasterAt = master.At, StartZone = player.ZoneId, AI = new PlayerbotBattlegroundAI(bot.Session, _options),
                TravelYards = Vector3.Distance(new Vector3(player.X, player.Y, player.Z), master.At) * PlayerbotBattlegroundQueueMachine.TravelDetourFactor,
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
            if (!PlayerbotBattlegroundBrackets.Supported(match.Type) || match.Status == BattlegroundStatus.WaitLeave) continue;
            _demand.AddMatchInBracket(match.Type, match.Bracket);
            if (!match.HasFreeSlots) continue;
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
            foreach ((ObjectGuid guid, bool _, Tracked t) in members)
            {
                t.Role = roles[guid];
                t.AI?.SetTeamRoles(roles);   // the team plan of the match (Arathi Basin) orders exactly these bots
            }
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

    // --- Warsong Gulch plans ---------------------------------------------------------------------------------------

    /// <summary>
    /// The Warsong Gulch order of <paramref name="player"/> this tick (<see cref="PlayerbotWarsongTactics.Plan"/>). The plan of the bot's match
    /// and team is made once per tick and shared by its bots: one pass over the match's participants and the team's followed bots, never a
    /// scan per bot think.
    /// </summary>
    internal PlayerbotWsgOrder? WarsongOrder(WarsongGulch wsg, Team team, Player player)
    {
        (uint, Team) key = (wsg.InstanceId, team);
        if (!_wsgPlans.TryGetValue(key, out (ulong Tick, Dictionary<ObjectGuid, PlayerbotWsgOrder> Orders) plan) || plan.Tick != _tick)
        {
            plan = (_tick, BuildWarsongPicture(wsg, team, player) is { } picture ? PlayerbotWarsongTactics.Plan(picture) : []);
            _wsgPlans[key] = plan;
        }

        return plan.Orders.TryGetValue(player.Guid, out PlayerbotWsgOrder order) ? order : null;
    }

    /// <summary>What the plan reads about <paramref name="team"/>'s side of <paramref name="wsg"/>, from the bot's map (null: no flag room or stands known).</summary>
    private PlayerbotWsgPicture? BuildWarsongPicture(WarsongGulch wsg, Team team, Player anyBot)
    {
        if (_world is null || anyBot.Map is not { } map) return null;
        Team enemy = BattlegroundConstants.OtherTeam(team);
        WorldMaps maps = WorldMaps.Of(_world);
        uint ownTrigger = team == Team.Alliance ? WarsongGulch.AreaTriggerAllianceFlagSpawn : WarsongGulch.AreaTriggerHordeFlagSpawn;
        if (maps.FindAreaTrigger(ownTrigger) is not { } room || WarsongStands() is not ({ } allianceStand, { } hordeStand)) return null;
        Vector3 ownRoom = new(room.X, room.Y, room.Z);
        Vector3 enemyStand = enemy == Team.Alliance ? allianceStand : hordeStand;
        Vector3 ownStand = team == Team.Alliance ? allianceStand : hordeStand;
        if (!_wsgMidfields.TryGetValue((wsg.InstanceId, team), out Vector3 middle))
        {
            middle = ResolveWarsongMidfield(map, ownStand, enemyStand, out bool settled);
            if (settled) _wsgMidfields[(wsg.InstanceId, team)] = middle;
        }

        var bots = new List<PlayerbotWsgMember>();
        foreach (Tracked t in _tracked.Values)
        {
            if (t.Phase != PlayerbotBattlegroundPhase.InMatch || t.InstanceId != wsg.InstanceId || t.Team != team
                || !_bots.TryGetValue(t.BotId, out PlayerbotBattlegroundBot bot) || bot.Session.Player is not { IsInWorld: true } p
                || !ReferenceEquals(p.Map, map) || wsg.PlayerTeam(p.Guid) != team) continue;
            // A walking bot sends a movement heartbeat every 500 ms, while a route change flushes its packet position at once; reading
            // the live route position keeps two bots that left a fight together from leapfrogging each other in the team plan and trading
            // TakeEnemyFlag and Midfield back and forth until the loop watch blacklists both objectives.
            bots.Add(new PlayerbotWsgMember(p.Guid, PlayerbotMotion.CurrentPosition(p), p.IsAlive && (p.Flags & PlayerFlags.Ghost) == 0, t.Healer));
        }

        var enemies = new List<PlayerbotWsgEnemy>();
        foreach ((ObjectGuid guid, Team side) in wsg.Participants())
            if (side == enemy && _world.FindOnlinePlayer(guid) is { IsInWorld: true, IsAlive: true } e && (e.Flags & PlayerFlags.Ghost) == 0 && ReferenceEquals(e.Map, map))
                enemies.Add(new PlayerbotWsgEnemy(guid, PlayerbotMotion.CurrentPosition(e)));

        Vector3? At(ObjectGuid guid) => guid.IsEmpty ? null : map.FindObject(guid) is WorldObject o && ReferenceEquals(o.Map, map) ? new Vector3(o.X, o.Y, o.Z) : null;
        uint enemyStandEntry = enemy == Team.Alliance ? WarsongGulch.AllianceFlagBaseEntry : WarsongGulch.HordeFlagBaseEntry;
        bool enemyReady = wsg.FlagState(enemy) == WsgFlagState.OnBase
            && wsg.IsActiveEvent(enemy == Team.Alliance ? WarsongGulch.EventFlagAlliance : WarsongGulch.EventFlagHorde, 0);
        ObjectGuid standObject = default;
        if (enemyReady && map.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            // Once per match, team and tick (the stand object is respawned with the flag, so its guid is looked up again).
            float best = float.MaxValue;
            foreach (GameObject go in objects.GameObjects)
            {
                if (go.Entry != enemyStandEntry || !go.IsSpawned) continue;
                float distance = Vector3.Distance(new Vector3(go.X, go.Y, go.Z), enemyStand);
                if (distance < best)
                {
                    best = distance;
                    standObject = go.Guid;
                }
            }
        }

        ObjectGuid enemyCarrier = wsg.FlagState(team) == WsgFlagState.OnPlayer ? wsg.FlagPicker(team) : default;
        ObjectGuid ownCarrier = wsg.FlagState(enemy) == WsgFlagState.OnPlayer ? wsg.FlagPicker(enemy) : default;
        return new PlayerbotWsgPicture
        {
            Team = team,
            Bots = bots,
            Enemies = enemies,
            OwnFlag = wsg.FlagState(team),
            OwnFlagDropped = wsg.FlagState(team) == WsgFlagState.OnGround ? At(wsg.DroppedFlagGuid(team)) : null,
            EnemyFlag = wsg.FlagState(enemy),
            EnemyFlagReady = enemyReady,
            EnemyStandObject = standObject,
            EnemyFlagDropped = wsg.FlagState(enemy) == WsgFlagState.OnGround ? At(wsg.DroppedFlagGuid(enemy)) : null,
            EnemyCarrier = enemyCarrier,
            EnemyCarrierAt = enemyCarrier.IsEmpty ? null : _world.FindOnlinePlayer(enemyCarrier) is { IsInWorld: true } ec && ReferenceEquals(ec.Map, map) ? PlayerbotMotion.CurrentPosition(ec) : null,
            OwnCarrier = ownCarrier,
            OwnCarrierAt = ownCarrier.IsEmpty ? null : _world.FindOnlinePlayer(ownCarrier) is { IsInWorld: true } oc && ReferenceEquals(oc.Map, map) ? PlayerbotMotion.CurrentPosition(oc) : null,
            OwnRoom = ownRoom,
            EnemyStand = enemyStand,
            Midfield = middle,
        };
    }

    /// <summary><see cref="ResolveWarsongMidfield(Map, Vector3, Vector3, out bool)"/> without the settled answer.</summary>
    internal static Vector3 ResolveWarsongMidfield(Map map, Vector3 ownStand, Vector3 enemyStand)
        => ResolveWarsongMidfield(map, ownStand, enemyStand, out _);

    /// <summary>
    /// The middle of the Warsong Gulch field the bots hold: the point between the two flag stands, snapped to the ground and then to the
    /// walkable mesh (the nearest ring point that passes <see cref="CheckWarsongMidfield"/>; failing all, the nearest walkable point on the way
    /// to the bot's own stand, else the stand itself). With a mesh, a point the mesh was never asked about is not returned: the answer is one
    /// the mesh accepted, or a fallback. <paramref name="settled"/> is false for a fallback (the walk back to the own stand, and the stand: the
    /// walkable check reads no tile, so a tile not resident yet answers "not walkable" like a hole) and whenever a tile read was waited for on
    /// the way; the caller does not keep such an answer: it asks again once the tiles are loaded.
    /// </summary>
    internal static Vector3 ResolveWarsongMidfield(Map map, Vector3 ownStand, Vector3 enemyStand, out bool settled)
    {
        long pendingMark = PlayerbotNavigation.PendingMark;
        Vector3 middle = (ownStand + enemyStand) / 2f;
        float probeZ = MathF.Max(ownStand.Z, enemyStand.Z) + 5f;
        float terrain = map.Terrain.GetHeight(middle.X, middle.Y, middle.Z);
        float ground = terrain > Game.Maps.Terrain.TerrainTile.InvalidHeight && float.IsFinite(terrain)
            ? map.Collision.GetHeight(middle.X, middle.Y, terrain + 2f, maxSearchDistance: 6f)
            : map.Collision.GetHeight(middle.X, middle.Y, probeZ);
        if (terrain > Game.Maps.Terrain.TerrainTile.InvalidHeight && float.IsFinite(terrain)
            && (ground <= Game.Maps.Terrain.TerrainTile.InvalidHeight || !float.IsFinite(ground) || ground > terrain + Game.Maps.Collision.GroundHeight.PropHeightYards))
        {
            ground = terrain;
        }

        if (ground > Game.Maps.Terrain.TerrainTile.InvalidHeight && float.IsFinite(ground))
        {
            if (map.Collision.TryGetMeshFloor(middle.X, middle.Y, ground, 20f, 4f, out float meshZ) && float.IsFinite(meshZ) && meshZ < ground - 1.0f)
                ground = meshZ;
            middle.Z = ground;
        }

        if (map.Collision.MeshPoints is null)
        {
            // No mesh to ask (yet): the snapped point is all there is. It costs no query and is asked again each tick, as before.
            settled = false;
            return middle;
        }

        MidfieldCheck first = CheckWarsongMidfield(map, middle, ownStand);
        if (first != MidfieldCheck.NotWalkable)
        {
            settled = first == MidfieldCheck.Walkable && !PlayerbotNavigation.PendingSince(pendingMark);
            return middle;
        }

        ReadOnlySpan<float> radii = [5f, 9f, 14f, 20f, 28f];
        foreach (float radius in radii)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.PI / 4f;
                Vector3 candidate = SnapToGround(map, middle.X + (radius * MathF.Cos(angle)), middle.Y + (radius * MathF.Sin(angle)), middle.Z);
                MidfieldCheck check = CheckWarsongMidfield(map, candidate, ownStand);
                if (check == MidfieldCheck.NotWalkable) continue;
                settled = check == MidfieldCheck.Walkable && !PlayerbotNavigation.PendingSince(pendingMark);
                return candidate;
            }
        }

        // No point around the middle passed the reach probe, and the middle itself is off the mesh: it is never returned. Walk back along the
        // line to the bot's own stand for the nearest ground the mesh accepts, and fall back to the stand (a walkable place by construction).
        ReadOnlySpan<float> fractions = [0.05f, 0.1f, 0.15f, 0.2f, 0.3f, 0.4f, 0.5f, 0.65f, 0.8f];
        foreach (float fraction in fractions)
        {
            Vector3 candidate = SnapToGround(map, middle.X + ((ownStand.X - middle.X) * fraction), middle.Y + ((ownStand.Y - middle.Y) * fraction),
                middle.Z + ((ownStand.Z - middle.Z) * fraction));
            if (!PlayerbotNavigation.OnWalkableMesh(map, candidate)) continue;
            settled = false;
            return candidate;
        }

        settled = false;
        return ownStand;
    }

    /// <summary>The ground at (<paramref name="x"/>, <paramref name="y"/>) near <paramref name="z"/>: terrain, then the collision floor, then the mesh floor.</summary>
    private static Vector3 SnapToGround(Map map, float x, float y, float z)
    {
        float candZ = z;
        float candTerrain = map.Terrain.GetHeight(x, y, candZ);
        float candGround = candTerrain > Game.Maps.Terrain.TerrainTile.InvalidHeight && float.IsFinite(candTerrain)
            ? map.Collision.GetHeight(x, y, candTerrain + 2f, maxSearchDistance: 6f)
            : map.Collision.GetHeight(x, y, candZ + 6f, maxSearchDistance: 12f);
        if (candGround > Game.Maps.Terrain.TerrainTile.InvalidHeight && float.IsFinite(candGround))
            candZ = candGround;
        if (map.Collision.TryGetMeshFloor(x, y, candZ, 12f, 4f, out float candMeshZ) && float.IsFinite(candMeshZ))
            candZ = candMeshZ;
        return new Vector3(x, y, candZ);
    }

    /// <summary>What the mesh says of a midfield candidate (<see cref="CheckWarsongMidfield"/>).</summary>
    private enum MidfieldCheck : byte
    {
        NotWalkable,
        Walkable,

        /// <summary>A tile of the probe is still being read: no verdict yet.</summary>
        Pending,
    }

    private static MidfieldCheck CheckWarsongMidfield(Map map, Vector3 candidate, Vector3 ownStand)
    {
        if (!PlayerbotNavigation.OnWalkableMesh(map, candidate))
            return MidfieldCheck.NotWalkable;
        Vector2 toOwn = new(ownStand.X - candidate.X, ownStand.Y - candidate.Y);
        Vector2 dir = toOwn.LengthSquared() > 1f ? Vector2.Normalize(toOwn) : new Vector2(1f, 0f);
        float probeX = candidate.X + (dir.X * 18f);
        float probeY = candidate.Y + (dir.Y * 18f);
        float probeTerrain = map.Terrain.GetHeight(probeX, probeY, candidate.Z);
        float probeZ = probeTerrain > Game.Maps.Terrain.TerrainTile.InvalidHeight && float.IsFinite(probeTerrain) ? probeTerrain : candidate.Z;
        var probe = new Vector3(probeX, probeY, probeZ);
        PathResult reach = PlayerbotThinkProbe.Find(map.Collision, candidate, probe, new PathOptions
        {
            MaxPoints = 32,
            Mover = PathMover.Player,
            ExcludeFlags = NavTerrain.SteepSlopes,
            AllowPartial = true,
            MaxSearchNodes = 256,
            LoadTiles = true,
        });
        if ((reach.Type & PathType.TilesPending) != 0)
            return MidfieldCheck.Pending;
        if (!reach.HasPath || (reach.Type & PathType.NoPath) != 0)
            return MidfieldCheck.NotWalkable;
        if ((reach.Type & PathType.Incomplete) != 0
            && Vector2.Distance(new Vector2(reach.End.X, reach.End.Y), new Vector2(candidate.X, candidate.Y)) < 10f)
            return MidfieldCheck.NotWalkable;
        return MidfieldCheck.Walkable;
    }

    /// <summary>Where the two Warsong Gulch flag stands are spawned (cached per object content).</summary>
    private (Vector3? Alliance, Vector3? Horde) WarsongStands()
    {
        if (services.GetService<GameObjectLootFeature>()?.Content is not { } objects) return (null, null);
        if (!ReferenceEquals(_wsgStands.Of, objects))
        {
            Vector3? alliance = null, horde = null;
            foreach (GameObjectSpawn spawn in objects.GetSpawns(BattlegroundManager.MapOfType(BattlegroundType.WarsongGulch)))
            {
                if (spawn.Entry == WarsongGulch.AllianceFlagBaseEntry) alliance ??= new Vector3(spawn.X, spawn.Y, spawn.Z);
                else if (spawn.Entry == WarsongGulch.HordeFlagBaseEntry) horde ??= new Vector3(spawn.X, spawn.Y, spawn.Z);
            }

            _wsgStands = (objects, alliance, horde);
        }

        return (_wsgStands.Alliance, _wsgStands.Horde);
    }

    // --- content ----------------------------------------------------------------------------------------------------

    /// <summary>A battlemaster spawn: its guid, where it stands, its name and its faction template.</summary>
    private sealed record Battlemaster(ObjectGuid Guid, uint SpawnGuid, BattlegroundType Type, Vector3 At, string Name, uint Faction, bool Walks);

    /// <summary>The distance within which a battlemaster's absence from the map is trusted (well inside the visibility range).</summary>
    internal const float MasterPresenceYards = 60f;

    /// <summary>How far the live battlemaster has to be from the goal of the walk before the goal moves to it.</summary>
    private const float MasterMovedYards = 3f;

    /// <summary>Within this distance of the bot the goal follows the live battlemaster by <see cref="NearMasterMovedYards"/> (under the interaction distance less the stop margin).</summary>
    private const float NearMasterYards = 12f;

    /// <summary>How far a battlemaster near the bot has to move from the goal for the goal to follow it.</summary>
    private const float NearMasterMovedYards = 0.5f;

    /// <summary>
    /// The nearest battlemaster of <paramref name="type"/> on the bot's map within <see cref="PlayerbotBattlegroundOptions.MaxTravelYards"/> that
    /// the bot may talk to (its faction is not hostile to the bot, as for a trainer: <see cref="PlayerbotTrainerDestinations.CanApproachTrainer"/>)
    /// and can walk to. The battlemasters are the creature spawns whose entry has a <c>battlemaster_entry</c> row (<see cref="BattlegroundFeature.Battlemasters"/>).
    /// <para>
    /// Nearest is by straight line (ties by spawn guid, so the answer does not depend on the spawn order), but a master that stands still
    /// (spawn movement type not waypoint) is taken over a nearer one that walks its waypoints (the emissary 14991 spawns 190500 and 190516 under game
    /// event 20 walk away from their spawn points, and the bot has to be within reach of the creature itself). A master is only taken when the walk is
    /// possible by the region graph alone (<see cref="PlayerbotNavigation.IsInOtherWalkingRegion"/>: Teldrassil's island against the mainland;
    /// a master or a bot indoors is let through, the indoor climb has its escapes). No path search: the walk itself is planned in legs
    /// (<see cref="PlayerbotNavigation.PlanToward"/>) and a route that fails there refuses the master for the zone (<see cref="RefuseMaster"/>).
    /// Known false negative: the 16x size leniency of <see cref="PlayerbotNavigation.IsUnreachableOnFoot"/> is not applied, so a bot on a
    /// medium plateau bounded by steep slopes (a component under a sixteenth of the master's, but no fragment) gets <c>NoBattlemaster</c> though
    /// transport or an escape might reach the master.
    /// </para>
    /// </summary>
    private Battlemaster? FindMaster(Player player, BattlegroundType type)
    {
        if (player.Map is not { } map || services.GetService<QuestNpcFeature>() is not { } npcs) return null;
        float reach = Math.Clamp(_options.Battlegrounds.MaxTravelYards, 100, 20_000);
        Battlemaster? still = null, walking = null;
        float stillAt = reach, walkingAt = reach;
        // A spawn a game event holds back is not in the world while its event is off (the Warsong Emissary, 15105, of game event 19 "Call
        // to Arms: Warsong Gulch"; vmangos GameEventMgr GameEventSpawn/GameEventUnspawn): walking to it finds nobody.
        ArcaneCore.Game.WorldState.Events.ISpawnGate? gate = map.FindUpdater<CreatureMapSystem>()?.SpawnGate;
        var here = new Vector3(player.X, player.Y, player.Z);
        foreach (Battlemaster master in MastersOn(player.MapId))
        {
            if (master.Type != type) continue;
            if (gate is not null && !gate.AllowsCreature(master.SpawnGuid)) continue;
            if (IsMasterRefused(master.Guid, player.ZoneId)) continue;
            float distance = Vector3.Distance(here, master.At);
            // Within sight of the spawn point the map says whether the master is there: an emissary whose event the gate still allows but
            // that is not in the world (or is dead) had bots walk up, hold 5 s and report 'no living battlemaster' (live: 15105 Undercity, 15102 Ironforge).
            if (distance <= MasterPresenceYards && map.FindObject(master.Guid) is not Creature { IsAlive: true }) continue;
            float limit = master.Walks ? walkingAt : stillAt;
            Battlemaster? held = master.Walks ? walking : still;
            // Strictly nearer, or as near with the lower spawn guid.
            if (distance > limit || (distance == limit && held is not null && master.SpawnGuid >= held.SpawnGuid)) continue;
            if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, npcs.Services, npcs.FactionTemplates, master.Faction)
                || PlayerbotNavigation.IsInOtherWalkingRegion(player, master.At))
                continue;
            if (master.Walks)
            {
                walking = master;
                walkingAt = distance;
            }
            else
            {
                still = master;
                stillAt = distance;
            }
        }

        return still ?? walking;
    }

    /// <summary>Test hook, not for production code: the battlemaster <see cref="FindMaster"/> picks (spawn guid and spawn point).</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal (uint SpawnGuid, Vector3 At)? FindMasterForTests(Player player, BattlegroundType type)
        => FindMaster(player, type) is { } master ? (master.SpawnGuid, master.At) : null;

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
            list.Add(new Battlemaster(ObjectGuid.WithEntry(HighGuid.Unit, spawn.Entry, spawn.Guid), spawn.Guid, type, new Vector3(spawn.X, spawn.Y, spawn.Z),
                template.Name, template.Faction, spawn.MovementType == (byte)CreatureMovementType.Waypoint));
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
    internal bool HasObjectives(BattlegroundType type) => Feature() is { } feature && HasObjectiveContent(feature, type);

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
            BattlegroundType.AlteracValley => AlteracValleyContent(objects, maps, feature),
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

    private bool AlteracValleyContent(GameObjectContent objects, WorldMaps _, BattlegroundFeature feature)
    {
        uint mapId = BattlegroundManager.MapOfType(BattlegroundType.AlteracValley);
        if (feature.Manager.TemplateOf(BattlegroundType.AlteracValley) is not { } template
            || template.MapId != mapId
            || services.GetService<SpellFeature>()?.System.Store.Get(PlayerbotBattlegroundAI.BannerSpell) is null)
            return false;
        int banners = 0, routed = 0;
        foreach (GameObjectSpawn spawn in objects.GetSpawns(mapId))
        {
            if (objects.FindTemplate(spawn.Entry) is not { } goTemplate || GameObjectLocks.LockIdOf(goTemplate) == 0) continue;
            foreach ((byte node, byte _) in feature.Events.ObjectEvents(spawn.Guid))
            {
                if (node >= AlteracValley.NodeCount) continue;
                banners++;
                if (ArcaneCore.World.Battlegrounds.MatchRuntime.RoutesBannerUse(goTemplate)) routed++;
            }
        }

        return banners == 0 || routed > 0;
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
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.bg-coordinator", ex);
            return null;
        }
    }

    private Game.Groups.GroupManager? GroupManagerOrNull()
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Groups;
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.bg-coordinator", ex);
            return null;
        }
    }

    /// <summary>A client's own battleground request: through its world handler, never refused for lack of the shared action budget.</summary>
    private static bool Act(WorldSession session, WorldOpcode opcode, byte[] payload)
        => session.IsManaged && Groups.PlayerbotGroupCoordinator.Act(session, opcode, payload, budgeted: false);

    private void Note(string line)
    {
        Events.Add(string.Create(CultureInfo.InvariantCulture, $"{Now / 1000}s {line}"));
        if (Events.Count > 32) Events.RemoveAt(0);
    }
}
