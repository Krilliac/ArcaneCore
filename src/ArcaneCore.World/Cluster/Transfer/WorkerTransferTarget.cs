using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// Builds the passive shadow of a transferred character on the target worker: the <see cref="Player"/> object with everything the character
/// screen login loads (inventory, spells, quests, vitals, pets), read from the characters database. The player is not added to any map,
/// does not tick and the load performs no new durable write and registers nothing shared (hooks marked <see cref="ISkippedOnTransferShadowLoad"/>
/// do not run; the others only read, or drain writes that were already queued). May run on any thread (it reads databases).
/// </summary>
public interface ITransferShadowLoader
{
    /// <summary>Loads the shadow of <paramref name="bundle"/>'s character. Throws when the character or its client session cannot be resolved here.</summary>
    Task<Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken);

    /// <summary>
    /// Undoes what loading <paramref name="shadow"/> left in shared services (for example the honor service's tracking of the player), for a
    /// shadow that is dropped without ever entering the world. Writes nothing durable. Any thread.
    /// </summary>
    void Release(Player shadow);
}

/// <summary>
/// Marks an <see cref="ICharacterHooks"/> implementation that must not run on a transfer's shadow load, because its loading phase writes
/// durably or registers state a dropped shadow would leave behind (the battleground hook deletes the match binding of a character saved on
/// a battleground map: a login is not in a match any more, but an aborted transfer must keep it).
/// </summary>
public interface ISkippedOnTransferShadowLoad
{
}

/// <summary>The <see cref="ICharacterHooks"/> loading phases as the shadow load runs them: the login's two phases without the hooks marked <see cref="ISkippedOnTransferShadowLoad"/>.</summary>
public static class TransferShadowHooks
{
    /// <summary>Runs <see cref="ICharacterHooks.OnPlayerLoadingAsync"/> of every hook, then <see cref="ICharacterHooks.OnPlayerLoadedAsync"/> of every hook, skipping the marked ones. Session task or pool thread.</summary>
    public static async Task RunAsync(IEnumerable<ICharacterHooks> all, WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        ICharacterHooks[] hooks = [.. all.Where(h => h is not ISkippedOnTransferShadowLoad)];
        foreach (ICharacterHooks hook in hooks)
        {
            await hook.OnPlayerLoadingAsync(session, character, player).ConfigureAwait(false);
        }

        foreach (ICharacterHooks hook in hooks)
        {
            await hook.OnPlayerLoadedAsync(session, character, player).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// What activation needs from the adopted client session. Both run on the world thread. The session state machine of the adopted session
/// (character screen to in world) lives behind this seam. The login packets themselves are the teleport service's arrival callbacks.
/// </summary>
public interface ITransferSessionEntry
{
    /// <summary>Moves the adopted session into the in-world state with <paramref name="player"/>. False when the client is gone or superseded (nothing more is sent or added).</summary>
    bool TryAttach(Player player);

    /// <summary>The saves held for the character since the transfer's capture resume (as the login does once the player is in its map).</summary>
    void ResumeSaves(Player player);
}

/// <summary>
/// Production <see cref="ITransferShadowLoader"/>: the <see cref="ICharacterHooks"/> loading phases of CMSG_PLAYER_LOGIN (through
/// <see cref="TransferShadowHooks"/>, so hooks marked <see cref="ISkippedOnTransferShadowLoad"/> do not run) on the adopted session's service
/// scope. <paramref name="sessions"/> resolves the adopted <see cref="WorldSession"/> of a bundle (null while the gateway has not moved the
/// client here); an unresolved session fails the load and the coordinator aborts or retries. The load saves nothing and registers nothing
/// shared that <see cref="Release"/> does not undo. It skips the login's settlement barriers and rename check: the source's settlement save
/// is durable before the bundle exists. Not thread-affine.
/// </summary>
public sealed class CharacterHooksShadowLoader(Func<PlayerTransferBundle, WorldSession?> sessions) : ITransferShadowLoader
{
    public async Task<Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        WorldSession session = sessions(bundle)
            ?? throw new InvalidOperationException($"no adopted session for account {bundle.AccountId} generation {bundle.SessionGeneration} on this worker");
        if (session.AccountId != bundle.AccountId)
        {
            throw new InvalidOperationException($"adopted session belongs to account {session.AccountId}, the transfer to account {bundle.AccountId}");
        }

        int characterId = checked((int)new ObjectGuid(bundle.CharacterGuid).Low);
        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        IWorldDataStore worldData = session.Services.GetRequiredService<IWorldDataStore>();
        CharacterRecord character = await characters.GetByIdAsync(characterId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"character {characterId} does not exist");
        if (character.AccountId != bundle.AccountId)
        {
            throw new InvalidOperationException($"character {characterId} is not owned by account {bundle.AccountId}");
        }

        RaceInfo raceInfo = await worldData.GetRaceInfoAsync(character.Race, character.Gender, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"missing race data for character {characterId}");
        ClassInfo classInfo = await worldData.GetClassInfoAsync(character.Class, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"missing class data for character {characterId}");
        HomeBind home = bundle.HomeBind;
        if (home.IsUnset)
        {
            home = new HomeBind(character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ);
        }

        // As the login handler: a character that never set a bind point starts at its race and class start position.
        if (home.IsUnset && await worldData.GetStartPositionAsync(character.Race, character.Class, cancellationToken).ConfigureAwait(false) is { } start)
        {
            home = new HomeBind(start.MapId, start.ZoneId, start.X, start.Y, start.Z);
        }

        if (home.IsUnset)
        {
            throw new InvalidOperationException($"character {characterId} has no bind point and its race and class have no start position");
        }

        IReadOnlyList<ActionButton> buttons = await characters.GetActionButtonsAsync(characterId, cancellationToken).ConfigureAwait(false);
        var player = new Player(character, CharacterPackets.BuildAppearance(raceInfo, classInfo), session, buttons)
        {
            Home = home,
            CharacterOwnership = session.World.CharacterOwnership,
        };

        await TransferShadowHooks.RunAsync(session.Services.GetServices<ICharacterHooks>(), session, character, player).ConfigureAwait(false);
        return player;
    }

    public void Release(Player shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        if (shadow.Session is WorldSession session)
        {
            // HonorFeature.OnPlayerLoadingAsync tracked the shadow in the shared honor service.
            session.Services.GetService<HonorFeature>()?.ActiveService?.Untrack(shadow);
        }
    }
}

/// <summary>
/// Production <see cref="ITransferSessionEntry"/> over the adopted <see cref="WorldSession"/> of the player (the shadow was built with it).
/// World thread. <see cref="TryAttach"/> needs the adopted session in <see cref="SessionState.CharacterSelect"/> (the same gate as a login).
/// </summary>
public sealed class WorldSessionTransferEntry : ITransferSessionEntry
{
    public bool TryAttach(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Session is not WorldSession session || !session.TryBeginLogin())
        {
            return false;
        }

        if (!session.TryEnterWorld(player))
        {
            session.AbortLogin();
            return false;
        }

        return true;
    }

    public void ResumeSaves(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Session is not WorldSession session)
        {
            return;
        }

        // As the login does once the player is in its map: the saves held for the character since capture resume.
        int id = checked((int)player.Guid.Low);
        session.Services.GetService<CharacterSaveQueue>()?.ResumeCharacter(id);
        session.Services.GetService<QuestNpcFeature>()?.Persistence.ResumeCharacter(id);
    }
}

/// <summary>
/// The production <see cref="ITransferTargetWorker"/> (Tier 3, default D4). The target of a transfer reloads the character from the characters
/// database (the source's settlement save is durable before the bundle exists) instead of deserialising live objects:
/// <list type="bullet">
/// <item><description><see cref="TryReserveSlotAsync"/>: refuses when <see cref="WorkerAdmission"/> is closed, when this worker does not own the target map copy,
/// when the character is already online here or reserved by another transfer, or when the slot limit is reached. Idempotent per transfer id.</description></item>
/// <item><description><see cref="InstallPassiveShadowAsync"/>: loads the shadow through <see cref="ITransferShadowLoader"/> once per transfer (concurrent and repeated calls share the load; a failed load is retried by the next call).
/// The shadow is in no map, is never ticked and nothing is written (see <see cref="ISkippedOnTransferShadowLoad"/>).</description></item>
/// <item><description><see cref="ActivateTargetPlayerAsync"/> (after the coordinator committed the new epoch): one world-thread command that adopts the target ownership token, places the
/// player at the target position, attaches the adopted session, registers the player online without a map and starts the retail far-teleport arrival
/// (<see cref="TeleportService.BeginClusterArrival"/>): SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD now, the login packets and the map add when the client's
/// MSG_MOVE_WORLDPORT_ACK arrives. The activation completes once the arrival is registered, not when the client has finished loading. A repeat for the same transfer id is a no-op.</description></item>
/// <item><description><see cref="DiscardPassiveShadowAsync"/>: drops the reservation and the shadow (undoing its shared registrations through <see cref="ITransferShadowLoader.Release"/>) and frees the slot; a discarded transfer id can never be installed or activated afterwards. A discard after or during activation does nothing.</description></item>
/// </list>
/// After a target restart the reservation is gone, so install and activate adopt the transfer from the bundle (the roll-forward of a committed transfer must complete).
/// Thread affinity: the interface methods may be called from any thread; world mutation runs through <see cref="WorldRuntime.InvokeAsync{T}"/>; the slot table is guarded by a lock.
/// </summary>
public sealed class WorkerTransferTarget : ITransferTargetWorker
{
    /// <summary>How many finished (activated or discarded) transfer ids are remembered to keep repeats idempotent.</summary>
    private const int RememberedFinished = 4096;

    private readonly WorkerNodeId _workerId;
    private readonly WorldRuntime _world;
    private readonly ITransferShadowLoader _loader;
    private readonly ITransferSessionEntry _entry;
    private readonly WorkerAdmission? _admission;
    private readonly int _maxReservations;
    private readonly ILogger _logger;
    private readonly Func<uint, ObjectGuid, uint>? _allocateInstance;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Slot> _slots = [];
    private readonly Dictionary<ulong, Guid> _byCharacter = [];
    private readonly FinishedSet _activated = new(RememberedFinished);
    private readonly FinishedSet _discarded = new(RememberedFinished);

    public WorkerTransferTarget(
        WorkerNodeId workerId,
        WorldRuntime world,
        ITransferShadowLoader loader,
        ITransferSessionEntry entry,
        WorkerAdmission? admission = null,
        int maxReservations = 64,
        Guid? incarnation = null,
        ILogger<WorkerTransferTarget>? logger = null,
        Func<uint, ObjectGuid, uint>? allocateInstance = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReservations, 1);
        _workerId = workerId;
        _world = world;
        _loader = loader;
        _entry = entry;
        _admission = admission;
        _maxReservations = maxReservations;
        Incarnation = incarnation ?? Guid.NewGuid();
        _logger = logger ?? NullLogger<WorkerTransferTarget>.Instance;
        _allocateInstance = allocateInstance;
    }

    public WorkerNodeId WorkerId => _workerId;

    public Guid Incarnation { get; }

    /// <summary>
    /// The <c>worker:&lt;shard&gt;</c> scope this world claims under (its own ownership token), which a committed transfer moves the character's claim to, so the source's retained saves are
    /// refused from the commit on (design _wf/writer-lock-DESIGN.md section 1b). Null in Realm scope (the realm-wide token is not a worker token): the claim keeps the source's scope.
    /// </summary>
    public string? OwnershipScope => _world.CharacterOwnership is { } token && ArcaneCore.Data.Characters.Ownership.CharacterOwnershipScopes.IsWorker(token.Scope) ? token.Scope : null;

    /// <summary>Transfers that hold a slot (reserved or shadowed, not yet activated or discarded).</summary>
    public int ReservedCount
    {
        get { lock (_gate) return _slots.Count; }
    }

    /// <summary>Whether a passive shadow is loaded for <paramref name="transferId"/>.</summary>
    public bool HasShadow(Guid transferId)
    {
        lock (_gate) return _slots.TryGetValue(transferId, out Slot? slot) && slot.Loading is { IsCompletedSuccessfully: true };
    }

    /// <summary>Whether <paramref name="transferId"/> was activated here (remembered for the last few thousand transfers).</summary>
    public bool WasActivated(Guid transferId)
    {
        lock (_gate) return _activated.Contains(transferId);
    }

    public async Task<bool> TryReserveSlotAsync(
        Guid transferId,
        ulong characterGuid,
        uint targetMapId,
        uint targetInstanceId,
        CancellationToken cancellationToken = default) =>
        (await ReserveSlotAsync(transferId, characterGuid, targetMapId, targetInstanceId, cancellationToken).ConfigureAwait(false)).Reserved;

    /// <summary>
    /// Stage 1 with the instance binding of orchestrator decision Q8: when <paramref name="targetInstanceId"/> is 0 and the map is an instanced one (the instance allocator answers a
    /// non-zero id), this worker allocates and binds an instance for the character and answers its id; every other request is answered with the requested id. The id is remembered with
    /// the slot, so a repeat of the same transfer id answers the same instance.
    /// </summary>
    public async Task<TransferSlotReservation> ReserveSlotAsync(
        Guid transferId,
        ulong characterGuid,
        uint targetMapId,
        uint targetInstanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransferSlotReservation refused = new(false, targetInstanceId);
        if (_admission is { IsOpen: false } || _world.IsOnline(new ObjectGuid(characterGuid)))
        {
            return refused;
        }

        bool allocate = targetInstanceId == 0 && _allocateInstance is not null;
        // An instance-0 request for an instanced map names no copy yet: the worker must own instance copies of the map (any non-zero id probes that).
        if (!_world.Owns(targetMapId, allocate ? InstanceManager.FirstInstanceId : targetInstanceId))
        {
            return refused;
        }

        lock (_gate)
        {
            if (_discarded.Contains(transferId))
            {
                return refused;
            }

            if (_activated.Contains(transferId))
            {
                return new TransferSlotReservation(true, targetInstanceId); // a repeated stage 1 of a transfer that already completed
            }

            if (_slots.TryGetValue(transferId, out Slot? existing))
            {
                return new TransferSlotReservation(existing.CharacterGuid == characterGuid, existing.InstanceId);
            }

            if (_byCharacter.ContainsKey(characterGuid) || _slots.Count >= _maxReservations)
            {
                return refused;
            }

            if (!allocate)
            {
                AddSlot(new Slot(transferId, characterGuid) { InstanceId = targetInstanceId });
                return new TransferSlotReservation(true, targetInstanceId);
            }
        }

        // The instance manager is world-thread state: allocate and bind there, outside the slot lock.
        Func<uint, ObjectGuid, uint> allocator = _allocateInstance!;
        var guid = new ObjectGuid(characterGuid);
        uint allocated = await _world.InvokeAsync(() => allocator(targetMapId, guid), CancellationToken.None).ConfigureAwait(false);
        // The allocator answers 0 for a non-instanced map: the request then stands as instance 0 and must pass the plain ownership check (the probe above used a non-zero id,
        // which MapOwnership accepts for any map while instance maps are allowed).
        if (!_world.Owns(targetMapId, allocated))
        {
            return refused;
        }

        lock (_gate)
        {
            if (_discarded.Contains(transferId))
            {
                return refused;
            }

            if (_slots.TryGetValue(transferId, out Slot? raced))
            {
                return new TransferSlotReservation(raced.CharacterGuid == characterGuid, raced.InstanceId);
            }

            if (_byCharacter.ContainsKey(characterGuid) || _slots.Count >= _maxReservations)
            {
                return refused;
            }

            AddSlot(new Slot(transferId, characterGuid) { InstanceId = allocated });
            return new TransferSlotReservation(true, allocated);
        }
    }

    public async Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBundle(transferId, bundle);
        Slot? slot = TryBeginInstall(transferId, bundle, out Task<Player>? load);
        if (slot is null)
        {
            return; // already activated
        }

        await AwaitLoadAsync(slot, load!, cancellationToken).ConfigureAwait(false);
    }

    public Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Slot? removed;
        lock (_gate)
        {
            if (_activated.Contains(transferId))
            {
                _logger.LogWarning("Transfer {TransferId} was activated here; its discard ({Reason}) is ignored", transferId, abortReason);
                return Task.CompletedTask;
            }

            if (_slots.TryGetValue(transferId, out Slot? activating) && activating.Activating)
            {
                _logger.LogWarning("Transfer {TransferId} is activating here; its discard ({Reason}) is ignored", transferId, abortReason);
                return Task.CompletedTask;
            }

            removed = RemoveSlot(transferId);
            _discarded.Add(transferId);
        }

        if (removed is not null)
        {
            _logger.LogInformation("Transfer {TransferId} discarded on {Worker}: {Reason}", transferId, _workerId, abortReason);
        }

        // The shadow was never in a map or the online registry: dropping the reference is the teardown, plus undoing what its load registered
        // in shared services (the honor service's tracking). A load still running is released when it finishes.
        if (removed?.Loading is { } loading)
        {
            ReleaseWhenLoaded(loading);
        }

        return Task.CompletedTask;
    }

    private void ReleaseWhenLoaded(Task<Player> load)
    {
        if (load.IsCompleted)
        {
            ReleaseFinished(load);
            return;
        }

        _ = load.ContinueWith(ReleaseFinished, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ReleaseFinished(Task<Player> load)
    {
        if (load.IsFaulted)
        {
            _ = load.Exception; // observed: a discarded load that failed is not an unobserved task exception
        }
        else if (load.IsCompletedSuccessfully)
        {
            ReleaseShadow(load.Result);
        }
    }

    private void ReleaseShadow(Player shadow)
    {
        try
        {
            _loader.Release(shadow);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transfer-target-release", ex);
            _logger.LogError(ex, "Releasing the dropped shadow of character {Guid} failed", shadow.Guid.Value);
        }
    }

    public async Task ActivateTargetPlayerAsync(
        Guid transferId,
        PlayerTransferBundle bundle,
        CharacterOwnershipToken targetOwnershipToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(targetOwnershipToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBundle(transferId, bundle);
        Slot? slot = TryBeginInstall(transferId, bundle, out Task<Player>? load);
        if (slot is null)
        {
            return; // a duplicate activate: no-op
        }

        Player shadow = await AwaitLoadAsync(slot, load!, cancellationToken).ConfigureAwait(false);

        // The command must not lose a race to cancellation once it may have started adding the player.
        await _world.InvokeAsync(() => ActivateOnWorldThread(slot, bundle, targetOwnershipToken, shadow), CancellationToken.None).ConfigureAwait(false);
    }

    private bool ActivateOnWorldThread(Slot slot, PlayerTransferBundle bundle, CharacterOwnershipToken token, Player shadow)
    {
        lock (_gate)
        {
            if (_activated.Contains(slot.TransferId))
            {
                return false; // a concurrent duplicate got here first
            }

            if (_discarded.Contains(slot.TransferId))
            {
                throw new InvalidOperationException($"transfer '{slot.TransferId}' was discarded while it was activating");
            }

            slot.Activating = true; // from here a discard of this transfer is ignored
        }

        if (_world.FindOnlinePlayer(shadow.Guid) is { } online)
        {
            if (ReferenceEquals(online, shadow))
            {
                Finish(slot); // a repeat after the arrival was registered but the caller did not hear of it
                return false;
            }

            if (online.CharacterOwnership == token)
            {
                // A very late duplicate of a transfer whose id is no longer remembered: its character is already here under this very token.
                ReleaseShadow(shadow);
                Finish(slot);
                return false;
            }

            slot.Activating = false;
            throw new InvalidOperationException($"character {bundle.CharacterGuid} is already online on worker '{_workerId}'");
        }

        if (_world.TryGetMap(bundle.TargetMapId, bundle.TargetInstanceId, out Map? map) != MapAccessResult.Granted || map is null)
        {
            slot.Activating = false;
            throw new InvalidOperationException($"worker '{_workerId}' does not own map {bundle.TargetMapId} instance {bundle.TargetInstanceId}");
        }

        TeleportService? teleports = TeleportService.Of(_world);
        if (teleports is null)
        {
            slot.Activating = false;
            throw new InvalidOperationException($"worker '{_workerId}' has no teleport service attached");
        }

        TransferPosition at = bundle.TargetPosition;
        shadow.CharacterOwnership = token;
        // The claim moved to this worker with the commit: the activated player gets its own claim ticket, so its logout release is invalidated by any newer login's claim
        // (a quick relogin that claims before the posted release runs) exactly as for a character that logged in here.
        CharacterClaimService? claims = (shadow.Session as WorldSession)?.Services.GetService<CharacterClaimService>();
        int characterId = (int)shadow.Guid.Low;
        long claimTicket = claims?.IssueTicketForReceivedClaim(characterId, token) ?? 0;
        claims?.BindTicket(shadow, claimTicket);
        shadow.MapId = bundle.TargetMapId;
        shadow.Relocate(at.X, at.Y, at.Z, at.Orientation, _world.NowMs);

        if (!_entry.TryAttach(shadow))
        {
            _logger.LogInformation("Transfer {TransferId}: the client of character {Character} left before activation; nothing to add", slot.TransferId, bundle.CharacterGuid);
            claims?.AbandonTicket(characterId, claimTicket);
            ReleaseShadow(shadow);
            Finish(slot);
            return false;
        }

        // Retail far-teleport order: vmangos Player::ExecuteTeleportFar sends SMSG_TRANSFER_PENDING then SMSG_NEW_WORLD (Player.cpp:2068-2118),
        // and the map add with both login packet groups runs only when MSG_MOVE_WORLDPORT_ACK arrives (HandleMoveWorldportAckOpcode,
        // MovementHandler.cpp:112-146). ArcaneCore's own far teleport does the same; the transfer reuses it (TeleportService.BeginClusterArrival).
        // vmangos has no cross-worker transfer, so the sequence is the far teleport's, not a login's.
        try
        {
            _world.RegisterPlayerForTransferArrival(shadow);
            if (!teleports.BeginClusterArrival(shadow, map, new TeleportDestination(bundle.TargetMapId, at.X, at.Y, at.Z, at.Orientation), new Arrival(_entry, _world)))
            {
                throw new InvalidOperationException($"the arrival of character {bundle.CharacterGuid} could not be started");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leave a session "in world" with a player that is in the registry but will never reach a map: undo the registration and kick.
            FaultLedger.Shared.Record("cluster.transfer-target", ex);
            claims?.AbandonTicket(characterId, claimTicket);
            teleports.Forget(shadow);
            _world.DetachPlayerForTransfer(shadow);
            shadow.Session.Kick("transfer target could not start the arrival");
            slot.Activating = false;
            throw;
        }

        Finish(slot);
        return true;
    }

    /// <summary>The arrival callbacks of the teleport service: the held saves resume once the player is in its map (and when the arrival is abandoned), then the login notification.</summary>
    private sealed class Arrival(ITransferSessionEntry entry, WorldRuntime world) : ITransferArrivalObserver
    {
        public void MapAdded(Player player) => entry.ResumeSaves(player);

        public void Entered(Player player) => world.NotifyLoggedIn(player);

        public void Abandoned(Player player) => entry.ResumeSaves(player);
    }

    /// <summary>Finds or adopts the slot of a transfer and starts (or joins) its shadow load; null when the transfer is already activated.</summary>
    private Slot? TryBeginInstall(Guid transferId, PlayerTransferBundle bundle, out Task<Player>? load)
    {
        lock (_gate)
        {
            load = null;
            if (_activated.Contains(transferId))
            {
                return null;
            }

            if (_discarded.Contains(transferId))
            {
                throw new InvalidOperationException($"transfer '{transferId}' was discarded on worker '{_workerId}'");
            }

            if (!_slots.TryGetValue(transferId, out Slot? slot))
            {
                // The reservation is gone (this worker restarted after the reserve): adopt the transfer from the bundle.
                if (!_world.Owns(bundle.TargetMapId, bundle.TargetInstanceId))
                {
                    throw new InvalidOperationException($"worker '{_workerId}' does not own map {bundle.TargetMapId} instance {bundle.TargetInstanceId}");
                }

                if (_byCharacter.TryGetValue(bundle.CharacterGuid, out Guid other))
                {
                    throw new InvalidOperationException($"character {bundle.CharacterGuid} is reserved by transfer '{other}', not '{transferId}'");
                }

                slot = new Slot(transferId, bundle.CharacterGuid);
                AddSlot(slot);
            }
            else if (slot.CharacterGuid != bundle.CharacterGuid)
            {
                throw new InvalidOperationException($"transfer '{transferId}' reserved character {slot.CharacterGuid}, the bundle carries {bundle.CharacterGuid}");
            }

            // One shared load per transfer; it does not take the first caller's cancellation, which only abandons its own wait.
            slot.Loading ??= Task.Run(() => _loader.LoadAsync(bundle, CancellationToken.None)); // on the pool: the loader's synchronous prefix does not run under the gate
            load = slot.Loading;
            return slot;
        }
    }

    private async Task<Player> AwaitLoadAsync(Slot slot, Task<Player> load, CancellationToken cancellationToken)
    {
        try
        {
            return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (load.IsFaulted)
            {
                // The next install or activate loads again; the reservation stays.
                lock (_gate)
                {
                    if (ReferenceEquals(slot.Loading, load))
                    {
                        slot.Loading = null;
                    }
                }

                FaultLedger.Shared.Record("cluster.transfer-target", ex);
            }

            throw;
        }
    }

    private void Finish(Slot slot)
    {
        lock (_gate)
        {
            RemoveSlot(slot.TransferId);
            _activated.Add(slot.TransferId);
        }
    }

    private void AddSlot(Slot slot)
    {
        _slots[slot.TransferId] = slot;
        _byCharacter[slot.CharacterGuid] = slot.TransferId;
    }

    private Slot? RemoveSlot(Guid transferId)
    {
        if (!_slots.Remove(transferId, out Slot? slot))
        {
            return null;
        }

        if (_byCharacter.TryGetValue(slot.CharacterGuid, out Guid owner) && owner == transferId)
        {
            _byCharacter.Remove(slot.CharacterGuid);
        }

        return slot;
    }

    private void ValidateBundle(Guid transferId, PlayerTransferBundle bundle)
    {
        if (bundle.TransferId != transferId)
        {
            throw new ArgumentException($"bundle belongs to transfer '{bundle.TransferId}', not '{transferId}'", nameof(bundle));
        }

        if (bundle.TargetWorkerId != _workerId)
        {
            throw new ArgumentException($"bundle targets worker '{bundle.TargetWorkerId}', this is '{_workerId}'", nameof(bundle));
        }
    }

    private sealed class Slot(Guid transferId, ulong characterGuid)
    {
        public Guid TransferId { get; } = transferId;
        public ulong CharacterGuid { get; } = characterGuid;

        /// <summary>The instance id the reservation answered (the requested one, or the one this worker allocated for an instance-0 request).</summary>
        public uint InstanceId { get; init; }

        /// <summary>The shadow load (null until the first install; guarded by the target's gate).</summary>
        public Task<Player>? Loading { get; set; }

        /// <summary>Set (under the target's gate) when the activation command began; a discard of the transfer is ignored from then on.</summary>
        public bool Activating
        {
            get => _activating;
            set => _activating = value;
        }

        private volatile bool _activating;
    }

    /// <summary>A bounded set of transfer ids, oldest forgotten first. Guarded by the target's gate.</summary>
    private sealed class FinishedSet(int capacity)
    {
        private readonly HashSet<Guid> _set = [];
        private readonly Queue<Guid> _order = new();

        public bool Contains(Guid id) => _set.Contains(id);

        public void Add(Guid id)
        {
            if (!_set.Add(id))
            {
                return;
            }

            _order.Enqueue(id);
            while (_order.Count > capacity)
            {
                _set.Remove(_order.Dequeue());
            }
        }
    }
}
