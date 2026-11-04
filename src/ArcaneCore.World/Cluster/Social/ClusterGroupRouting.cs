using System.Text;
using System.Threading.Channels;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// The group authority as a worker reaches it: the holder's <see cref="GroupAuthority"/> in process (<see cref="PresenceHolderService"/>) or over the peer link
/// (<see cref="PeerPresenceClient"/>). Blocking members: never call on the world thread.
/// </summary>
public interface IGroupAuthorityTransport
{
    /// <summary>Apply <paramref name="command"/> of a character served by <paramref name="workerId"/> at the authority.</summary>
    void ExecuteGroupCommand(string workerId, GroupCommand command);

    /// <summary>One bounded batch of the events queued for <paramref name="workerId"/>; empty when there is none.</summary>
    IReadOnlyList<GroupEvent> DrainGroupEvents(string workerId);

    /// <summary>
    /// True when a call can be made now: this node holds the authority, or it has a live link to the node that does. False is the normal state before the peer link connects (and
    /// while the holder is away): callers wait instead of calling, so nothing throws. A link that drops between this check and the call may still throw. Never blocks.
    /// A transport that is always in process returns true.
    /// </summary>
    bool IsAuthorityReachable { get; }
}

/// <summary>
/// Encoding of group commands and events for the peer link (carried inside <see cref="PresenceOp.GroupCommand"/> and <see cref="PresenceOp.GroupDrain"/>). Little-endian
/// <see cref="BinaryWriter"/> fields, strings length-prefixed UTF-8, lists an int32 count. Decoders throw <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/>
/// on a frame that does not fit. Pure functions; thread-safe.
/// </summary>
internal static class GroupWireCodec
{
    private const int MaxList = 4096;
    private const int MaxBytes = 64 * 1024;

    public static void WriteCommand(BinaryWriter w, GroupCommand c)
    {
        w.Write((byte)c.Kind);
        w.Write(c.Actor);
        w.Write(c.ActorName);
        w.Write(c.ActorTeam);
        w.Write(c.ActorIsGameMaster);
        w.Write(c.TargetGuid);
        w.Write(c.TargetName);
        w.Write(c.OtherName);
        w.Write(c.Value);
        w.Write(c.Value2);
        w.Write(c.Master);
        WriteBytes(w, c.Payload ?? []);
    }

    public static GroupCommand ReadCommand(BinaryReader r)
    {
        var kind = (GroupCommandKind)r.ReadByte();
        ulong actor = r.ReadUInt64();
        string actorName = r.ReadString();
        byte team = r.ReadByte();
        bool gm = r.ReadBoolean();
        ulong targetGuid = r.ReadUInt64();
        string targetName = r.ReadString();
        string otherName = r.ReadString();
        uint value = r.ReadUInt32();
        uint value2 = r.ReadUInt32();
        ulong master = r.ReadUInt64();
        byte[] payload = ReadBytes(r);
        return new GroupCommand(kind, actor, actorName, team, gm, targetGuid, targetName, otherName, value, value2, master, payload.Length == 0 ? null : payload);
    }

    public static void WriteEvents(BinaryWriter w, IReadOnlyList<GroupEvent> events)
    {
        w.Write(events.Count);
        foreach (GroupEvent e in events)
        {
            w.Write(e.RecipientGuid);
            w.Write(e.Replica is not null);
            if (e.Replica is { } replica)
            {
                w.Write(replica.GroupId);
                w.Write(replica.IsRaid);
                w.Write(replica.Leader);
                w.Write(replica.LeaderName);
                w.Write(replica.LootMethod);
                w.Write(replica.Looter);
                w.Write(replica.LootThreshold);
                w.Write(replica.TargetIcons.Count);
                foreach (ulong icon in replica.TargetIcons)
                {
                    w.Write(icon);
                }

                w.Write(replica.Members.Count);
                foreach (GroupReplicaMember m in replica.Members)
                {
                    w.Write(m.Guid);
                    w.Write(m.Name);
                    w.Write(m.SubGroup);
                    w.Write(m.Assistant);
                }
            }

            w.Write(e.DepartedGroupId);
            w.Write(e.Disbanded);
            w.Write(e.InviterGuid);
            w.Write(e.InviteCancelled);
            w.Write(e.Packets.Count);
            foreach (GroupPacket p in e.Packets)
            {
                w.Write((ushort)p.Opcode);
                WriteBytes(w, p.Payload);
            }
        }
    }

    public static IReadOnlyList<GroupEvent> ReadEvents(BinaryReader r)
    {
        int count = ReadCount(r);
        var events = new List<GroupEvent>(count);
        for (int i = 0; i < count; i++)
        {
            ulong recipient = r.ReadUInt64();
            GroupReplica? replica = null;
            if (r.ReadBoolean())
            {
                uint id = r.ReadUInt32();
                bool raid = r.ReadBoolean();
                ulong leader = r.ReadUInt64();
                string leaderName = r.ReadString();
                byte method = r.ReadByte();
                ulong looter = r.ReadUInt64();
                byte threshold = r.ReadByte();
                int iconCount = ReadCount(r);
                var icons = new ulong[iconCount];
                for (int k = 0; k < iconCount; k++)
                {
                    icons[k] = r.ReadUInt64();
                }

                int memberCount = ReadCount(r);
                var members = new GroupReplicaMember[memberCount];
                for (int k = 0; k < memberCount; k++)
                {
                    members[k] = new GroupReplicaMember(r.ReadUInt64(), r.ReadString(), r.ReadByte(), r.ReadBoolean());
                }

                replica = new GroupReplica(id, raid, leader, leaderName, method, looter, threshold, icons, members);
            }

            uint departed = r.ReadUInt32();
            bool disbanded = r.ReadBoolean();
            ulong inviter = r.ReadUInt64();
            bool inviteCancelled = r.ReadBoolean();
            int packetCount = ReadCount(r);
            var packets = new GroupPacket[packetCount];
            for (int k = 0; k < packetCount; k++)
            {
                packets[k] = new GroupPacket((WorldOpcode)r.ReadUInt16(), ReadBytes(r));
            }

            events.Add(new GroupEvent(recipient, replica, departed, disbanded, inviter, packets, inviteCancelled));
        }

        return events;
    }

    private static void WriteBytes(BinaryWriter w, byte[] bytes)
    {
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader r)
    {
        int length = r.ReadInt32();
        if (length < 0 || length > MaxBytes)
        {
            throw new InvalidDataException($"byte block of {length} B (at most {MaxBytes})");
        }

        byte[] bytes = r.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return bytes;
    }

    private static int ReadCount(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > MaxList)
        {
            throw new InvalidDataException($"list of {count} items (at most {MaxList})");
        }

        return count;
    }
}

/// <summary>
/// Group operations between workers (M15.2 S3a). The group manager of this worker hands every command to <see cref="Forward"/> (an enqueue, safe on the world thread); one background
/// task sends them in order to the group authority at the Social lease holder (<see cref="IGroupAuthorityTransport"/>), and the pump (<see cref="PumpOnce"/>, every interval after
/// <see cref="Start"/>) drains the events queued for this worker and hands them to <see cref="Deliver"/>, which the social feature posts to the world thread. A command whose send
/// fails once the authority is reachable (link dropped mid-call, refusal) is dropped with a log line, as chat is.
/// <para>
/// While <see cref="IGroupAuthorityTransport.IsAuthorityReachable"/> is false (the peer link is not up yet, or the holder is away) the sender holds the oldest command and the rest
/// wait behind it in the queue, in order. Transient <see cref="GroupCommandKind.Relay"/> commands (party stats, pings, rolls) are never held: <see cref="Forward"/> drops them
/// while the authority is unreachable and counts them in <see cref="RelayDropped"/>, so they cannot crowd out a Logout or Leave. Every queued command carries its enqueue time; one
/// older than <see cref="MaxHeldCommandAge"/> when it comes to be sent is dropped and counted in <see cref="Expired"/> (this bounds a stale Leave or Kick after a long outage; a
/// startup hold is far shorter). The queue holds at most <see cref="MaxQueuedCommands"/> commands; when an outage fills it the newest are dropped (those already waiting keep their
/// order) and counted in <see cref="QueueDropped"/>. The first command to wait logs once at Information, and the outage logs once more when it ends, with the number of commands
/// dropped for each reason; the count of commands still waiting there is approximate.
/// </para>
/// <para>Ownership: owns the sender and pump tasks; <see cref="DisposeAsync"/> stops both (a hold ends at once and the commands still held are discarded, with one log line).
/// Thread-safety: all members are safe from any thread. <see cref="Forward"/> reads <see cref="IGroupAuthorityTransport.IsAuthorityReachable"/> for a relay command, which takes a short lock
/// in the lease keeper (never blocks on the network).</para>
/// </summary>
public sealed class ClusterGroupRouting : IGroupAuthorityLink, IAsyncDisposable
{
    /// <summary>Most commands waiting for the sender task.</summary>
    public const int MaxQueuedCommands = 4096;

    /// <summary>Most batches one <see cref="PumpOnce"/> pass takes from the holder.</summary>
    public const int MaxPumpBatches = 64;

    /// <summary>The longest a command may wait before it is sent; an older one is dropped (see the class remarks).</summary>
    public static readonly TimeSpan MaxHeldCommandAge = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan HoldPoll = TimeSpan.FromMilliseconds(25);

    private sealed record Queued(GroupCommand Command, long EnqueuedAt);

    private readonly IGroupAuthorityTransport _transport;
    private readonly string _workerId;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Channel<Queued> _queue = Channel.CreateBounded<Queued>(
        new BoundedChannelOptions(MaxQueuedCommands) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private Task _pump = Task.CompletedTask;
    private Action<GroupEvent>? _deliver;
    private long _queueDropped;
    private long _sendFailed;
    private long _relayDropped;
    private long _expired;
    private int _started;
    private int _disposed;

    // Sender-task state of the current outage (only the sender task touches these).
    private bool _resumePending;
    private long _outageQueueDropped;
    private long _outageRelayDropped;
    private long _outageExpired;

    /// <param name="time">The clock commands are aged with (<see cref="TimeProvider.GetTimestamp"/>); null takes the system clock.</param>
    public ClusterGroupRouting(IGroupAuthorityTransport transport, string workerId, ILogger? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        _transport = transport;
        _workerId = workerId;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _sender = Task.Run(RunSenderAsync, CancellationToken.None);
    }

    /// <summary>This worker's id.</summary>
    public string WorkerId => _workerId;

    /// <summary>Where events go (the social feature posts them to the world thread). While null the pump leaves them at the authority.</summary>
    public Action<GroupEvent>? Deliver
    {
        get => Volatile.Read(ref _deliver);
        set => Volatile.Write(ref _deliver, value);
    }

    /// <summary>Commands dropped because the queue was full.</summary>
    public long QueueDropped => Interlocked.Read(ref _queueDropped);

    /// <summary>Commands dropped because the authority could not be reached or refused them.</summary>
    public long SendFailed => Interlocked.Read(ref _sendFailed);

    /// <summary>Relay commands (stats, pings, rolls) dropped because the authority was unreachable.</summary>
    public long RelayDropped => Interlocked.Read(ref _relayDropped);

    /// <summary>Commands dropped at send time because they had waited longer than <see cref="MaxHeldCommandAge"/>.</summary>
    public long Expired => Interlocked.Read(ref _expired);

    public void Forward(GroupCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind == GroupCommandKind.Relay && !_transport.IsAuthorityReachable)
        {
            Interlocked.Increment(ref _relayDropped); // transient: a late ping or roll is worse than none
            return;
        }

        if (!_queue.Writer.TryWrite(new Queued(command, _time.GetTimestamp())))
        {
            Interlocked.Increment(ref _queueDropped);
        }
    }

    /// <summary>
    /// Completes when the sender has passed every command queued before the call: each was sent, or dropped (refused, failed, expired), or, after disposal, discarded. During a hold
    /// that is only once the authority is reachable again (or the routing is disposed). When the queue is full the marker cannot be queued and the call completes at once without
    /// waiting for anything. For tests and orderly shutdown.
    /// </summary>
    public async Task FlushAsync()
    {
        // A marker the sender recognises by reference (never sent): it is dequeued behind every command queued before it.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_flushWaiters)
        {
            _flushWaiters.Add(done);
        }

        if (!_queue.Writer.TryWrite(FlushMarker))
        {
            done.TrySetResult();
        }

        await done.Task.ConfigureAwait(false);
    }

    private static readonly Queued FlushMarker = new(new GroupCommand(GroupCommandKind.Login, 0, string.Empty, 0, false), 0);
    private readonly List<TaskCompletionSource> _flushWaiters = [];

    private async Task RunSenderAsync()
    {
        ChannelReader<Queued> reader = _queue.Reader;
        int inHand = 0;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (!_stop.IsCancellationRequested && reader.TryRead(out Queued? queued))
                {
                    if (ReferenceEquals(queued, FlushMarker))
                    {
                        ReportResume(reader);
                        ReleaseFlushWaiters();
                        continue;
                    }

                    GroupCommand command = queued.Command;
                    if (!_transport.IsAuthorityReachable)
                    {
                        if (command.Kind == GroupCommandKind.Relay)
                        {
                            Interlocked.Increment(ref _relayDropped); // queued before the link went away
                            continue;
                        }

                        inHand = 1;
                        await HoldUntilReachableAsync(reader, command).ConfigureAwait(false);
                        inHand = 0;
                    }

                    if (_time.GetElapsedTime(queued.EnqueuedAt) > MaxHeldCommandAge)
                    {
                        Interlocked.Increment(ref _expired);
                        _outageExpired++;
                        _logger.LogDebug("Group command {Kind} of {Actor} dropped: it waited longer than {Seconds} s", command.Kind, command.Actor, (int)MaxHeldCommandAge.TotalSeconds);
                        continue;
                    }

                    ReportResume(reader);
                    try
                    {
                        _transport.ExecuteGroupCommand(_workerId, command);
                    }
                    catch (Exception ex) when (IsTransportFailure(ex))
                    {
                        Interlocked.Increment(ref _sendFailed);
                        _logger.LogWarning("Group command {Kind} of {Actor} dropped ({Message})", command.Kind, command.Actor, ex.Message);
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                    {
                        Interlocked.Increment(ref _sendFailed);
                        _logger.LogFault("cluster.group-send", ex, "A group command failed at the authority; it is dropped");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            int discarded = inHand;
            while (reader.TryRead(out Queued? rest))
            {
                if (!ReferenceEquals(rest, FlushMarker))
                {
                    discarded++;
                }
            }

            if (discarded > 0)
            {
                _logger.LogInformation("Group routing stopped: {Count} held commands were discarded", discarded);
            }

            ReleaseFlushWaiters(); // a hold ended by disposal must not leave a FlushAsync caller waiting
        }
    }

    private void ReleaseFlushWaiters()
    {
        lock (_flushWaiters)
        {
            foreach (TaskCompletionSource waiter in _flushWaiters)
            {
                waiter.TrySetResult();
            }

            _flushWaiters.Clear();
        }
    }

    /// <summary>
    /// Waits, on the sender task only, until the authority is reachable. <paramref name="held"/> is the command in hand (the oldest one); the others wait in the queue behind it, so
    /// order is kept. Logs once when the wait starts; the matching resume line is written by <see cref="ReportResume"/> once the backlog has been walked (so it can count what expired).
    /// Throws <see cref="OperationCanceledException"/> when the routing is disposed.
    /// </summary>
    private async Task HoldUntilReachableAsync(ChannelReader<Queued> reader, GroupCommand held)
    {
        // A future lease takeover that ends a hold must be ordered against SocialGroupPersistenceFeature.ReloadAuthority's Reset: held commands must not reach a table that is being reloaded.
        _outageQueueDropped = QueueDropped;
        _outageRelayDropped = RelayDropped;
        _outageExpired = 0;
        _logger.LogInformation(
            "Group commands are held: this node has no link to the Social holder; starting with {Kind} of {Actor}, {Waiting} more queued",
            held.Kind, held.Actor, reader.Count);
        while (!_transport.IsAuthorityReachable)
        {
            await Task.Delay(HoldPoll, _stop.Token).ConfigureAwait(false);
        }

        _resumePending = true;
    }

    /// <summary>Writes the once-per-outage resume line (if an outage is waiting to be reported): reached when the first fresh command is about to be sent, or the backlog ended.</summary>
    private void ReportResume(ChannelReader<Queued> reader)
    {
        if (!_resumePending)
        {
            return;
        }

        _resumePending = false;
        _logger.LogInformation(
            "Group commands resume: the Social holder is reachable; about {Waiting} commands are still waiting; dropped during the outage: {Expired} older than {Seconds} s, {Dropped} for a full queue, {Relays} relays",
            reader.Count, _outageExpired, (int)MaxHeldCommandAge.TotalSeconds, QueueDropped - _outageQueueDropped, RelayDropped - _outageRelayDropped);
    }

    /// <summary>Drains the events queued for this worker and hands them to <see cref="Deliver"/>; returns how many. Blocking when the authority is remote: never call on the world thread.</summary>
    public int PumpOnce()
    {
        Action<GroupEvent>? deliver = Deliver;
        if (deliver is null || !_transport.IsAuthorityReachable)
        {
            return 0; // not linked to the holder yet: nothing to drain, and nothing is thrown for it
        }

        int handed = 0;
        for (int batch = 0; batch < MaxPumpBatches; batch++)
        {
            IReadOnlyList<GroupEvent> events = _transport.DrainGroupEvents(_workerId);
            if (events.Count == 0)
            {
                break;
            }

            foreach (GroupEvent e in events)
            {
                deliver(e);
                handed++;
            }
        }

        return handed;
    }

    /// <summary>Starts the pump: <see cref="PumpOnce"/> every <paramref name="interval"/>. Idempotent.</summary>
    public void Start(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _pump = Task.Run(() => RunPumpAsync(interval, _stop.Token), CancellationToken.None);
    }

    private async Task RunPumpAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    PumpOnce();
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    _logger.LogDebug("Group event pump deferred ({Message})", ex.Message);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _logger.LogFault("cluster.group-pump", ex, "A group event pump pass failed; the loop continues");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsTransportFailure(Exception ex)
        => ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_sender, _pump).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.group-routing", ex);
        }

        _stop.Dispose();
    }
}
