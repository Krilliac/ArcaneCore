using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
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
/// does not tick and the load performs no durable write. May run on any thread (it reads databases).
/// </summary>
public interface ITransferShadowLoader
{
    /// <summary>Loads the shadow of <paramref name="bundle"/>'s character. Throws when the character or its client session cannot be resolved here.</summary>
    Task<Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken);
}

/// <summary>
/// What activation needs from the adopted client session, in login order. All three run on the world thread. The session state machine of the
/// adopted session (character screen to in world) lives behind this seam.
/// </summary>
public interface ITransferSessionEntry
{
    /// <summary>Moves the adopted session into the in-world state with <paramref name="player"/>. False when the client is gone or superseded (nothing more is sent or added).</summary>
    bool TryAttach(Player player);

    /// <summary>The login-style packets that precede the map add (<c>SendInitialPacketsBeforeAddToMap</c>).</summary>
    void SendInitialPacketsBeforeAddToMap(Player player);

    /// <summary>The login-style packets after the map add (resume of saves, zone update and world states).</summary>
    void SendInitialPacketsAfterAddToMap(Player player);
}

/// <summary>
/// Production <see cref="ITransferShadowLoader"/>: the same <see cref="ICharacterHooks"/> loading phases as CMSG_PLAYER_LOGIN, on the adopted
/// session's service scope. <paramref name="sessions"/> resolves the adopted <see cref="WorldSession"/> of a bundle (null while the gateway has not
/// moved the client here); an unresolved session fails the load and the coordinator aborts or retries. The load itself saves nothing; a hook
/// that writes would equally write on a normal login. Not thread-affine.
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

        RaceInfo raceInfo = await worldData.GetRaceInfoAsync(character.Race, character.Gender).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"missing race data for character {characterId}");
        ClassInfo classInfo = await worldData.GetClassInfoAsync(character.Class).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"missing class data for character {characterId}");
        HomeBind home = bundle.HomeBind;
        if (home.IsUnset)
        {
            home = new HomeBind(character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ);
        }

        IReadOnlyList<ActionButton> buttons = await characters.GetActionButtonsAsync(characterId, cancellationToken).ConfigureAwait(false);
        var player = new Player(character, CharacterPackets.BuildAppearance(raceInfo, classInfo), session, buttons)
        {
            Home = home,
            CharacterOwnership = session.World.CharacterOwnership,
        };

        ICharacterHooks[] allHooks = [.. session.Services.GetServices<ICharacterHooks>()];
        foreach (ICharacterHooks hooks in allHooks)
        {
            await hooks.OnPlayerLoadingAsync(session, character, player).ConfigureAwait(false);
        }

        foreach (ICharacterHooks hooks in allHooks)
        {
            await hooks.OnPlayerLoadedAsync(session, character, player).ConfigureAwait(false);
        }

        return player;
    }
}

/// <summary>
/// Production <see cref="ITransferSessionEntry"/> over the adopted <see cref="WorldSession"/> of the player (the shadow was built with it). The
/// session task does not read the account settings while the gateway buffers the client's packets for the commit window, so reading them on the
/// world thread here does not race. World thread.
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

    public void SendInitialPacketsBeforeAddToMap(Player player)
    {
        var session = (WorldSession)player.Session;
        LoginSequence.SendInitialPacketsBeforeAddToMap(session, player, LoginPackets.BuildTutorialFlags(session.Settings.Tutorials));
    }

    public void SendInitialPacketsAfterAddToMap(Player player)
    {
        var session = (WorldSession)player.Session;
        // As the login does: the player is in its map, so the saves held for the character since capture resume.
        int id = checked((int)player.Guid.Low);
        session.Services.GetService<CharacterSaveQueue>()?.ResumeCharacter(id);
        session.Services.GetService<QuestNpcFeature>()?.Persistence.ResumeCharacter(id);
        LoginSequence.SendInitialPacketsAfterAddToMap(session, player);
    }
}

/// <summary>
/// The production <see cref="ITransferTargetWorker"/> (Tier 3, default D4). The target of a transfer reloads the character from the characters
/// database (the source's settlement save is durable before the bundle exists) instead of deserialising live objects:
/// <list type="bullet">
/// <item><description><see cref="TryReserveSlotAsync"/>: refuses when <see cref="WorkerAdmission"/> is closed, when this worker does not own the target map copy,
/// when the character is already online here or reserved by another transfer, or when the slot limit is reached. Idempotent per transfer id.</description></item>
/// <item><description><see cref="InstallPassiveShadowAsync"/>: loads the shadow through <see cref="ITransferShadowLoader"/> once per transfer (concurrent and repeated calls share the load; a failed load is retried by the next call).
/// The shadow is in no map, is never ticked and nothing is written.</description></item>
/// <item><description><see cref="ActivateTargetPlayerAsync"/> (after the coordinator committed the new epoch): one world-thread command that adopts the target ownership token, places the
/// player at the target position, attaches the adopted session, sends SMSG_NEW_WORLD and the login-style initial packets and adds the player to the map. A repeat for the same transfer id is a no-op.</description></item>
/// <item><description><see cref="DiscardPassiveShadowAsync"/>: drops the reservation and the shadow and frees the slot; a discarded transfer id can never be installed or activated afterwards. A discard after activation does nothing.</description></item>
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
        ILogger<WorkerTransferTarget>? logger = null)
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
    }

    public WorkerNodeId WorkerId => _workerId;

    public Guid Incarnation { get; }

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

    public Task<bool> TryReserveSlotAsync(
        Guid transferId,
        ulong characterGuid,
        uint targetMapId,
        uint targetInstanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_admission is { IsOpen: false })
        {
            return Task.FromResult(false);
        }

        if (!_world.Owns(targetMapId, targetInstanceId) || _world.IsOnline(new ObjectGuid(characterGuid)))
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            if (_discarded.Contains(transferId))
            {
                return Task.FromResult(false);
            }

            if (_activated.Contains(transferId))
            {
                return Task.FromResult(true); // a repeated stage 1 of a transfer that already completed
            }

            if (_slots.TryGetValue(transferId, out Slot? existing))
            {
                return Task.FromResult(existing.CharacterGuid == characterGuid);
            }

            if (_byCharacter.ContainsKey(characterGuid) || _slots.Count >= _maxReservations)
            {
                return Task.FromResult(false);
            }

            AddSlot(new Slot(transferId, characterGuid));
            return Task.FromResult(true);
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

            removed = RemoveSlot(transferId);
            _discarded.Add(transferId);
        }

        if (removed is not null)
        {
            _logger.LogInformation("Transfer {TransferId} discarded on {Worker}: {Reason}", transferId, _workerId, abortReason);
        }

        // The shadow was never in a map or the online registry: dropping the reference is the whole teardown. A load still running finishes
        // into a slot nobody reads.
        if (removed?.Loading is { IsFaulted: true } faulted)
        {
            _ = faulted.Exception; // observed: a discarded load that failed is not an unobserved task exception
        }

        return Task.CompletedTask;
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
        }

        if (_world.FindOnlinePlayer(shadow.Guid) is { } online)
        {
            if (!ReferenceEquals(online, shadow))
            {
                throw new InvalidOperationException($"character {bundle.CharacterGuid} is already online on worker '{_workerId}'");
            }

            Finish(slot); // a repeat after the add succeeded but the caller did not hear of it
            return false;
        }

        if (_world.TryGetMap(bundle.TargetMapId, bundle.TargetInstanceId, out Map? map) != MapAccessResult.Granted || map is null)
        {
            throw new InvalidOperationException($"worker '{_workerId}' does not own map {bundle.TargetMapId} instance {bundle.TargetInstanceId}");
        }

        TransferPosition at = bundle.TargetPosition;
        shadow.CharacterOwnership = token;
        shadow.MapId = bundle.TargetMapId;
        shadow.Relocate(at.X, at.Y, at.Z, at.Orientation, _world.NowMs);

        if (!_entry.TryAttach(shadow))
        {
            _logger.LogInformation("Transfer {TransferId}: the client of character {Character} left before activation; nothing to add", slot.TransferId, bundle.CharacterGuid);
            Finish(slot);
            return false;
        }

        // Login order (vmangos HandlePlayerLogin): the client is told the new map, then the initial packets, the map add, the packets after it.
        shadow.Session.Send(WorldOpcode.SmsgNewWorld, TeleportPackets.BuildNewWorld(bundle.TargetMapId, at.X, at.Y, at.Z, at.Orientation));
        _entry.SendInitialPacketsBeforeAddToMap(shadow);
        try
        {
            _world.AddPlayer(shadow, map);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leave a session "in world" with a player that is not in a map.
            FaultLedger.Shared.Record("cluster.transfer-target", ex);
            shadow.Session.Kick("transfer target could not add the player");
            throw;
        }

        _entry.SendInitialPacketsAfterAddToMap(shadow);
        _world.NotifyLoggedIn(shadow);
        Finish(slot);
        return true;
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
            slot.Loading ??= _loader.LoadAsync(bundle, CancellationToken.None);
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

        /// <summary>The shadow load (null until the first install; guarded by the target's gate).</summary>
        public Task<Player>? Loading { get; set; }
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
