using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// The worker's <see cref="IClusterFarTeleportRouter"/> (design _wf/handoff-control-DESIGN.md sections 4.3 and 4.5, commit C10): a teleport to a map another worker owns becomes a
/// <see cref="ControlSubmit"/> of kind transfer request on the player's own gateway link; the gateway's transfer service coordinates the six stages and answers with a
/// <see cref="ControlSubmitReply"/> (taken or refused) and, when the transfer ends, a <see cref="ControlNotice"/>.
/// <list type="bullet">
/// <item><description><see cref="TryBeginTransfer"/> never blocks: it decides from local facts (the player's session is a gateway session whose link is admitted, which contract 5 guarantees; the finite destination; the
/// source ownership token; the <see cref="ITransferPlayerProbe"/> blockers; no transfer of this character in flight here) and queues the frame. It answers true once the frame is queued; the player stays where it is until
/// the source's participant suspends it.</description></item>
/// <item><description>Which worker owns the target map is the gateway's decision (its map router); the request names only the map, with instance 0 (the target worker allocates an instance id at <c>Reserve</c> for an instanced
/// map, orchestrator decision Q8). The source worker id the gateway journals is the slot node id of the link the request arrives on, so no worker id travels in the frame.</description></item>
/// <item><description>A refusal or an abort arrives later (<see cref="OnSubmitReply"/>, <see cref="OnNotice"/>, on the link's receive loop) and is shown to the player on the world thread. Question Q9: retail answers a
/// full map with <c>SMSG_TRANSFER_ABORTED</c> reason <c>TRANSFER_ABORT_MAX_PLAYERS</c> (vmangos Map.cpp:2123-2138, <c>DungeonMap::CanEnter</c>; cmangos-classic Player.cpp:20055-20062 <c>GetAreaTriggerLockStatus</c> returns
/// <c>AREA_LOCKSTATUS_INSTANCE_IS_FULL</c>, which Player.cpp:18182-18183 sends as <c>TRANSFER_ABORT_MAX_PLAYERS</c>; cmangos-classic has no <c>DungeonMap::CanEnter</c>, the check lives in the player). That packet is sent when
/// the gateway's structured <see cref="ControlReasonCode"/> is <see cref="ControlReasonCode.Capacity"/>; the free text is never parsed. No retail reason fits "no worker owns the map", "the transfer was aborted" or "a
/// transfer is already running", so those are a generic system chat line only (a documented gap against retail, orchestrator decision Q9). The player never sees the gateway's text, which carries node ids, account ids, session
/// generations and exception text: it goes to the log only.</description></item>
/// <item><description>The in-flight table is bounded by <see cref="InFlightExpiry"/>: a gateway that died never sends its notice, and the character must be able to try again.</description></item>
/// </list>
/// Thread affinity: <see cref="TryBeginTransfer"/> runs on the world thread; the sink methods run on a link's receive loop and only touch the lock-guarded table and <see cref="WorldRuntime.Post"/>.
/// </summary>
public sealed class GatewayClusterFarTeleportRouter : IClusterFarTeleportRouter, IGatewayControlSubmitSink
{
    /// <summary>How long a transfer stays in flight here without a reply or notice before the character may start another.</summary>
    public static readonly TimeSpan InFlightExpiry = TimeSpan.FromMinutes(5);

    private readonly WorldRuntime _world;
    private readonly ITransferPlayerProbe _probe;
    private readonly Func<Player, long?> _sessionGeneration;
    private readonly Func<long, ControlSubmit, bool> _submit;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, InFlight> _inFlight = [];
    private readonly Dictionary<ulong, ulong> _characterByRequest = [];
    private ulong _nextRequestId;
    private Guid _lastTransferId;

    private sealed record InFlight(Guid TransferId, ulong RequestId, long StartedAt);

    /// <param name="world">The world, for the ownership token and to reach the player on the world thread.</param>
    /// <param name="probe">The blockers that forbid a transfer (open trade, charm, taxi flight, quest or economy settlement).</param>
    /// <param name="sessionGeneration">The gateway session generation of the player's session, or null when the player has no gateway session (a socket session, a bot).</param>
    /// <param name="submit">Queues a <see cref="ControlSubmit"/> on the admitted link that carries the generation; false when there is none.</param>
    /// <param name="timeProvider">Clock of the in-flight expiry (tests).</param>
    public GatewayClusterFarTeleportRouter(
        WorldRuntime world,
        ITransferPlayerProbe probe,
        Func<Player, long?> sessionGeneration,
        Func<long, ControlSubmit, bool> submit,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(sessionGeneration);
        ArgumentNullException.ThrowIfNull(submit);
        _world = world;
        _probe = probe;
        _sessionGeneration = sessionGeneration;
        _submit = submit;
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Characters with a transfer in flight from this worker (a test seam and a diagnostic).</summary>
    public int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>The id of the newest transfer this router sent (<see cref="Guid.Empty"/> before the first): the key of its journal row, for diagnostics and tests.</summary>
    public Guid LastTransferId
    {
        get
        {
            lock (_gate)
            {
                return _lastTransferId;
            }
        }
    }

    /// <inheritdoc />
    public bool TryBeginTransfer(Player player, TeleportDestination destination, TeleportOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z) || !float.IsFinite(destination.Orientation))
        {
            return false;
        }

        if (player.Map is not { } map || _sessionGeneration(player) is not { } generation)
        {
            return false;
        }

        CharacterOwnershipToken? token = player.CharacterOwnership ?? _world.CharacterOwnership;
        if (token is null || _probe.Inspect(player).HasAny)
        {
            return false;
        }

        ulong character = player.Guid.Value;
        Guid transferId = Guid.NewGuid();
        ulong requestId;
        lock (_gate)
        {
            ExpireLocked();
            if (_inFlight.ContainsKey(character))
            {
                return false;
            }

            requestId = ++_nextRequestId;
            // Registered before the frame is queued: the gateway's reply can arrive before this method returns.
            _inFlight[character] = new InFlight(transferId, requestId, _time.GetTimestamp());
            _characterByRequest[requestId] = character;
            _lastTransferId = transferId;
        }

        var body = new ControlTransferSubmit(
            transferId, character, player.AccountId, generation, map.MapId, map.InstanceId, destination.MapId, 0,
            new TransferPosition(destination.X, destination.Y, destination.Z, destination.Orientation),
            (options & TeleportOptions.NotLeaveTransport) != 0 ? ControlTransferOptions.NotLeaveTransport : ControlTransferOptions.None,
            token);
        bool queued;
        try
        {
            queued = _submit(generation, new ControlSubmit(requestId, body));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("far teleport of character {Character}: the transfer request could not be sent ({Message})", character, ex.Message);
            queued = false;
        }

        if (!queued)
        {
            Forget(character, requestId);
        }

        return queued;
    }

    /// <inheritdoc />
    public void OnSubmitReply(ControlSubmitReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ulong character;
        lock (_gate)
        {
            if (!_characterByRequest.Remove(reply.RequestId, out character))
            {
                return;
            }

            if (reply.Accepted)
            {
                return; // the transfer runs; its notice ends the entry
            }

            if (_inFlight.TryGetValue(character, out InFlight? entry) && entry.RequestId == reply.RequestId)
            {
                _inFlight.Remove(character);
            }
        }

        Tell(character, reply.Code, reply.Reason);
    }

    /// <inheritdoc />
    public void OnNotice(ControlNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.Kind != ControlNoticeKind.TransferOutcome)
        {
            return;
        }

        lock (_gate)
        {
            if (_inFlight.TryGetValue(notice.CharacterGuid, out InFlight? entry) && entry.TransferId == notice.OperationId)
            {
                _inFlight.Remove(notice.CharacterGuid);
                _characterByRequest.Remove(entry.RequestId);
            }
        }

        if (notice.Outcome != ControlNoticeOutcome.Completed)
        {
            Tell(notice.CharacterGuid, notice.Code, notice.Text);
        }
    }

    private void Forget(ulong character, ulong requestId)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(character, out InFlight? entry) && entry.RequestId == requestId)
            {
                _inFlight.Remove(character);
            }

            _characterByRequest.Remove(requestId);
        }
    }

    private void ExpireLocked()
    {
        if (_inFlight.Count == 0)
        {
            return;
        }

        long now = _time.GetTimestamp();
        List<ulong>? expired = null;
        foreach (KeyValuePair<ulong, InFlight> entry in _inFlight)
        {
            if (_time.GetElapsedTime(entry.Value.StartedAt, now) >= InFlightExpiry)
            {
                (expired ??= []).Add(entry.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (ulong character in expired)
        {
            _characterByRequest.Remove(_inFlight[character].RequestId);
            _inFlight.Remove(character);
        }
    }

    /// <summary>The only line a player sees for a refused or aborted far teleport.</summary>
    internal const string PlayerFailureText = "The teleport could not be completed.";

    // The gateway's text goes to the log only. The player may be gone by now (logged out, or retired by a completed transfer): nothing is sent then. Runs on the world thread.
    private void Tell(ulong character, ControlReasonCode code, string detail)
    {
        _logger.LogInformation("far teleport of character {Character} did not happen: code={Code} detail={Detail}", character, code, detail);
        _world.Post(() =>
        {
            if (_world.FindOnlinePlayer(new ObjectGuid(character)) is not { } player)
            {
                return;
            }

            if (code == ControlReasonCode.Capacity)
            {
                player.Session.Send(WorldOpcode.SmsgTransferAborted, TeleportPackets.BuildTransferAborted(TransferAbortReason.MaxPlayers));
            }

            player.Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(PlayerFailureText));
        });
    }
}
