using ArcaneCore.Game.Maps;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// In-process zero-socket <see cref="IGatewayWorkerTransport"/> that marshals session attach/detach and client opcodes
/// directly onto the authoritative <see cref="WorldRuntime"/> simulation thread while enforcing readiness, drain,
/// <c>SessionGeneration</c>, <c>OwnerEpoch</c>, and monotonic per-session sequence ordering.
/// </summary>
public sealed class LocalGatewayWorkerTransport : IGatewayWorkerTransport
{
    private sealed class AttachedSessionState
    {
        public required int AccountId { get; init; }
        public required ulong CharacterGuid { get; init; }
        public required long SessionGeneration { get; init; }
        public required int GatewayConnectionId { get; init; }
        public long LastInboundSequence;
        public long LastOutboundSequence;
    }

    private readonly WorldRuntime _world;
    private readonly ClusterWorkerReadinessProbe _readiness;
    private readonly Action<LocalGatewayWorkerTransport, ClusterSessionPacket>? _onClientPacket;
    private readonly Dictionary<ulong, AttachedSessionState> _sessions = [];

    public LocalGatewayWorkerTransport(
        WorldRuntime world,
        ClusterWorkerReadinessProbe readiness,
        Action<LocalGatewayWorkerTransport, ClusterSessionPacket>? onClientPacket = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(readiness);
        _world = world;
        _readiness = readiness;
        _onClientPacket = onClientPacket;
    }

    public event Action<ClusterSessionPacket>? OutboundPacketReceived;

    internal event Action<int, ClusterSessionPacket>? OutboundPacketForConnectionReceived;

    public event Action<ulong, long, ClusterSessionDetachReason>? SessionDetached;

    internal event Action<int, ulong, long, ClusterSessionDetachReason>? SessionDetachedForConnection;

    public ValueTask<ClusterSessionAttachResult> AttachSessionAsync(
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default) =>
        AttachSessionCoreAsync( gatewayConnectionId: 0, accountId, characterGuid, requestedGeneration, expectedOwnerEpoch, cancellationToken);

    internal async ValueTask<ClusterSessionAttachResult> AttachSessionCoreAsync(
        int gatewayConnectionId,
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return new ClusterSessionAttachResult(false, 0, _readiness.CurrentOwnerEpoch, "invalid accountId");
        }

        if (characterGuid == 0)
        {
            return new ClusterSessionAttachResult(false, 0, _readiness.CurrentOwnerEpoch, "invalid characterGuid");
        }

        if (requestedGeneration <= 0)
        {
            return new ClusterSessionAttachResult(false, 0, _readiness.CurrentOwnerEpoch, "requestedGeneration must be positive");
        }

        if (!_readiness.IsGameplayReady)
        {
            return new ClusterSessionAttachResult(
                false,
                0,
                _readiness.CurrentOwnerEpoch,
                $"worker is not gameplay-ready (state={_readiness.State})");
        }

        long activeEpoch = _readiness.CurrentOwnerEpoch;
        if (expectedOwnerEpoch != activeEpoch)
        {
            return new ClusterSessionAttachResult(
                false,
                0,
                activeEpoch,
                $"owner epoch mismatch: expected {expectedOwnerEpoch}, worker epoch is {activeEpoch}");
        }

        return await _world.InvokeAsync(() =>
        {
            if (!_readiness.IsGameplayReady)
            {
                return new ClusterSessionAttachResult(
                    false,
                    0,
                    _readiness.CurrentOwnerEpoch,
                    $"worker is not gameplay-ready on world thread (state={_readiness.State})");
            }

            long epochNow = _readiness.CurrentOwnerEpoch;
            if (expectedOwnerEpoch != epochNow)
            {
                return new ClusterSessionAttachResult(
                    false,
                    0,
                    epochNow,
                    $"owner epoch mismatch on world thread: expected {expectedOwnerEpoch}, worker epoch is {epochNow}");
            }

            if (_sessions.TryGetValue(characterGuid, out AttachedSessionState? existing))
            {
                if (requestedGeneration <= existing.SessionGeneration)
                {
                    return new ClusterSessionAttachResult(
                        false,
                        existing.SessionGeneration,
                        epochNow,
                        $"stale session generation {requestedGeneration} (active={existing.SessionGeneration})");
                }

                // Newer generation deterministically supersedes the older session.
                _sessions.Remove(characterGuid);
                NotifySessionDetached(
                    existing.GatewayConnectionId,
                    characterGuid,
                    existing.SessionGeneration,
                    ClusterSessionDetachReason.SupersededByNewLogin);
            }

            var state = new AttachedSessionState
            {
                AccountId = accountId,
                CharacterGuid = characterGuid,
                SessionGeneration = requestedGeneration,
                GatewayConnectionId = gatewayConnectionId,
            };
            _sessions[characterGuid] = state;
            return new ClusterSessionAttachResult(true, requestedGeneration, epochNow, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> DetachSessionAsync(
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default) =>
        DetachSessionCoreAsync(gatewayConnectionId: null, characterGuid, sessionGeneration, reason, cancellationToken);

    internal async ValueTask<bool> DetachSessionCoreAsync(
        int? gatewayConnectionId,
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default)
    {
        if (_world.IsStopped)
        {
            return false;
        }

        return await _world.InvokeAsync(() =>
        {
            if (!_sessions.TryGetValue(characterGuid, out AttachedSessionState? existing))
            {
                return false;
            }

            if (existing.SessionGeneration != sessionGeneration)
            {
                return false;
            }

            if (gatewayConnectionId.HasValue && existing.GatewayConnectionId != gatewayConnectionId.Value)
            {
                return false;
            }

            _sessions.Remove(characterGuid);
            NotifySessionDetached(existing.GatewayConnectionId, characterGuid, sessionGeneration, reason);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches all sessions currently owned by <paramref name="gatewayConnectionId"/> (e.g. when a gateway TCP connection drops).
    /// </summary>
    internal async ValueTask DetachAllForConnectionAsync(int gatewayConnectionId, CancellationToken cancellationToken = default)
    {
        if (_world.IsStopped)
        {
            return;
        }

        await _world.InvokeAsync(() =>
        {
            List<AttachedSessionState>? toRemove = null;
            foreach (AttachedSessionState session in _sessions.Values)
            {
                if (session.GatewayConnectionId == gatewayConnectionId)
                {
                    toRemove ??= [];
                    toRemove.Add(session);
                }
            }

            if (toRemove is null)
            {
                return true;
            }

            foreach (AttachedSessionState session in toRemove)
            {
                _sessions.Remove(session.CharacterGuid);
                NotifySessionDetached(
                    session.GatewayConnectionId,
                    session.CharacterGuid,
                    session.SessionGeneration,
                    ClusterSessionDetachReason.ClientDisconnect);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> SendToWorkerAsync(
        ClusterSessionPacket packet,
        CancellationToken cancellationToken = default)
    {
        if (packet.Payload.Length > WorldSession.MaxClientPacketSize)
        {
            throw new InvalidOperationException(
                $"client packet payload ({packet.Payload.Length} B) exceeds MaxClientPacketSize ({WorldSession.MaxClientPacketSize} B)");
        }

        if (!_readiness.IsLive || _world.IsStopped)
        {
            return false;
        }

        if (packet.OwnerEpoch != _readiness.CurrentOwnerEpoch)
        {
            return false;
        }

        byte[] payloadCopy = packet.Payload.ToArray();
        ClusterSessionPacket safePacket = packet with { Payload = payloadCopy };

        return await _world.InvokeAsync(() =>
        {
            if (!_readiness.IsLive || _world.IsStopped)
            {
                return false;
            }

            if (safePacket.OwnerEpoch != _readiness.CurrentOwnerEpoch)
            {
                return false;
            }

            if (!_sessions.TryGetValue(safePacket.CharacterGuid, out AttachedSessionState? session))
            {
                return false;
            }

            if (session.SessionGeneration != safePacket.SessionGeneration || session.AccountId != safePacket.AccountId)
            {
                return false;
            }

            if (safePacket.Sequence <= session.LastInboundSequence)
            {
                return false;
            }

            session.LastInboundSequence = safePacket.Sequence;
            _onClientPacket?.Invoke(this, safePacket);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Called on the authoritative <see cref="WorldRuntime"/> thread (or via a posted command) to emit an ordered
    /// server packet for an attached session back to its owning gateway.
    /// Returns <see langword="false"/> if the session generation is no longer active.
    /// </summary>
    public bool TrySendToGateway(
        ulong characterGuid,
        long sessionGeneration,
        uint opcode,
        ReadOnlyMemory<byte> payload)
    {
        if (!_world.IsWorldThread)
        {
            throw new InvalidOperationException("TrySendToGateway must be called on the authoritative WorldRuntime thread");
        }

        if (payload.Length > WorldSession.MaxServerPayload)
        {
            throw new InvalidOperationException(
                $"server packet payload ({payload.Length} B) exceeds MaxServerPayload ({WorldSession.MaxServerPayload} B)");
        }

        if (!_readiness.IsLive || _world.IsStopped)
        {
            return false;
        }

        if (!_sessions.TryGetValue(characterGuid, out AttachedSessionState? session))
        {
            return false;
        }

        if (session.SessionGeneration != sessionGeneration)
        {
            return false;
        }

        long seq = ++session.LastOutboundSequence;
        var outbound = new ClusterSessionPacket(
            SessionGeneration: session.SessionGeneration,
            AccountId: session.AccountId,
            CharacterGuid: session.CharacterGuid,
            OwnerEpoch: _readiness.CurrentOwnerEpoch,
            Sequence: seq,
            Opcode: opcode,
            Payload: payload.ToArray());

        OutboundPacketReceived?.Invoke(outbound);
        OutboundPacketForConnectionReceived?.Invoke(session.GatewayConnectionId, outbound);
        return true;
    }

    private void NotifySessionDetached(
        int gatewayConnectionId,
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason)
    {
        SessionDetached?.Invoke(characterGuid, sessionGeneration, reason);
        SessionDetachedForConnection?.Invoke(gatewayConnectionId, characterGuid, sessionGeneration, reason);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
