using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Battlegrounds;

// Battleground queues behind the holder (docs/integration/cluster-realm-state.md, S3e lane 8). The survey's rule: a queue is realm-wide, so one node
// holds it. The holder is the designated battleground worker: it runs the one BattlegroundManager (queues, invitations, the client-visible instance
// ids, the matches). Every other worker forwards its players' queue commands to it and applies the notices it sends back; accepting an invitation
// makes the player's worker teleport the player to the match's start location, which that worker's TeleportService hands to the cluster far
// transfer (IClusterFarTeleportRouter) because the battleground map is not one it owns.

/// <summary>A queue command of a player served by another worker (worker to holder). Immutable.</summary>
public abstract record BattlegroundCommand;

/// <summary>CMSG_BATTLEMASTER_JOIN after the worker validated the battlemaster; <paramref name="EntryPoint"/> is the requester's place at the time (the return point).</summary>
public sealed record BattlegroundJoinCommand(BattlegroundJoinRequest Request, BattlegroundEntryPoint? EntryPoint) : BattlegroundCommand;

/// <summary>CMSG_BATTLEFIELD_PORT: accept an invitation or leave the queue.</summary>
public sealed record BattlegroundPortCommand(BattlegroundPortRequest Request) : BattlegroundCommand;

/// <summary>CMSG_BATTLEFIELD_STATUS: poll the queue slots.</summary>
public sealed record BattlegroundStatusCommand(ObjectGuid Player, uint Level) : BattlegroundCommand;

/// <summary>A player entered the world on the worker: the holder learns its team, level and deserter state (group queueing needs them) and where it is served.</summary>
public sealed record BattlegroundLoginCommand(BattlegroundMember Member) : BattlegroundCommand;

/// <summary>A player left the world on the worker: its queue entries go.</summary>
public sealed record BattlegroundLogoutCommand(ObjectGuid Player) : BattlegroundCommand;

/// <summary>
/// The hub saw no sign of a worker for its time to live (the worker crashed or lost its link for good): every player the holder serves for it is logged out, so no queue slot or
/// invitation outlives the worker. The worker's id is the command's sender id. Produced by the hub, never by a worker.
/// </summary>
public sealed record BattlegroundWorkerGoneCommand : BattlegroundCommand;

/// <summary>A message for a player served by another worker (holder to worker). Immutable.</summary>
public abstract record BattlegroundNotice(ObjectGuid Player);

/// <summary>SMSG_BATTLEFIELD_STATUS for a queue slot.</summary>
public sealed record BattlegroundStatusNotice(ObjectGuid Player, uint QueueSlot, BattlegroundStatusSubject Subject, BattlegroundStatus Status, uint Time1, uint Time2)
    : BattlegroundNotice(Player);

/// <summary>SMSG_GROUP_JOINED_BATTLEGROUND.</summary>
public sealed record BattlegroundGroupJoinedNotice(ObjectGuid Player, uint Result) : BattlegroundNotice(Player);

/// <summary>The group-join error message.</summary>
public sealed record BattlegroundJoinErrorNotice(ObjectGuid Player, BattlegroundJoinError Error) : BattlegroundNotice(Player);

/// <summary>"Group queue limit is set to N. You have been queued solo."</summary>
public sealed record BattlegroundQueueLimitNotice(ObjectGuid Player, uint Limit) : BattlegroundNotice(Player);

/// <summary>Resurrect the player and end its taxi flight before the port.</summary>
public sealed record BattlegroundPrepareToPortNotice(ObjectGuid Player) : BattlegroundNotice(Player);

/// <summary>
/// The player accepted: teleport it to the match's start location. <paramref name="MapId"/> is a battleground map the receiving worker does not own,
/// so the teleport becomes a far transfer to the worker that runs the match.
/// </summary>
public sealed record BattlegroundEnterNotice(ObjectGuid Player, uint MapId, uint InstanceId, Team Team, float X, float Y, float Z, float Orientation)
    : BattlegroundNotice(Player);

/// <summary>
/// How a worker reaches the holder's queue (the peer link, or in process in tests). <see cref="Submit"/> must not block: it is called on the world thread.
/// </summary>
public interface IBattlegroundHolderLink
{
    /// <summary>Queue <paramref name="command"/> of a player served by <paramref name="workerId"/> for the holder.</summary>
    void Submit(string workerId, BattlegroundCommand command);
}

/// <summary>
/// How the holder reaches a worker's players. <see cref="Deliver"/> is called on the holder's world thread and must not block; the receiving
/// worker applies the notice on its own world thread (<see cref="BattlegroundFeature.ReceiveNotice"/>).
/// </summary>
public interface IBattlegroundWorkerLink
{
    /// <summary>Send <paramref name="notice"/> to <paramref name="workerId"/>.</summary>
    void Deliver(string workerId, BattlegroundNotice notice);
}

/// <summary>
/// The holder side: executes the commands of every worker against the one <see cref="BattlegroundManager"/> and, as the manager's host, sends
/// everything addressed to a player of another worker as a notice instead of a local packet. Players of this node (and every player in single-process
/// mode, where nothing is registered) go through <c>local</c> unchanged.
/// <para>
/// Thread affinity: <see cref="Submit"/> is safe from any thread (a lock-free queue); everything else runs on the holder's world thread, where
/// <see cref="Pump"/> executes the queued commands in arrival order. State: <c>_players</c> maps a remote player to its worker and facts; entries
/// go on logout and when the player is sent to a match (it is then served by this node).
/// </para>
/// </summary>
internal sealed class BattlegroundClusterQueue(IBattlegroundManagerHost local, Action<ObjectGuid, BattlegroundEntryPoint> storeEntryPoint) : IBattlegroundManagerHost
{
    private const int MaxCommandsPerPump = 1024;

    private sealed record RemotePlayer(string WorkerId, BattlegroundMember Member);

    private readonly ConcurrentQueue<(string WorkerId, BattlegroundCommand Command)> _inbox = new();
    private readonly Dictionary<ObjectGuid, RemotePlayer> _players = [];
    private BattlegroundManager? _manager;
    private BattlegroundEntryPoint? _joinPoint;

    /// <summary>This node's worker id; commands from it are executed like local ones.</summary>
    public string LocalWorkerId { get; set; } = string.Empty;

    /// <summary>The way to the other workers; null (single process) makes every player local.</summary>
    public IBattlegroundWorkerLink? Workers { get; set; }

    /// <summary>Number of remote players the holder knows (diagnostics, tests).</summary>
    public int RemotePlayerCount => _players.Count;

    /// <summary>The manager this host serves (called once, right after the manager is built with this host).</summary>
    public void Bind(BattlegroundManager manager) => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <summary>Queue a worker's command (any thread).</summary>
    public void Submit(string workerId, BattlegroundCommand command)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        ArgumentNullException.ThrowIfNull(command);
        _inbox.Enqueue((workerId, command));
    }

    /// <summary>Execute the queued commands, at most <see cref="MaxCommandsPerPump"/> (world thread). Returns how many ran. A command that throws is recorded and skipped.</summary>
    public int Pump()
    {
        int ran = 0;
        while (ran < MaxCommandsPerPump && _inbox.TryDequeue(out (string WorkerId, BattlegroundCommand Command) item))
        {
            ran++;
            try
            {
                Execute(item.WorkerId, item.Command);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("battleground.cluster-queue", ex);
                AnomalyLedger.Shared.Report("battleground", "feature-fault", "cluster-command", $"error={ex.GetType().Name}: {ex.Message}");
            }
        }

        return ran;
    }

    /// <summary>Apply one command against the manager (world thread).</summary>
    internal void Execute(string workerId, BattlegroundCommand command)
    {
        BattlegroundManager manager = _manager ?? throw new InvalidOperationException("the cluster queue is not bound to a manager");
        bool remote = Workers is not null && workerId != LocalWorkerId;
        switch (command)
        {
            case BattlegroundLoginCommand login:
                Register(workerId, login.Member, remote);
                manager.PlayerLoggedIn(login.Member.Guid, login.Member.Level);
                break;

            case BattlegroundLogoutCommand logout:
                _players.Remove(logout.Player);
                manager.PlayerLoggedOut(logout.Player);
                break;

            case BattlegroundWorkerGoneCommand:
                if (remote)
                {
                    DropWorker(workerId, manager);
                }

                break;

            case BattlegroundStatusCommand status:
                Touch(workerId, status.Player, remote);
                manager.SendStatusReports(status.Player, status.Level);
                break;

            case BattlegroundPortCommand port:
                Touch(workerId, port.Request.Player, remote);
                manager.HandlePort(port.Request);
                break;

            case BattlegroundJoinCommand join:
                Register(workerId, join.Request.Requester, remote);
                BattlegroundJoinRequest request = join.Request with { Group = [.. join.Request.Group.Select(Known)] };
                _joinPoint = remote ? join.EntryPoint : null;
                try
                {
                    manager.JoinQueue(request);
                }
                finally
                {
                    _joinPoint = null;
                }

                break;
        }
    }

    /// <summary>Log out every remote player this holder serves for a worker that is gone, so its queue slots and invitations are released.</summary>
    private void DropWorker(string workerId, BattlegroundManager manager)
    {
        List<ObjectGuid> gone = [.. _players.Where(p => p.Value.WorkerId == workerId).Select(p => p.Key)];
        foreach (ObjectGuid player in gone)
        {
            _players.Remove(player);
            manager.PlayerLoggedOut(player);
        }
    }

    private void Register(string workerId, BattlegroundMember member, bool remote)
    {
        if (remote)
        {
            _players[member.Guid] = new RemotePlayer(workerId, member);
        }
        else
        {
            _players.Remove(member.Guid);
        }
    }

    /// <summary>A command of a known player: it is served by <paramref name="workerId"/> (a player that came back to this node is local again).</summary>
    private void Touch(string workerId, ObjectGuid player, bool remote)
    {
        if (!remote)
        {
            _players.Remove(player);
        }
        else if (_players.TryGetValue(player, out RemotePlayer? known))
        {
            _players[player] = known with { WorkerId = workerId };
        }
        else
        {
            _players[player] = new RemotePlayer(workerId, new BattlegroundMember(player, Team.Alliance, 0, InWorld: true, Deserter: false));
        }
    }

    /// <summary>A group member as the holder knows it: facts registered by its own worker replace the requester's guess.</summary>
    private BattlegroundMember Known(BattlegroundMember member)
        => _players.TryGetValue(member.Guid, out RemotePlayer? known) ? known.Member with { InWorld = true } : member;

    private string? RemoteWorker(ObjectGuid player)
        => Workers is not null && _players.TryGetValue(player, out RemotePlayer? known) ? known.WorkerId : null;

    private bool TryNotify(ObjectGuid player, Func<BattlegroundNotice> notice)
    {
        if (RemoteWorker(player) is not { } worker)
        {
            return false;
        }

        Workers!.Deliver(worker, notice());
        return true;
    }

    // ------------------------------------------------------------------ IBattlegroundManagerHost

    public bool IsOnline(ObjectGuid player) => RemoteWorker(player) is not null || local.IsOnline(player);

    public IBattlegroundHost CreateMatchHost(BattlegroundType type, uint instanceId) => local.CreateMatchHost(type, instanceId);

    public bool CanCreateMatch(uint mapId) => local.CanCreateMatch(mapId);

    public uint AllocateInstanceId() => local.AllocateInstanceId();

    public void SendStatus(ObjectGuid player, uint queueSlot, BattlegroundStatusSubject subject, BattlegroundStatus status, uint time1, uint time2)
    {
        if (!TryNotify(player, () => new BattlegroundStatusNotice(player, queueSlot, subject, status, time1, time2)))
        {
            local.SendStatus(player, queueSlot, subject, status, time1, time2);
        }
    }

    public void SendGroupJoined(ObjectGuid player, uint result)
    {
        if (!TryNotify(player, () => new BattlegroundGroupJoinedNotice(player, result)))
        {
            local.SendGroupJoined(player, result);
        }
    }

    public void SendJoinError(ObjectGuid player, BattlegroundJoinError error)
    {
        if (!TryNotify(player, () => new BattlegroundJoinErrorNotice(player, error)))
        {
            local.SendJoinError(player, error);
        }
    }

    public void GroupQueueLimitNotice(ObjectGuid player, uint limit)
    {
        if (!TryNotify(player, () => new BattlegroundQueueLimitNotice(player, limit)))
        {
            local.GroupQueueLimitNotice(player, limit);
        }
    }

    /// <summary>The return point of a remote requester (and its group, which the leader's place stands for) is the position its worker sent with the join.</summary>
    public void StoreEntryPoint(ObjectGuid member, ObjectGuid leader, bool queuedAtPortal)
    {
        if (_joinPoint is { } point)
        {
            storeEntryPoint(member, point);
            return;
        }

        local.StoreEntryPoint(member, leader, queuedAtPortal);
    }

    public void PrepareToPortIn(ObjectGuid player)
    {
        if (!TryNotify(player, () => new BattlegroundPrepareToPortNotice(player)))
        {
            local.PrepareToPortIn(player);
        }
    }

    /// <summary>
    /// A remote player is told to go to the match's start location: its worker teleports it, and the teleport is a far transfer to this node.
    /// From then on the player is served here, so it is no longer registered as remote.
    /// </summary>
    public void SendToBattleground(ObjectGuid player, Battleground battleground, Team team)
    {
        if (RemoteWorker(player) is not { } worker)
        {
            local.SendToBattleground(player, battleground, team);
            return;
        }

        BattlegroundStartLocation start = team == Team.Alliance ? battleground.Template.AllianceStart : battleground.Template.HordeStart;
        Workers!.Deliver(worker, new BattlegroundEnterNotice(player, battleground.MapId, battleground.InstanceId, team, start.X, start.Y, start.Z, start.Orientation));
        _players.Remove(player);
    }

    public void BattlegroundCreated(Battleground battleground) => local.BattlegroundCreated(battleground);

    public void BattlegroundDeleted(Battleground battleground) => local.BattlegroundDeleted(battleground);
}

/// <summary>
/// The worker side: turns the player's queue actions into commands for the holder. Stateless; the link must not block. World thread.
/// </summary>
internal sealed class BattlegroundWorkerClient(string workerId, IBattlegroundHolderLink holder)
{
    /// <summary>This worker's id as the holder knows it.</summary>
    public string WorkerId { get; } = workerId;

    public void Join(BattlegroundJoinRequest request, BattlegroundEntryPoint? entryPoint) => holder.Submit(WorkerId, new BattlegroundJoinCommand(request, entryPoint));

    public void Port(BattlegroundPortRequest request) => holder.Submit(WorkerId, new BattlegroundPortCommand(request));

    public void Poll(ObjectGuid player, uint level) => holder.Submit(WorkerId, new BattlegroundStatusCommand(player, level));

    public void Login(BattlegroundMember member) => holder.Submit(WorkerId, new BattlegroundLoginCommand(member));

    public void Logout(ObjectGuid player) => holder.Submit(WorkerId, new BattlegroundLogoutCommand(player));
}
