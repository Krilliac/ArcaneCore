using ArcaneCore.Game;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Economy;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// What <see cref="WorkerTransferSource"/> asks the game features about a player: whether a transient state forbids the handoff, and the
/// interruptible windows (loot, gossip) that are closed instead. Both run on the world thread.
/// </summary>
public interface ITransferPlayerProbe
{
    /// <summary>Which transient states (open trade, charm or possession, taxi flight, pending quest or economy settlement) forbid a transfer of <paramref name="player"/> now.</summary>
    TransferActiveBlockers Inspect(Player player);

    /// <summary>Closes the windows a transfer interrupts rather than refuses on (loot, gossip). Safe to call when none is open.</summary>
    void CloseInterruptible(Player player);
}

/// <summary>The durable side of a capture: the settlement-grade save, the persistence barrier, and the save refusal after retire.</summary>
public interface ITransferPersistence
{
    /// <summary>
    /// World thread: starts the pre-transfer save of <paramref name="state"/> ahead of ordinary snapshots and suppresses every later
    /// ordinary snapshot of the character (the freeze). The returned task completes when the write is durable.
    /// </summary>
    Task SaveAndHold(CharacterState state);

    /// <summary>Waits until the save and every per-character feature queue of <paramref name="characterId"/> are durable; throws when one is not.</summary>
    Task FlushAsync(int characterId, CancellationToken cancellationToken);

    /// <summary>Lifts the freeze (abort): ordinary snapshots of the character are accepted again.</summary>
    void ReleaseHold(int characterId);

    /// <summary>Retire: refuses every snapshot of the character until an authoritative load resumes it, so this world can never overwrite what the target saves.</summary>
    void Refuse(int characterId);
}

/// <summary>
/// Production <see cref="ITransferPersistence"/> over <see cref="CharacterSaveQueue"/> (settlement save, hold, quarantine) and the
/// unified <see cref="CharacterPersistenceBarrier"/> (every other per-character queue). Thread-safe through the queue.
/// </summary>
public sealed class SaveQueueTransferPersistence(CharacterSaveQueue saves, CharacterPersistenceBarrier? barrier = null) : ITransferPersistence
{
    public Task SaveAndHold(CharacterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Same order as the whole-world handoff freeze (LocalWorldHandoffSource): the settlement save bypasses the hold, then the hold is set.
        Task write = saves.SaveForSettlementAsync(state);
        saves.HoldCharacter(state.Id);
        return write;
    }

    public async Task FlushAsync(int characterId, CancellationToken cancellationToken)
    {
        if (barrier is null)
        {
            await saves.FlushCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
            return;
        }

        CharacterPersistenceFlushResult flushed = await barrier.TryFlushCharacterAsync(characterId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!flushed.IsDurable)
        {
            throw new InvalidOperationException($"character {characterId} is not durable ({string.Join(", ", flushed.FailedQueues)}); the transfer cannot be captured");
        }
    }

    public void ReleaseHold(int characterId) => saves.ResumeCharacter(characterId);

    public void Refuse(int characterId) => saves.QuarantineCharacter(characterId);
}

/// <summary>
/// Production <see cref="ITransferPlayerProbe"/> over the world features reachable from <paramref name="services"/>; a feature that is not
/// installed contributes no blocker. World thread.
/// </summary>
public sealed class FeatureTransferPlayerProbe(IServiceProvider services, CharacterSaveQueue? saves = null) : ITransferPlayerProbe
{
    public TransferActiveBlockers Inspect(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var trade = services.GetService<EconomyFeature>()?.TradeOf(player);
        bool taxi = services.GetService<NpcServicesFeature>()?.Flights is { } flights && flights.IsFlying(player);
        int id = checked((int)player.Guid.Low);
        return new TransferActiveBlockers(
            HasActiveTradeWindow: trade is not null,
            HasOpenLootWindow: false, // closed by CloseInterruptible
            HasActiveCharmOrMindControl: !player.CharmGuid.IsEmpty || !player.CharmerGuid.IsEmpty,
            HasPendingQuestSettlement: player.IsQuestSettlementPending,
            // An economy settlement holds the character in the save queue; so does a loot settlement. Either is not quiescent.
            HasPendingEconomySettlement: trade is { Settling: true } || (saves?.IsHeld(id) ?? false) || (saves?.IsQuarantined(id) ?? false),
            HasInFlightTaxiMovement: taxi);
    }

    public void CloseInterruptible(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map is { } map
            && services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot is { } loot
            && loot.OpenLootOf(player) is { } bag)
        {
            loot.Release(player, bag.Source);
        }

        // Leaving the map ends any gossip window on the client side too; the client is told explicitly so it does not keep a stale one.
        player.Session.Send(ArcaneCore.Protocol.WorldOpcode.SmsgGossipComplete, []);
    }
}

/// <summary>
/// The production <see cref="ITransferSourceWorker"/> (Tier 3, default D4): a transfer is a save and a reload through the characters
/// database, not a serialisation of live objects. <see cref="SuspendAndCaptureAsync"/> checks the blockers, closes loot and gossip, then on the
/// world thread (one command, so nothing ticks in between) snapshots the player, starts the settlement save, suppresses later snapshots and takes
/// the player out of its map, keeping the session shell (the gateway session and the <c>WorldSession</c> stay; only the in-world entity goes).
/// It then waits until the save and every feature queue are durable and returns a slim bundle (identity, generation, source and target
/// position, ownership token): the target reloads everything else from the database. Persistent pets and auras come back from the database
/// there; temporary guardians and totems go with the player's removal from the map and are not carried.
/// <para>
/// A player with a blocker (open trade, charm, taxi flight, quest or economy settlement) is not frozen: the returned bundle carries the
/// blockers and the coordinator aborts the transfer on validation. <see cref="ResumeAfterAbortAsync"/> re-adds the player where it stood
/// and lifts the save hold. <see cref="RetireSourcePlayerAsync"/> releases this worker's claim on the character under the superseded token
/// and refuses every later save of it.
/// </para>
/// Thread affinity: the interface methods may be called from any thread; every world mutation runs through <see cref="WorldRuntime.InvokeAsync{T}"/>.
/// The claim table is guarded by a lock.
/// </summary>
public sealed class WorkerTransferSource(
    WorkerNodeId workerId,
    WorldRuntime world,
    ITransferPersistence persistence,
    ITransferPlayerProbe probe,
    TimeProvider? timeProvider = null,
    ILogger<WorkerTransferSource>? logger = null) : ITransferSourceWorker
{
    /// <summary>The failure code of a suspend whose player is not online here.</summary>
    public const string ErrorPlayerNotOnline = "player-not-online";

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Suspension> _suspended = [];
    private readonly HashSet<Guid> _retired = [];
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger<WorkerTransferSource>.Instance;

    public WorkerNodeId WorkerId => workerId;

    /// <summary>Characters this worker has frozen for a transfer and still holds the claim on.</summary>
    public int SuspendedCount
    {
        get { lock (_gate) return _suspended.Count; }
    }

    /// <summary>Whether this worker still holds the claim on <paramref name="characterGuid"/> for a transfer (false after retire or abort).</summary>
    public bool HoldsClaim(ulong characterGuid)
    {
        lock (_gate) return _suspended.ContainsKey(characterGuid);
    }

    public async Task<PlayerTransferBundle> SuspendAndCaptureAsync(
        Guid transferId,
        ulong characterGuid,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition,
        CancellationToken cancellationToken = default)
    {
        Capture captured = await world.InvokeAsync(
            () => CaptureOnWorldThread(transferId, characterGuid, targetWorkerId, targetMapId, targetInstanceId, targetPosition),
            CancellationToken.None).ConfigureAwait(false); // the command must not lose a race to cancellation after it removed the player

        if (captured.Suspension is not { } suspension)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return captured.Bundle; // blocked or already captured: nothing more to wait for
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await suspension.Save.WaitAsync(cancellationToken).ConfigureAwait(false);
            await persistence.FlushAsync(suspension.CharacterId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException)
            {
                FaultLedger.Shared.Record("cluster.transfer-source", ex);
            }

            // Not durable: the player must not stay frozen out of the world; put it back, then fail the capture.
            await RestoreAsync(suspension, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return captured.Bundle;
    }

    public async Task ResumeAfterAbortAsync(
        Guid transferId,
        ulong characterGuid,
        CharacterOwnershipToken ownershipToken,
        string abortReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownershipToken);
        Suspension suspension;
        lock (_gate)
        {
            if (_retired.Contains(transferId))
            {
                throw new InvalidOperationException($"transfer '{transferId}' was retired on this worker; it cannot resume");
            }

            if (!_suspended.TryGetValue(characterGuid, out Suspension? found))
            {
                return; // never frozen (blocked capture) or already resumed
            }

            if (found.TransferId != transferId)
            {
                throw new InvalidOperationException($"character {characterGuid} is suspended by transfer '{found.TransferId}', not '{transferId}'");
            }

            // The coordinator's per-character token need not equal the world-scope token captured here. An abort must always put back a player
            // this worker froze for this transfer, so a difference is logged, never a reason to leave the player out of the world.
            if (!Equals(found.Token, ownershipToken))
            {
                _logger.LogWarning("Transfer {TransferId} resumes character {CharacterGuid} under ownership token {Resumed}, captured under {Captured}; restoring anyway",
                    transferId, characterGuid, ownershipToken, found.Token);
            }

            suspension = found;
        }

        await RestoreAsync(suspension, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetireSourcePlayerAsync(
        Guid transferId,
        ulong characterGuid,
        CharacterOwnershipToken supersededToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supersededToken);
        cancellationToken.ThrowIfCancellationRequested();
        Suspension? suspension = null;
        lock (_gate)
        {
            if (!_suspended.TryGetValue(characterGuid, out Suspension? found))
            {
                if (_retired.Contains(transferId))
                {
                    return; // a roll-forward repeats the step
                }
            }
            else if (found.TransferId != transferId)
            {
                throw new InvalidOperationException($"character {characterGuid} is suspended by transfer '{found.TransferId}', not '{transferId}'");
            }
            else
            {
                // Same as resume: the coordinator's token is authoritative for the commit, so a difference from the captured world token is logged
                // and the committed roll-forward still completes.
                if (!Equals(found.Token, supersededToken))
                {
                    _logger.LogWarning("Transfer {TransferId} retires character {CharacterGuid} under superseded token {Superseded}, captured under {Captured}",
                        transferId, characterGuid, supersededToken, found.Token);
                }

                // Release the claim before refusing saves: from here no path can resume or re-add the entity.
                _suspended.Remove(characterGuid);
                _retired.Add(transferId);
                suspension = found;
            }
        }

        if (suspension is null)
        {
            await RetireWithoutSuspensionAsync(transferId, characterGuid, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The hold set at capture stays; quarantine makes the refusal explicit and survives a later ResumeCharacter of the hold.
        persistence.Refuse(suspension.CharacterId);

        // The retired entity never ran the logout teardown (capture detached it without it): do it now so the PlayerLoggingOut subscribers
        // forget the guid and the Player is released. The claim is already gone, so this runs at most once per transfer.
        await world.InvokeAsync(() => world.RetireDetachedPlayer(suspension.Player), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Retire of a character this process holds no suspension of. The suspension is memory-only, so a source that restarted after the capture has none, and its frozen player died with
    /// the old process: there is nothing to tear down, and the step is done once this world can no longer write the character. Refusing here would stop the committed roll-forward at
    /// its first step and leave <c>active_transfer</c> set for good. Callers reach this only for a transfer the journal says is committed (the capture was durable before the commit),
    /// and in Worker fencing scope the login claim refuses the character while that row's <c>active_transfer</c> is set, so nothing on this worker can bring it online before the roll-forward
    /// ends. Realm scope has no claim; there a second world is refused at startup, so this restarted world is the only one that could hold the character.
    /// The check and the refusal are one step on the world thread, where a login enters the world: an online character is not the player the transfer froze, so that is an error.
    /// </summary>
    private async Task RetireWithoutSuspensionAsync(Guid transferId, ulong characterGuid, CancellationToken cancellationToken)
    {
        var guid = new ObjectGuid(characterGuid);
        bool online = await world.InvokeAsync(() =>
        {
            if (world.FindOnlinePlayer(guid) is not null)
            {
                return true;
            }

            persistence.Refuse(checked((int)guid.Low));
            return false;
        }, cancellationToken).ConfigureAwait(false);
        if (online)
        {
            throw new InvalidOperationException($"character {characterGuid} is online on this worker but not suspended; nothing to retire for transfer '{transferId}'");
        }

        lock (_gate)
        {
            _retired.Add(transferId);
        }

        _logger.LogInformation("Transfer {TransferId}: character {CharacterGuid} has no suspension on this worker and is not online here (the worker restarted after the capture); retired", transferId, characterGuid);
    }

    private Capture CaptureOnWorldThread(
        Guid transferId,
        ulong characterGuid,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition)
    {
        lock (_gate)
        {
            if (_suspended.TryGetValue(characterGuid, out Suspension? existing))
            {
                if (existing.TransferId != transferId)
                {
                    throw new InvalidOperationException($"character {characterGuid} is already suspended by transfer '{existing.TransferId}'");
                }

                return new Capture(existing.Bundle, null); // a repeated call of the same transfer
            }
        }

        Player player = world.FindOnlinePlayer(new ObjectGuid(characterGuid))
            ?? throw new KeyNotFoundException($"{ErrorPlayerNotOnline}: character {characterGuid} is not online on worker '{workerId}'");
        Map map = player.Map ?? throw new InvalidOperationException($"character {characterGuid} is online but not in a map");
        CharacterOwnershipToken token = player.CharacterOwnership ?? world.CharacterOwnership
            ?? throw new InvalidOperationException("the source world holds no characters ownership");

        TransferActiveBlockers blockers = probe.Inspect(player);
        PlayerTransferBundle bundle = BuildBundle(transferId, player, map, token, targetWorkerId, targetMapId, targetInstanceId, targetPosition, blockers);
        if (blockers.HasAny)
        {
            return new Capture(bundle, null); // refused: the player stays in the world untouched
        }

        probe.CloseInterruptible(player);

        // Queue what logout would queue (skills past their debounce, the current pet, explored zones, rest, spell state, pvp flags) so the
        // flush after this command makes it durable; the target reloads from the database.
        world.RaisePlayerTransferPreparing(player);

        // Freeze: snapshot, start the settlement save, suppress later snapshots, leave the map; one world-thread command, so no tick runs between.
        CharacterState state = player.CreateSnapshot(world.NowMs);
        Task save = persistence.SaveAndHold(state);
        var suspension = new Suspension(transferId, player, map, state.Id, token, bundle, save);
        lock (_gate)
        {
            _suspended[characterGuid] = suspension;
        }

        try
        {
            // Not RemovePlayer: that is the logout path (PlayerLoggingOut, OnLogout, features untrack the player) and abort could not undo it.
            world.DetachPlayerForTransfer(player);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transfer-source", ex);
            lock (_gate)
            {
                _suspended.Remove(characterGuid);
            }

            persistence.ReleaseHold(state.Id);
            if (!world.IsOnline(player.Guid) && player.Map is null)
            {
                world.ReattachPlayerAfterTransferAbort(player, ResolveMap(player, map));
            }

            throw;
        }

        return new Capture(bundle, suspension);
    }

    /// <summary>Puts the suspended player back where it stood and lifts the hold; idempotent through the claim table.</summary>
    private async Task RestoreAsync(Suspension suspension, CancellationToken cancellationToken)
    {
        await world.InvokeAsync(() =>
        {
            lock (_gate)
            {
                if (!_suspended.TryGetValue(suspension.Player.Guid.Value, out Suspension? current) || !ReferenceEquals(current, suspension))
                {
                    return false; // retired or already restored
                }

                _suspended.Remove(suspension.Player.Guid.Value);
            }

            if (!world.IsOnline(suspension.Player.Guid) && suspension.Player.Map is null)
            {
                world.ReattachPlayerAfterTransferAbort(suspension.Player, ResolveMap(suspension.Player, suspension.Map));
            }

            persistence.ReleaseHold(suspension.CharacterId);

            // The client left while the player was frozen (its gateway died, or the session was closed): the session's close found no player in the world and logged
            // nobody out, so the restored player would stay online with nobody behind it and every later login of the character would be refused as a duplicate.
            // Log it out now, after the hold is lifted so the logout snapshot is written and the claim release follows (the close's own posted removal, if it is still
            // queued behind this command, then finds the player offline and does nothing).
            if (suspension.Player.Session.IsDisconnected)
            {
                _logger.LogInformation("Character {CharacterGuid} resumed after an aborted transfer, but its session is closed; logging it out", suspension.Player.Guid.Value);
                world.RemovePlayer(suspension.Player);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private Map ResolveMap(Player player, Map captured)
        => ReferenceEquals(world.FindMap(captured.MapId, captured.InstanceId), captured) ? captured : world.ResolveLoginMap(player);

    private PlayerTransferBundle BuildBundle(
        Guid transferId,
        Player player,
        Map map,
        CharacterOwnershipToken token,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition,
        TransferActiveBlockers blockers)
    {
        var powers = new uint[5];
        for (int i = 0; i < powers.Length; i++)
        {
            powers[i] = player.GetUInt32(UpdateFields.UnitFieldPower1 + i);
        }

        return new PlayerTransferBundle
        {
            TransferId = transferId,
            CharacterGuid = player.Guid.Value,
            CharacterId = checked((int)player.Guid.Low),
            AccountId = player.Session.AccountId,
            SessionGeneration = player.Session.SessionGeneration,
            SourceWorkerId = workerId,
            TargetWorkerId = targetWorkerId,
            SourceOwnershipToken = token,
            CapturedAtUtcMs = _time.GetUtcNow().ToUnixTimeMilliseconds(),
            SourceMapId = map.MapId,
            SourceInstanceId = map.InstanceId,
            SourcePosition = new TransferPosition(player.X, player.Y, player.Z, player.Orientation),
            TargetMapId = targetMapId,
            TargetInstanceId = targetInstanceId,
            TargetPosition = targetPosition,
            Health = player.Health,
            MaxHealth = player.MaxHealth,
            Powers = powers,
            Level = player.Level,
            Money = player.Money,
            Flags = (uint)player.Flags,
            StandState = (byte)player.StandState,
            IsGhost = (player.Flags & PlayerFlags.Ghost) != 0,
            HomeBind = player.Home,
            // The body as the settlement save stores it. The target restores the ghost from that stored life; this copy is the journal's record of what was captured.
            Corpse = PlayerLife.Capture(player).Corpse,
            Blockers = blockers,
        };
    }

    private readonly record struct Capture(PlayerTransferBundle Bundle, Suspension? Suspension);

    /// <summary>The claim this worker holds on one frozen player: the entity and map to put back on abort, and the token it captured under.</summary>
    private sealed record Suspension(
        Guid TransferId,
        Player Player,
        Map Map,
        int CharacterId,
        CharacterOwnershipToken Token,
        PlayerTransferBundle Bundle,
        Task Save);
}
