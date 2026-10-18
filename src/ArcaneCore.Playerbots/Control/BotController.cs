using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>How a bot under <c>.botctl</c> control reacts on its own (docs/ops/botctl.md, "Behaviour modes").</summary>
internal enum BotControlMode : byte
{
    /// <summary>Nothing on its own: it only does what it is told (it still acknowledges server orders like a client).</summary>
    Passive,

    /// <summary>Like passive, but it fights back: with no victim and an attacker on its map it targets and auto-attacks the attacker.</summary>
    Defensive,
}

/// <summary>What a controlled bot's motion task is.</summary>
internal enum BotMotionKind : byte { None, Move, Follow }

/// <summary>Where a controlled bot's motion task stands.</summary>
internal enum BotMotionState : byte { Idle, Planning, Moving, Arrived, Following, Failed, Lost }

/// <summary>One thing a controlled bot saw or did: a server packet decoded (<see cref="BotControlEvents"/>) or a control action.</summary>
internal sealed record BotControlEvent(long Seq, uint TimeMs, DateTimeOffset At, string Bot, string Kind, string Text, bool Error);

/// <summary>
/// The controller <c>.botctl take</c> attaches (<see cref="ManagedPlayerbotFeature.SetControllerAsync"/>): while attached the bot's own
/// <see cref="PlayerbotBrain"/> does not run at all; the bot does what operators and agents tell it, through ordinary client opcodes
/// (<see cref="WorldSession.TryManagedAction"/>, the brain's own path), and walks with the brain's own navigation. It acknowledges the
/// server's teleports and movement orders like a client, records what the server sends it (events, last errors) and runs one motion task
/// (move to a point, follow a unit). World thread, except <see cref="OnPacket"/> (the session's sending thread) and the read-only snapshots
/// (any thread, under the gate).
/// </summary>
internal sealed class BotController : IPlayerbotController
{
    internal const int MaxEvents = 512;
    internal const int MaxErrors = 32;
    private const int MaxInbox = 4096;
    private const uint ThinkMs = 200;
    private const int MaxPlanFailures = 5;
    private const int MaxNoProgress = 4;

    private static long s_sequence;

    private readonly BotControlHub _hub;
    private readonly ConcurrentQueue<(WorldOpcode Opcode, byte[] Payload)> _inbox = new();
    private readonly object _gate = new();
    private readonly LinkedList<BotControlEvent> _events = new();
    private readonly LinkedList<BotControlEvent> _errors = new();
    private readonly List<BotControlSubscriber> _subscribers = [];
    private int _inboxCount;
    private long _droppedPackets;
    private WorldSession? _session;
    private Action<WorldOpcode, byte[]>? _observer;
    private PlayerbotOptions? _options;
    private PlayerbotRoute? _route;
    private TileWait _tileWait;
    private uint _accumulated;
    private Vector3 _plannedFor;
    private float _bestDistance = float.MaxValue;
    private int _noProgress;
    private volatile int _mode;


    internal BotController(BotControlHub hub, Guid botId, string name, string owner)
    {
        _hub = hub;
        BotId = botId;
        Name = name;
        Owner = owner;
        Since = DateTimeOffset.UtcNow;
    }

    public Guid BotId { get; }

    public string Name { get; }

    /// <summary>Who took the bot (the invoker name: a GM, <c>Console</c> or <c>RA:NAME</c>).</summary>
    public string Owner { get; internal set; }

    public DateTimeOffset Since { get; }

    public BotControlMode Mode
    {
        get => (BotControlMode)_mode;
        set => _mode = (int)value;
    }

    /// <summary>Whether the controller was detached (released, replaced, or the bot stopped).</summary>
    public bool IsDetached { get; private set; }

    /// <summary>The bot's session once the controller ticked (world thread).</summary>
    internal WorldSession? Session => _session;

    // --- motion (world thread) ---------------------------------------------------------------------------------

    public BotMotionKind MotionKind { get; private set; }

    public BotMotionState MotionState { get; private set; }

    public Vector3 Destination { get; private set; }

    public float Tolerance { get; private set; } = 1.5f;

    public ObjectGuid FollowTarget { get; private set; }

    public float FollowDistance { get; private set; } = 3f;

    public uint MotionMap { get; private set; }

    public int PlanFailures { get; private set; }

    public string? MotionDetail { get; private set; }

    /// <summary>Counts motion tasks, so a wait for "arrived" is not satisfied by an older one.</summary>
    public int MotionGeneration { get; private set; }

    /// <summary>Whether the route of the motion task is being walked right now.</summary>
    public bool HasRoute => _route is { Complete: false };

    internal void MoveTo(Player player, Vector3 destination, float tolerance)
    {
        StartMotion(player, BotMotionKind.Move);
        Destination = destination;
        Tolerance = Math.Clamp(tolerance, 0.5f, 50f);
    }

    internal void Follow(Player player, ObjectGuid target, float distance)
    {
        StartMotion(player, BotMotionKind.Follow);
        FollowTarget = target;
        FollowDistance = Math.Clamp(distance, 1f, 40f);
    }

    /// <summary>End the motion task and stop where the bot stands.</summary>
    internal void StopMotion(string why)
    {
        bool had = MotionKind != BotMotionKind.None;
        MotionKind = BotMotionKind.None;
        MotionState = BotMotionState.Idle;
        MotionDetail = why;
        _route = null;
        if (_session?.Player is { } player) PlayerbotMovementControl.Stop(_session, player);
        if (had) Record("motion", "stopped: " + why, error: false);
    }

    private void StartMotion(Player player, BotMotionKind kind)
    {
        MotionGeneration++;
        MotionKind = kind;
        MotionState = BotMotionState.Planning;
        MotionMap = player.MapId;
        MotionDetail = null;
        PlanFailures = 0;
        _route = null;
        _tileWait = default;
        _bestDistance = float.MaxValue;
        _noProgress = 0;
        _accumulated = ThinkMs; // plan on the next tick
    }

    private void Finish(BotMotionState state, string detail)
    {
        MotionState = state;
        MotionDetail = detail;
        _route = null;
        if (state != BotMotionState.Following) MotionKind = BotMotionKind.None;
        if (_session?.Player is { } player) PlayerbotMovementControl.Stop(_session, player);
        Record("motion", state.ToString().ToLowerInvariant() + ": " + detail, error: state is BotMotionState.Failed or BotMotionState.Lost);
    }

    // --- IPlayerbotController ----------------------------------------------------------------------------------

    void IPlayerbotController.Tick(PlayerbotControllerContext context, uint elapsedMs)
    {
        if (!ReferenceEquals(_session, context.Session)) Attach(context.Session);
        context.Session.DrainManagedPackets();
        ProcessInbox(context.Session);
        bool acknowledged = context.AcknowledgeServerOrders();
        if (context.Player is not { IsInWorld: true } player) return;
        _accumulated += elapsedMs;
        if (_accumulated < ThinkMs) return;
        uint interval = _accumulated;
        _accumulated = 0;
        if (!acknowledged) UpdateMotion(context, player, interval);
        if (Mode == BotControlMode.Defensive) Defend(context, player);
        PruneSubscribers();
    }

    void IPlayerbotController.Detached(Guid botId)
    {
        IsDetached = true;
        if (_session is { } session && ReferenceEquals(session.ManagedPacketObserver, _observer)) session.ManagedPacketObserver = null;
        Record("control", "released (the bot's own AI drives it again, or it stopped)", error: false);
        _hub.Forget(this);
        lock (_gate) _subscribers.Clear();
    }

    /// <summary>Attach to the bot's session now (an action right after take, before the first tick). World thread.</summary>
    internal void EnsureAttached(WorldSession session)
    {
        if (!ReferenceEquals(_session, session)) Attach(session);
    }

    /// <summary>Turn the packets queued so far into events (an action reads the server's immediate answer). World thread.</summary>
    internal void Pump()
    {
        if (_session is { } session) ProcessInbox(session);
    }

    /// <summary>The last loot window the server opened for the bot (SMSG_LOOT_RESPONSE), for <c>.botctl loot</c>.</summary>
    internal Scenarios.LootResponseView? LastLoot { get; private set; }

    private void Attach(WorldSession session)
    {
        _session = session;
        _options = session.Services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new PlayerbotOptions();
        // The tap sees every packet the bot is sent (it also makes a lean bot build its update blocks while it is controlled).
        _observer ??= OnPacket;
        session.ManagedPacketObserver ??= _observer;
        Record("control", "taken by " + Owner, error: false);
    }

    /// <summary>The session's packet tap: any sending thread, under its send lock, so it only queues.</summary>
    private void OnPacket(WorldOpcode opcode, byte[] payload)
    {
        if (BotControlEvents.IsNoise(opcode)) return;
        if (Interlocked.Increment(ref _inboxCount) > MaxInbox)
        {
            Interlocked.Decrement(ref _inboxCount);
            Interlocked.Increment(ref _droppedPackets);
            return;
        }

        _inbox.Enqueue((opcode, payload));
    }

    private void ProcessInbox(WorldSession session)
    {
        ulong self = session.Player?.Guid.Value ?? 0;
        while (_inbox.TryDequeue(out (WorldOpcode Opcode, byte[] Payload) packet))
        {
            Interlocked.Decrement(ref _inboxCount);
            if (packet.Opcode == WorldOpcode.SmsgLootResponse)
            {
                try
                {
                    LastLoot = Scenarios.ScenarioDecoders.LootResponse(packet.Payload);
                }
                catch (FormatException)
                {
                    LastLoot = null;
                }
            }

            if (BotControlEvents.Describe(packet.Opcode, packet.Payload, self) is { } described)
                Record(described.Kind, described.Text, described.Error);
        }
    }

    // --- motion ------------------------------------------------------------------------------------------------

    private void UpdateMotion(PlayerbotControllerContext context, Player player, uint interval)
    {
        if (MotionKind == BotMotionKind.None || _options is not { } options) return;
        if (player.MapId != MotionMap)
        {
            Finish(BotMotionState.Failed, $"the bot left map {MotionMap} (now on {player.MapId})");
            return;
        }

        if (!PlayerbotMotion.CanMove(player)) return;
        Vector3 destination = Destination;
        if (MotionKind == BotMotionKind.Follow)
        {
            if (player.Map?.FindObject(FollowTarget) is not { } followed || !ReferenceEquals(followed.Map, player.Map))
            {
                Finish(BotMotionState.Lost, $"the follow target {FollowTarget} is not on the bot's map");
                return;
            }

            destination = new Vector3(followed.X, followed.Y, followed.Z);
        }

        Vector3 here = PlayerbotMotion.CurrentPosition(player);
        float distance = Vector2.Distance(new(here.X, here.Y), new(destination.X, destination.Y));
        if (MotionKind == BotMotionKind.Move && distance <= Tolerance)
        {
            Finish(BotMotionState.Arrived, $"at {Fmt(here)} ({distance:F1} yd from the destination)");
            return;
        }

        if (MotionKind == BotMotionKind.Follow)
        {
            if (distance <= FollowDistance)
            {
                if (_route is not null || PlayerbotMotion.IsActive(player)) PlayerbotMovementControl.Stop(context.Session, player);
                _route = null;
                MotionState = BotMotionState.Following;
                MotionDetail = $"within {FollowDistance:F0} yd";
                return;
            }

            if (_route is not null && Vector3.Distance(_plannedFor, destination) > 3f) _route = null;
        }

        if (_route is not { Complete: false } route)
        {
            if (_route is not null)
            {
                // A route walked to its end without arriving: count the walks that brought the bot no nearer.
                if (distance < _bestDistance - 0.5f) { _bestDistance = distance; _noProgress = 0; }
                else if (++_noProgress >= MaxNoProgress && MotionKind == BotMotionKind.Move)
                {
                    Finish(BotMotionState.Failed, $"stuck {distance:F1} yd from the destination at {Fmt(here)}");
                    return;
                }
            }

            uint now = context.World.NowMs;
            PlanOutcome planned = PlayerbotNavigation.PlanExact(player, destination, options, ref _tileWait, now, out PlayerbotRoute? plannedRoute,
                chase: MotionKind == BotMotionKind.Follow);
            if (planned == PlanOutcome.Pending)
            {
                MotionState = BotMotionState.Planning;
                MotionDetail = "waiting for navigation tiles";
                return;
            }

            if (planned != PlanOutcome.Route || plannedRoute is null)
            {
                _route = null;
                PlanFailures++;
                MotionDetail = $"no route ({PlanFailures} refused plans)";
                if (PlanFailures >= MaxPlanFailures) Finish(BotMotionState.Failed, $"no route from {Fmt(here)} to {Fmt(destination)}");
                return;
            }

            _route = route = plannedRoute;
            _plannedFor = destination;
            MotionState = BotMotionState.Moving;
            MotionDetail = $"{distance:F1} yd to go";
        }

        MotionDetail = $"{distance:F1} yd to go";
        if (!PlayerbotNavigation.TryAdvance(context.Session, route, options, interval, context.World.NowMs)) _route = null;
    }

    private void Defend(PlayerbotControllerContext context, Player player)
    {
        if (!player.IsAlive || player.Combat.Victim is not null) return;
        Unit? attacker = player.Combat.Attackers.Where(a => ReferenceEquals(a.Map, player.Map) && a.IsAlive).OrderBy(a => a.Guid.Value).FirstOrDefault();
        if (attacker is null) return;
        byte[] guid = BitConverter.GetBytes(attacker.Guid.Value);
        context.Session.ManagedBudget = null;
        if (context.TryAction(WorldOpcode.CmsgSetSelection, guid) && context.TryAction(WorldOpcode.CmsgAttackswing, guid))
            Record("action", $"defends itself: attacks {attacker.Guid}", error: false);
    }

    internal static string Fmt(Vector3 v) => FormattableString.Invariant($"({v.X:F1}, {v.Y:F1}, {v.Z:F1})");

    // --- events ------------------------------------------------------------------------------------------------

    /// <summary>Record an event (any thread): the ring, the error ring for errors, and every live subscriber.</summary>
    internal BotControlEvent Record(string kind, string text, bool error)
    {
        var evt = new BotControlEvent(Interlocked.Increment(ref s_sequence), _session?.World.NowMs ?? 0, DateTimeOffset.UtcNow, Name, kind, text, error);
        BotControlSubscriber[] subscribers;
        lock (_gate)
        {
            _events.AddLast(evt);
            while (_events.Count > MaxEvents) _events.RemoveFirst();
            if (error)
            {
                _errors.AddLast(evt);
                while (_errors.Count > MaxErrors) _errors.RemoveFirst();
            }

            subscribers = _subscribers.Count == 0 ? [] : [.. _subscribers];
        }

        foreach (BotControlSubscriber subscriber in subscribers) subscriber.Push(evt);
        return evt;
    }

    /// <summary>The newest sequence number handed out (a mark for "events from now on").</summary>
    internal static long CurrentSequence => Interlocked.Read(ref s_sequence);

    internal long DroppedPackets => Interlocked.Read(ref _droppedPackets);

    internal IReadOnlyList<BotControlEvent> Events(long since, int limit, bool errorsOnly = false)
    {
        lock (_gate)
        {
            IEnumerable<BotControlEvent> source = (errorsOnly ? _errors : _events).Where(e => e.Seq > since);
            BotControlEvent[] all = [.. source];
            return all.Length <= limit ? all : all[^limit..];
        }
    }

    internal bool HasEvent(long since, Func<BotControlEvent, bool> match)
    {
        lock (_gate)
        {
            for (LinkedListNode<BotControlEvent>? node = _events.Last; node is not null && node.Value.Seq > since; node = node.Previous)
                if (match(node.Value)) return true;
            return false;
        }
    }

    internal void Subscribe(BotControlSubscriber subscriber)
    {
        lock (_gate)
        {
            _subscribers.RemoveAll(s => s.Key == subscriber.Key);
            _subscribers.Add(subscriber);
        }
    }

    internal bool Unsubscribe(string key)
    {
        lock (_gate) return _subscribers.RemoveAll(s => s.Key == key) > 0;
    }

    internal int SubscriberCount
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    private void PruneSubscribers()
    {
        lock (_gate) _subscribers.RemoveAll(s => s.IsGone);
    }
}

/// <summary>A live event stream of one invoker (<c>.botctl stream</c>): each event is written as one <c>#event</c> line.</summary>
internal sealed class BotControlSubscriber(string key, Action<string> write, CancellationToken lifetime, bool json, bool errorsOnly)
{
    public string Key { get; } = key;

    public bool IsGone => lifetime.IsCancellationRequested;

    public void Push(BotControlEvent evt)
    {
        if (IsGone || (errorsOnly && !evt.Error)) return;
        try
        {
            write("#event " + (json ? BotControlOutput.EventJson(evt).ToJsonString() : BotControlOutput.EventText(evt)));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            // The invoker's sink closed; it is pruned at the next tick.
        }
    }
}