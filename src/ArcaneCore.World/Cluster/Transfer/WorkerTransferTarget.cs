using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
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
/// <param name="services">The world's root provider. The saves resume through it, not through the session's: a client that leaves during the arrival ends the session's scope before the logout abandons the arrival on the world thread.</param>
public sealed class WorldSessionTransferEntry(IServiceProvider services) : ITransferSessionEntry
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

        // As the login does once the player is in its map: the saves held for the character since capture resume.
        int id = checked((int)player.Guid.Low);
        services.GetService<CharacterSaveQueue>()?.ResumeCharacter(id);
        services.GetService<QuestNpcFeature>()?.Persistence.ResumeCharacter(id);
    }
}

/// <summary>
/// The instance side of a transfer's reservation on the target worker (orchestrator decision Q8: the target allocates instance ids). <see cref="Reserve"/>, <see cref="Release"/> and <see cref="Bind"/>
/// run on the world thread; <see cref="IsInstanced"/> reads immutable data and may run anywhere.
/// </summary>
public interface ITransferInstanceAllocator
{
    /// <summary>Whether an instance-0 request for <paramref name="mapId"/> needs an allocated instance (a dungeon). False for continents: the world thread is not visited for them.</summary>
    bool IsInstanced(uint mapId);

    /// <summary>The instance <paramref name="player"/> will enter on <paramref name="mapId"/>: its bound one, else a new one held for the transfer. 0 when the map is not instanced.</summary>
    uint Reserve(ObjectGuid player, uint mapId);

    /// <summary>Undoes <see cref="Reserve"/> of a reservation that was refused or aborted. Idempotent; leaves an instance somebody is bound to alone.</summary>
    void Release(uint mapId, uint instanceId);

    /// <summary>Binds <paramref name="player"/> to the reserved instance once the transfer committed (the bind write is refused by the worker write fence before). False when the instance is gone or the character is bound elsewhere.</summary>
    bool Bind(ObjectGuid player, uint mapId, uint instanceId);
}

/// <summary>The production <see cref="ITransferInstanceAllocator"/>: the world's <see cref="InstanceManager"/> (<see cref="InstanceManager.ReserveEntryInstance"/>, <see cref="InstanceManager.ReleaseEntryInstance"/>, <see cref="InstanceManager.BindReservedEntry"/>).</summary>
public sealed class InstanceManagerTransferAllocator(InstanceManager instances) : ITransferInstanceAllocator
{
    private readonly InstanceManager _instances = instances ?? throw new ArgumentNullException(nameof(instances));

    public bool IsInstanced(uint mapId) => _instances.IsInstancedMap(mapId);

    public uint Reserve(ObjectGuid player, uint mapId) => _instances.ReserveEntryInstance(player, mapId);

    public void Release(uint mapId, uint instanceId) => _instances.ReleaseEntryInstance(mapId, instanceId);

    public bool Bind(ObjectGuid player, uint mapId, uint instanceId) => _instances.BindReservedEntry(player, mapId, instanceId);
}

/// <summary>An allocator that is only a function: every map may be instanced, nothing to undo or bind (the function's owner keeps the instance).</summary>
internal sealed class DelegateTransferInstanceAllocator(Func<uint, ObjectGuid, uint> allocate) : ITransferInstanceAllocator
{
    public bool IsInstanced(uint mapId) => true;

    public uint Reserve(ObjectGuid player, uint mapId) => allocate(mapId, player);

    public void Release(uint mapId, uint instanceId)
    {
    }

    public bool Bind(ObjectGuid player, uint mapId, uint instanceId) => true;
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
/// Deferred shadow load (design _wf/handoff-control-DESIGN.md section 4.4; only with a <c>sessionReady</c> probe, which production passes): the coordinator's stage 3 runs before stage 4, where the gateway opens the client's
/// session on this worker, and the production loader needs that session. So <see cref="InstallPassiveShadowAsync"/> answers at once while the account's session is not open here (the bundle is remembered as install-requested),
/// <see cref="OnSessionOpened"/> starts the load when the session opens, and the install after the commit joins that running load before the activation. A load failure then surfaces at that second install (after the commit, where recovery repeats it). If the session never opens, that second install is a no-op too (it answers Ok without loading) and the failure surfaces at the activation, which requires the session (design R6/Q7).
/// Without the probe (every test double and Combined mode) an install loads at once, as before.
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
    private readonly ITransferInstanceAllocator? _instances;
    private readonly Func<PlayerTransferBundle, bool>? _sessionReady;
    private readonly ITransferOfflineArrival? _offline;
    private readonly CharacterClaimService? _claims;
    private readonly Dictionary<int, TaskCompletionSource> _sessionWaiters = [];
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
        Func<uint, ObjectGuid, uint>? allocateInstance = null,
        ITransferInstanceAllocator? instances = null,
        Func<PlayerTransferBundle, bool>? sessionReady = null,
        ITransferOfflineArrival? offlineArrival = null,
        CharacterClaimService? claims = null)
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
        _instances = instances ?? (allocateInstance is null ? null : new DelegateTransferInstanceAllocator(allocateInstance));
        _sessionReady = sessionReady;
        _offline = offlineArrival;
        _claims = claims;
    }

    /// <summary>
    /// How long an activation whose client session is not adopted here yet waits for it before taking the character offline. The worker's open reply reaches the gateway before
    /// the accepted session is started and entered in the acceptor (GatewaySessionLinkHost.OpenAsync: LinkSession.Accept, then RemoteWorldSession.Start), so the gateway's
    /// stage 4 ack, and the activation that follows it, can arrive while the session is still between the two. "Not adopted yet" lasts that long at most; "gone" lasts forever.
    /// </summary>
    internal TimeSpan SessionOpenGrace { get; init; } = TimeSpan.FromSeconds(2);

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
    /// non-zero id), this worker reserves an instance for the character (bound at activation, released when the reservation is refused, loses a race or is discarded) and answers its id; every other request is answered with the requested id. The id is remembered with
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
        // A repeated stage 1 of a transfer that already completed here is answered with the instance it entered before anything else: the activated player is online now,
        // which would refuse the replay below.
        lock (_gate)
        {
            if (_activated.TryGetInstance(transferId, out uint completedInstance))
            {
                return new TransferSlotReservation(true, completedInstance);
            }
        }

        if (_admission is { IsOpen: false } || _world.IsOnline(new ObjectGuid(characterGuid)))
        {
            return refused;
        }

        // A continent never needs an allocated instance: the registry pre-check keeps it off the world thread.
        bool allocate = targetInstanceId == 0 && _instances is { } instanced && instanced.IsInstanced(targetMapId);
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

            if (_activated.TryGetInstance(transferId, out uint activatedInstance))
            {
                return new TransferSlotReservation(true, activatedInstance); // a repeated stage 1 of a transfer that already completed: the instance it entered, not the requested one
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
        ITransferInstanceAllocator allocator = _instances!;
        var guid = new ObjectGuid(characterGuid);
        uint allocated = await _world.InvokeAsync(() => allocator.Reserve(guid, targetMapId), CancellationToken.None).ConfigureAwait(false);
        // The allocator answers 0 for a non-instanced map: the request then stands as instance 0 and must pass the plain ownership check (the probe above used a non-zero id,
        // which MapOwnership accepts for any map while instance maps are allowed).
        if (!_world.Owns(targetMapId, allocated))
        {
            await ReleaseInstanceAsync(targetMapId, allocated).ConfigureAwait(false);
            return refused;
        }

        TransferSlotReservation answer;
        bool keep = false;
        lock (_gate)
        {
            if (_discarded.Contains(transferId))
            {
                answer = refused;
            }
            else if (_slots.TryGetValue(transferId, out Slot? raced))
            {
                answer = new TransferSlotReservation(raced.CharacterGuid == characterGuid, raced.InstanceId);
                keep = raced.InstanceId == allocated; // the winner may have reserved the very same (bound) instance
            }
            else if (_byCharacter.ContainsKey(characterGuid) || _slots.Count >= _maxReservations)
            {
                answer = refused;
            }
            else
            {
                AddSlot(new Slot(transferId, characterGuid) { InstanceId = allocated, MapId = targetMapId, Allocated = allocated != 0 });
                return new TransferSlotReservation(true, allocated);
            }
        }

        // The slot race was lost (or the transfer was discarded meanwhile): this call's allocation is not held by any slot, so undo it.
        if (!keep)
        {
            await ReleaseInstanceAsync(targetMapId, allocated).ConfigureAwait(false);
        }

        return answer;
    }

    /// <summary>Undoes an instance reservation on the world thread. A failure is recorded and swallowed: the caller's refusal or discard stands, and the held map expires with the process at worst.</summary>
    private async Task ReleaseInstanceAsync(uint mapId, uint instanceId)
    {
        if (instanceId == 0 || _instances is not { } allocator)
        {
            return;
        }

        try
        {
            await _world.InvokeAsync(() =>
            {
                allocator.Release(mapId, instanceId);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transfer-target-instance", ex);
            _logger.LogError(ex, "Releasing the reserved instance {Instance} of map {Map} failed", instanceId, mapId);
        }
    }

    public async Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBundle(transferId, bundle);
        Slot? slot = TryBeginInstall(transferId, bundle, deferUntilSession: _sessionReady is not null, out Task<Player>? load);
        if (slot is null || load is null)
        {
            return; // already activated, or deferred until the account's session opens here (OnSessionOpened starts the load)
        }

        await AwaitLoadAsync(slot, load, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The account's gateway session was opened on this worker (stage 4 of a transfer): starts the shadow load of every transfer whose install was deferred for it (section 4.4). Any thread; never throws. A load that fails is
    /// recorded and forgotten, so the install after the commit (or the activation) loads again and reports the failure to its caller.
    /// </summary>
    public void OnSessionOpened(int accountId)
    {
        if (_sessionReady is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_sessionWaiters.Remove(accountId, out TaskCompletionSource? waiter))
            {
                waiter.TrySetResult(); // an activation waiting for this session (WaitForSessionAsync) goes on, on its own thread
            }

            foreach (Slot slot in _slots.Values)
            {
                if (slot.DeferredBundle is not { } bundle || bundle.AccountId != accountId || slot.Loading is not null || slot.Activating || !SessionReady(bundle))
                {
                    continue;
                }

                Task<Player> load = StartLoad(slot, bundle);
                _ = load.ContinueWith(done => ObserveDeferredLoad(slot, done), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private void ObserveDeferredLoad(Slot slot, Task<Player> done)
    {
        if (!done.IsFaulted)
        {
            return;
        }

        Exception? error = done.Exception?.GetBaseException();
        lock (_gate)
        {
            if (ReferenceEquals(slot.Loading, done))
            {
                slot.Loading = null; // the next install or activate loads again
            }
        }

        if (error is not null)
        {
            FaultLedger.Shared.Record("cluster.transfer-target", error);
            _logger.LogWarning(error, "The deferred shadow load of character {Guid} for transfer {TransferId} failed; the install after the commit repeats it", slot.CharacterGuid, slot.TransferId);
        }
    }

    public async Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Slot? removed;
        lock (_gate)
        {
            if (_activated.Contains(transferId))
            {
                _logger.LogWarning("Transfer {TransferId} was activated here; its discard ({Reason}) is ignored", transferId, abortReason);
                return;
            }

            if (_slots.TryGetValue(transferId, out Slot? activating) && activating.Activating)
            {
                _logger.LogWarning("Transfer {TransferId} is activating here; its discard ({Reason}) is ignored", transferId, abortReason);
                return;
            }

            removed = RemoveSlot(transferId);
            _discarded.Add(transferId, 0);
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

        // The instance the reservation allocated goes with it (an instance somebody is bound to stays).
        if (removed is { Allocated: true })
        {
            await ReleaseInstanceAsync(removed.MapId, removed.InstanceId).ConfigureAwait(false);
        }
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
        if (_offline is not null && _sessionReady is not null && !await WaitForSessionAsync(bundle, cancellationToken).ConfigureAwait(false))
        {
            // The commit is past and the client's session is not here (it closed, or this worker restarted): the player cannot be built, so the character arrives offline.
            await ActivateOfflineAsync(transferId, bundle, targetOwnershipToken, cancellationToken).ConfigureAwait(false);
            return;
        }

        Slot? slot = TryBeginInstall(transferId, bundle, deferUntilSession: false, out Task<Player>? load);
        if (slot is null)
        {
            return; // a duplicate activate: no-op
        }

        Player shadow = await AwaitLoadAsync(slot, load!, cancellationToken).ConfigureAwait(false);

        // The command must not lose a race to cancellation once it may have started adding the player.
        await _world.InvokeAsync(() => ActivateOnWorldThread(slot, bundle, targetOwnershipToken, shadow), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Activation of a committed transfer without the client (design _wf/handoff-control-DESIGN.md section 6): the character's stored position becomes the transfer's target through
    /// <see cref="ITransferOfflineArrival"/>, written under this worker's token, and the transfer finishes like an activated one (the slot, the shadow and an instance the reservation
    /// allocated are released). A character that is online here already keeps its live state (nothing is written). A refused or failed write throws and leaves the slot for the retry.
    /// </summary>
    private async Task ActivateOfflineAsync(Guid transferId, PlayerTransferBundle bundle, CharacterOwnershipToken token, CancellationToken cancellationToken)
    {
        Slot slot;
        lock (_gate)
        {
            if (_activated.Contains(transferId))
            {
                return; // a duplicate activate: no-op
            }

            if (_discarded.Contains(transferId))
            {
                throw new InvalidOperationException($"transfer '{transferId}' was discarded on worker '{_workerId}'");
            }

            if (!_slots.TryGetValue(transferId, out Slot? found))
            {
                // The reservation is gone (this worker restarted after the reserve): adopt the transfer from the bundle, as an install does.
                if (_byCharacter.TryGetValue(bundle.CharacterGuid, out Guid other))
                {
                    throw new InvalidOperationException($"character {bundle.CharacterGuid} is reserved by transfer '{other}', not '{transferId}'");
                }

                found = new Slot(transferId, bundle.CharacterGuid) { InstanceId = bundle.TargetInstanceId };
                AddSlot(found);
            }
            else if (found.CharacterGuid != bundle.CharacterGuid)
            {
                throw new InvalidOperationException($"transfer '{transferId}' reserved character {found.CharacterGuid}, the bundle carries {bundle.CharacterGuid}");
            }

            if (found.Activating)
            {
                throw new InvalidOperationException($"transfer '{transferId}' is already activating on worker '{_workerId}'");
            }

            found.Activating = true; // from here a discard of this transfer is ignored
            slot = found;
        }

        try
        {
            if (PlayerTokenFor(token) is not { } playerToken)
            {
                throw new InvalidOperationException(
                    $"transfer '{transferId}' committed character {bundle.CharacterGuid} to {token.Scope}/{token.Incarnation:D}, but worker '{_workerId}' writes as {_world.CharacterOwnership?.Scope}/{_world.CharacterOwnership?.Incarnation:D}");
            }

            var guid = new ObjectGuid(bundle.CharacterGuid);
            (bool online, uint zoneId) = await _world.InvokeAsync(
                () => (_world.FindOnlinePlayer(guid) is not null, TargetZone(bundle)), cancellationToken).ConfigureAwait(false);
            if (!online)
            {
                await _offline!.CompleteAsync(bundle, playerToken, zoneId, cancellationToken).ConfigureAwait(false);

                // Nobody plays the character here, so no logout will release the claim the commit gave this worker: release it once the transfer is marked completed,
                // or every login of the character on another worker is refused as a live owner's.
                _claims?.ScheduleReleaseOfReceivedClaim(checked((int)guid.Low), playerToken, () => _world.IsOnline(guid));
            }

            _logger.LogInformation(
                online
                    ? "Transfer {TransferId}: character {Character} is online on {Worker} already; the activation without the client writes nothing"
                    : "Transfer {TransferId}: the client of character {Character} is gone; the character arrived offline on {Worker}",
                transferId, bundle.CharacterGuid, _workerId);
        }
        catch
        {
            slot.Activating = false;
            throw;
        }

        Task<Player>? loading;
        lock (_gate)
        {
            loading = slot.Loading;
            RemoveSlot(transferId);
            _activated.Add(transferId, slot.InstanceId);
        }

        if (loading is not null)
        {
            ReleaseWhenLoaded(loading);
        }

        if (slot.Allocated)
        {
            await ReleaseInstanceAsync(slot.MapId, slot.InstanceId).ConfigureAwait(false);
        }
    }

    /// <summary>World thread: the zone of the transfer's target position from the target map's terrain, or 0 when the terrain does not know it (no map data).</summary>
    private uint TargetZone(PlayerTransferBundle bundle)
    {
        try
        {
            TransferPosition at = bundle.TargetPosition;
            return WorldMaps.Of(_world).Terrain.For(bundle.TargetMapId).GetZoneAndAreaId(at.X, at.Y, at.Z).ZoneId;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transfer-target-zone", ex);
            return 0;
        }
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

            if (online.CharacterOwnership is { } onlineToken && (onlineToken == token || onlineToken == PlayerTokenFor(token)))
            {
                // A very late duplicate of a transfer whose id is no longer remembered: its character is already here under this very token.
                ReleaseShadow(shadow);
                ReleaseUnboundInstance(slot);
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

        // The arrived player's snapshot token must be this world's own ownership token: the write fence verifies a snapshot's token against the scope's
        // character_ownership row, and the claim check proves the claim row names this scope and incarnation (writer-lock design section 1b). The committed
        // token names this scope and incarnation; a store that bumped only the per-character epoch (the in-memory store) is mapped back to the world's token here.
        // A commit to another scope or incarnation (this worker restarted since) is never adopted: the character could not be written here.
        if (PlayerTokenFor(token) is not { } playerToken)
        {
            slot.Activating = false;
            throw new InvalidOperationException(
                $"transfer '{slot.TransferId}' committed character {bundle.CharacterGuid} to {token.Scope}/{token.Incarnation:D}, but worker '{_workerId}' writes as {_world.CharacterOwnership?.Scope}/{_world.CharacterOwnership?.Incarnation:D}");
        }

        TransferPosition at = bundle.TargetPosition;
        shadow.CharacterOwnership = playerToken;
        // The claim moved to this worker with the commit: the activated player gets its own claim ticket, so its logout release is invalidated by any newer login's claim
        // (a quick relogin that claims before the posted release runs) exactly as for a character that logged in here.
        CharacterClaimService? claims = (shadow.Session as WorldSession)?.Services.GetService<CharacterClaimService>();
        int characterId = (int)shadow.Guid.Low;
        long claimTicket = claims?.IssueTicketForReceivedClaim(characterId, token) ?? 0;
        claims?.BindTicket(shadow, claimTicket);
        shadow.MapId = bundle.TargetMapId;
        shadow.Relocate(at.X, at.Y, at.Z, at.Orientation, _world.NowMs);
        ApplyTransportIntent(shadow, bundle.NotLeaveTransport);

        if (!_entry.TryAttach(shadow))
        {
            _logger.LogInformation("Transfer {TransferId}: the client of character {Character} left before activation; nothing to add", slot.TransferId, bundle.CharacterGuid);
            claims?.AbandonTicket(characterId, claimTicket);
            ReleaseShadow(shadow);
            ReleaseUnboundInstance(slot);
            Finish(slot);
            return false;
        }

        if (slot.Allocated)
        {
            // The claim moved here with the commit, so this bind write is accepted (at Reserve the source still held the claim and the worker write fence refused it).
            BindReservedInstance(slot, shadow);
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

    /// <summary>
    /// Placement of a transferred player's stored transport seat (loaded from the database with the shadow): a far teleport leaves the transport unless told to
    /// stay (vmangos Player::TeleportTo without TELE_TO_NOT_LEAVE_TRANSPORT, Player.cpp:1868-1872), so the seat is dropped; with the stay-aboard intent it is kept and
    /// the map add boards the player if its ship sails on the target map. World thread.
    /// </summary>
    internal static void ApplyTransportIntent(Player shadow, bool notLeaveTransport)
    {
        if (!notLeaveTransport)
        {
            shadow.LoginTransportSeat = null;
        }
    }

    /// <summary>
    /// The snapshot token an arrived player writes under for the committed <paramref name="committed"/>: this world's own ownership token when it names the same scope
    /// and incarnation (whatever per-character epoch the store recorded), the committed token when this world has none (fencing off), and null when the commit names
    /// another scope or incarnation (this worker cannot write the character).
    /// </summary>
    private CharacterOwnershipToken? PlayerTokenFor(CharacterOwnershipToken committed)
    {
        if (_world.CharacterOwnership is not { } own || own == committed)
        {
            return committed;
        }

        return string.Equals(own.Scope, committed.Scope, StringComparison.Ordinal) && own.Incarnation == committed.Incarnation ? own : null;
    }

    /// <summary>On the world thread: drops the instance this slot's reservation allocated when the activation ends without binding it (the held map would otherwise never unload). An instance somebody is bound to stays.</summary>
    private void ReleaseUnboundInstance(Slot slot)
    {
        if (!slot.Allocated || slot.InstanceId == 0 || _instances is not { } allocator)
        {
            return;
        }

        try
        {
            allocator.Release(slot.MapId, slot.InstanceId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transfer-target-instance", ex);
            _logger.LogError(ex, "Releasing the reserved instance {Instance} of map {Map} failed", slot.InstanceId, slot.MapId);
        }
    }

    private void BindReservedInstance(Slot slot, Player shadow)
    {
        try
        {
            if (!_instances!.Bind(shadow.Guid, slot.MapId, slot.InstanceId))
            {
                _logger.LogWarning("Transfer {TransferId}: character {Character} could not be bound to the reserved instance {Instance} of map {Map}", slot.TransferId, shadow.Guid.Value, slot.InstanceId, slot.MapId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The committed transfer must complete: the character still enters the reserved instance, only its lockout bind is missing.
            FaultLedger.Shared.Record("cluster.transfer-target-instance", ex);
            _logger.LogError(ex, "Binding character {Character} to the reserved instance {Instance} failed", shadow.Guid.Value, slot.InstanceId);
        }
    }

    /// <summary>The arrival callbacks of the teleport service: the held saves resume once the player is in its map (and when the arrival is abandoned), then the login notification.</summary>
    private sealed class Arrival(ITransferSessionEntry entry, WorldRuntime world) : ITransferArrivalObserver
    {
        public void MapAdded(Player player) => entry.ResumeSaves(player);

        public void Entered(Player player) => world.NotifyLoggedIn(player);

        public void Abandoned(Player player) => entry.ResumeSaves(player);
    }

    /// <summary>Finds or adopts the slot of a transfer and starts (or joins) its shadow load; null when the transfer is already activated.</summary>
    private Slot? TryBeginInstall(Guid transferId, PlayerTransferBundle bundle, bool deferUntilSession, out Task<Player>? load)
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

                slot = new Slot(transferId, bundle.CharacterGuid) { InstanceId = bundle.TargetInstanceId };
                AddSlot(slot);
            }
            else if (slot.CharacterGuid != bundle.CharacterGuid)
            {
                throw new InvalidOperationException($"transfer '{transferId}' reserved character {slot.CharacterGuid}, the bundle carries {bundle.CharacterGuid}");
            }

            if (slot.Loading is null && deferUntilSession && !SessionReady(bundle))
            {
                slot.DeferredBundle = bundle; // install-requested: OnSessionOpened starts the load once the gateway opens the client's session here
                return slot;
            }

            // One shared load per transfer; it does not take the first caller's cancellation, which only abandons its own wait.
            load = slot.Loading ?? StartLoad(slot, bundle);
            return slot;
        }
    }

    /// <summary>
    /// True when the bundle's client session is adopted here, waiting up to <see cref="SessionOpenGrace"/> for <see cref="OnSessionOpened"/> of its account when it is not yet.
    /// False only when it is still missing after the grace (closed, or never opened here).
    /// </summary>
    private async Task<bool> WaitForSessionAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken)
    {
        if (SessionReady(bundle))
        {
            return true;
        }

        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (!_sessionWaiters.TryGetValue(bundle.AccountId, out TaskCompletionSource? existing))
            {
                _sessionWaiters[bundle.AccountId] = existing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            waiter = existing;
        }

        try
        {
            if (!SessionReady(bundle)) // the session may have opened between the first probe and the registration
            {
                await waiter.Task.WaitAsync(SessionOpenGrace, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            // the grace ran out: the probe below decides
        }
        finally
        {
            lock (_gate)
            {
                if (_sessionWaiters.TryGetValue(bundle.AccountId, out TaskCompletionSource? current) && ReferenceEquals(current, waiter))
                {
                    _sessionWaiters.Remove(bundle.AccountId);
                }
            }
        }

        return SessionReady(bundle);
    }

    private bool SessionReady(PlayerTransferBundle bundle)
    {
        try
        {
            return _sessionReady!(bundle);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.transfer-target", ex);
            return false;
        }
    }

    /// <summary>Starts the slot's shadow load. Under the gate; <see cref="Slot.Loading"/> is null.</summary>
    private Task<Player> StartLoad(Slot slot, PlayerTransferBundle bundle)
    {
        slot.DeferredBundle = null;
        Task<Player> load = Task.Run(() => _loader.LoadAsync(bundle, CancellationToken.None)); // on the pool: the loader's synchronous prefix does not run under the gate
        slot.Loading = load;
        return load;
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
            _activated.Add(slot.TransferId, slot.InstanceId);
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

        /// <summary>The map the reservation allocated <see cref="InstanceId"/> on (meaningful when <see cref="Allocated"/>).</summary>
        public uint MapId { get; init; }

        /// <summary>Whether this worker's allocator reserved <see cref="InstanceId"/> for the slot: it is bound at activation and released on discard.</summary>
        public bool Allocated { get; init; }

        /// <summary>The bundle of an install that waits for the account's session (deferred load); null otherwise. Guarded by the target's gate.</summary>
        public PlayerTransferBundle? DeferredBundle { get; set; }

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

    /// <summary>A bounded map of finished transfer ids to the instance they ended on, oldest forgotten first. Guarded by the target's gate.</summary>
    private sealed class FinishedSet(int capacity)
    {
        private readonly Dictionary<Guid, uint> _set = [];
        private readonly Queue<Guid> _order = new();

        public bool Contains(Guid id) => _set.ContainsKey(id);

        public bool TryGetInstance(Guid id, out uint instanceId) => _set.TryGetValue(id, out instanceId);

        public void Add(Guid id, uint instanceId)
        {
            if (!_set.TryAdd(id, instanceId))
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
