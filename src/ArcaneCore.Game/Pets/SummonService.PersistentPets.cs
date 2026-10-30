using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Pets;

/// <summary>Persistence and effect-109 bridge for hunter pets.</summary>
public sealed partial class SummonService
{
    // The store seams below are assigned once, while the world is wired (PersistentPetFeature.Attach), before any pet save, delete or
    // load can run. The write paths read them on the thread pool without _cacheGate, so they must not be reassigned afterwards.
    public IPersistentPetStore? Persistence { get; set; }
    public Func<int, CancellationToken, Task<PersistentPetSnapshot?>>? LoadPersistence { get; set; }
    public Func<int, CancellationToken, Task<PersistentPetSnapshot?>>? LoadCallablePersistence { get; set; }
    public Func<PersistentPetSnapshot, CancellationToken, Task>? SavePersistence { get; set; }
    public Func<PersistentPetSnapshot, CancellationToken, Task>? SaveDetachedPersistence { get; set; }
    public Func<int, CancellationToken, Task>? DeletePersistence { get; set; }
    public bool DetachedPersistenceConfigured { get; set; }
    public bool DetachedPersistenceSupported { get; set; }
    private readonly Lock _cacheGate = new();
    private readonly Dictionary<int, PersistentPetSnapshot> _cached = [];
    private readonly Dictionary<int, Task> _saveTails = [];
    private readonly Dictionary<int, long> _cacheGeneration = [];
    private readonly Dictionary<int, (int Count, TaskCompletionSource Done)> _storedPetChanges = [];

    // A snapshot whose write failed and that no later write or delete of the character superseded. It is the authoritative
    // pet state until it is written: FlushCharacterAsync (the login, delete and handoff barrier) and StopAsync retry it, and a
    // newer save of the character replaces it (one row per character, so the newer full snapshot carries everything).
    private readonly Dictionary<int, PersistentPetSnapshot> _retained = [];

    // Characters whose abandon/broken-pet delete failed on every in-task attempt. The delete is durable intent: FlushCharacterAsync
    // (and so StopAsync) tries it again. A successful write of a different pet, or a stable write that replaces or removes the row
    // (QueueSerializedWriteAsync, replacedCurrentRow), clears it: one row per character, so that write replaced the row the delete
    // targeted. The store only deletes by character, so running a stale delete after such a write would destroy the new pet.
    private readonly HashSet<int> _failedDeletes = [];

    // The pet number most recently deleted per character. A save of that pet is dropped, so a snapshot captured around the delete
    // (a map update that saves before the pet despawns) can never bring the deleted row back.
    private readonly Dictionary<int, uint> _abandonedPets = [];

    private const int DeleteAttempts = 3;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Capture only the current player controlled pet; guardians, critters and wild summons are excluded.</summary>
    public PersistentPetSnapshot? CaptureCurrentPet(Player owner)
    {
        if (owner.Class != Class.Hunter || owner.Map?.FindObject(owner.PetGuid) is not Creature { Summon: { Kind: SummonKind.Pet, Charm: { } charm } } pet
            || pet.OwnerGuid != owner.Guid)
        {
            return null;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<PersistentPetCooldown> cooldowns = [];
        if (_spells is { } spellSystem)
        {
            foreach (PersistedCooldown cooldown in spellSystem.CaptureState(pet, now).Cooldowns)
            {
                cooldowns.Add(new PersistentPetCooldown((byte)cooldown.Kind,
                    cooldown.Kind == SpellCooldownKind.Spell ? cooldown.Id : 0,
                    cooldown.Kind == SpellCooldownKind.Category ? cooldown.Id : 0,
                    cooldown.EndsAtUnixMs));
            }
        }
        return new PersistentPetSnapshot((int)owner.Guid.Low, charm.PetNumber, pet.Entry, pet.Level, pet.GetUInt32(UpdateFields.UnitFieldPetexperience),
            pet.Health, pet.GetUInt32(UpdateFields.UnitFieldPower1), pet.GetUInt32(UpdateFields.UnitFieldPower5), (byte)charm.ReactState,
            charm.ActionBar.Select(button => button.Packed).ToArray(),
            charm.SpellStates.Select(s => new PersistentPetSpell(s.Key, s.Value == ActionType.Enabled, s.Value == ActionType.Passive)).ToArray(),
            Cooldowns: cooldowns, Name: charm.Name, NameTimestamp: charm.NameTimestamp, RenameAllowed: charm.RenameAllowed,
            LoyaltyLevel: PetLoyalty.Level(pet), LoyaltyPoints: charm.LoyaltyPoints, TrainingPoints: charm.TrainingPoints);
    }

    public async Task SaveCurrentPetAsync(Player owner, CancellationToken cancellationToken = default)
    {
        if (CaptureCurrentPet(owner) is { } snapshot)
        {
            lock (_cacheGate)
            {
                _cached[(int)owner.Guid.Low] = snapshot;
                _cacheGeneration[(int)owner.Guid.Low] = _cacheGeneration.GetValueOrDefault((int)owner.Guid.Low) + 1;
            }
            await PersistSerializedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveDetachedPetAsync(Player owner, CancellationToken cancellationToken = default)
    {
        if (CaptureCurrentPet(owner) is { } snapshot)
        {
            snapshot = snapshot with { IsCurrent = false };
            lock (_cacheGate)
            {
                _cached[(int)owner.Guid.Low] = snapshot;
                _cacheGeneration[(int)owner.Guid.Low] = _cacheGeneration.GetValueOrDefault((int)owner.Guid.Low) + 1;
            }
            await PersistSerializedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    public void QueueDetachedPetSave(Player owner)
    {
        _ = SaveDetachedQueuedAsync(owner);
    }

    private async Task SaveDetachedQueuedAsync(Player owner)
    {
        try { await SaveDetachedPetAsync(owner).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.persistence", ex);
            AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {owner.Guid.Low}", ex.Message);
            _logger.LogError(ex, "detached hunter pet save failed for character {CharacterId}", owner.Guid.Low);
        }
    }

    /// <summary>Capture on the world thread; the queue slot is taken here, the store write runs on the thread pool in per-character order.</summary>
    public void QueueCurrentPetSave(Player owner)
    {
        if (CaptureCurrentPet(owner) is { } snapshot)
        {
            lock (_cacheGate)
            {
                _cached[(int)owner.Guid.Low] = snapshot;
                _cacheGeneration[(int)owner.Guid.Low] = _cacheGeneration.GetValueOrDefault((int)owner.Guid.Low) + 1;
            }
            _ = PersistQueuedAsync(snapshot);
        }
    }

    /// <summary>Invalidate a character's cached pet and delete its durable row after every prior save.</summary>
    public void QueueDeletePet(Player owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        int characterId = checked((int)owner.Guid.Low);
        uint livePetNumber = owner.Map?.FindObject(owner.PetGuid) is Creature { Summon: { Kind: SummonKind.Pet, Charm: { } charm } }
            ? charm.PetNumber
            : 0;
        Task prior;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cacheGate)
        {
            uint petNumber = livePetNumber != 0 ? livePetNumber : _cached.GetValueOrDefault(characterId)?.PetNumber ?? 0;
            if (petNumber != 0)
            {
                _abandonedPets[characterId] = petNumber;
            }
            _cached.Remove(characterId);
            _retained.Remove(characterId); // the abandoned pet's unwritten snapshot must never be written back
            _cacheGeneration[characterId] = _cacheGeneration.GetValueOrDefault(characterId) + 1;
            prior = _saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _saveTails[characterId] = done.Task;
        }
        _ = Task.Run(() => DeleteQueuedAsync(characterId, prior, done, retry: false)); // the store call never runs on the calling (world/map) thread
    }

    /// <param name="retry">
    /// True for a barrier's retry of a failed abandon delete. A write queued ahead of it (a stable, an unstable, a tame save) may have replaced the
    /// character_pet row, which cancels the owed delete; the retry then completes without touching the store. A first-time abandon delete
    /// always runs.
    /// </param>
    private async Task DeleteQueuedAsync(int characterId, Task prior, TaskCompletionSource done, bool retry)
    {
        try
        {
            try
            {
                await prior.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not OperationCanceledException)
                {
                    FaultLedger.Shared.Record("pet.persistence", ex);
                    AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {characterId}", ex.Message);
                    _logger.LogFault(LogLevel.Warning, "pet-persistence", ex, "previous persistent pet write failed for character {CharacterId}; continuing with abandon delete", characterId);
                }
            }

            if (retry)
            {
                bool stillOwed;
                lock (_cacheGate) stillOwed = _failedDeletes.Contains(characterId);
                if (!stillOwed)
                {
                    done.SetResult(); // an earlier queued write replaced the row this delete was owed for
                    return;
                }
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DeleteOnceAsync(characterId).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && attempt < DeleteAttempts)
                {
                    FaultLedger.Shared.Record("pet.persistence", ex);
                    _logger.LogFault(LogLevel.Warning, "pet-persistence", ex, "persistent hunter pet delete attempt {Attempt} failed for character {CharacterId}; retrying", attempt, characterId);
                    await Task.Delay(DeleteRetryDelay * attempt).ConfigureAwait(false);
                }
            }

            lock (_cacheGate) _failedDeletes.Remove(characterId);
            done.SetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.persistence", ex);
            AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {characterId}", ex.Message);
            _logger.LogFault("pet-persistence", ex, "persistent hunter pet delete failed for character {CharacterId}; kept for retry at the next barrier", characterId);
            lock (_cacheGate) _failedDeletes.Add(characterId); // durable intent: FlushCharacterAsync retries it
            done.SetException(ex);
        }
    }

    private async Task DeleteOnceAsync(int characterId)
    {
        if (DeletePersistence is { } delete)
        {
            await delete(characterId, CancellationToken.None).ConfigureAwait(false);
        }
        else if (Persistence is { } store)
        {
            await store.DeleteAsync(characterId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Queue another attempt at a failed delete behind every write already queued; completes when it has run.</summary>
    private Task RetryFailedDeleteAsync(int characterId)
    {
        Task prior;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cacheGate)
        {
            if (!_failedDeletes.Contains(characterId))
            {
                return Task.CompletedTask;
            }
            prior = _saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _saveTails[characterId] = done.Task;
        }
        _logger.LogInformation("retrying failed persistent pet delete for character {CharacterId}", characterId);
        _ = Task.Run(() => DeleteQueuedAsync(characterId, prior, done, retry: true));
        return done.Task;
    }

    private async Task PersistQueuedAsync(PersistentPetSnapshot snapshot)
    {
        try { await PersistSerializedAsync(snapshot, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.persistence", ex);
            AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {snapshot.CharacterId}", ex.Message);
            _logger.LogError(ex, "persistent pet save failed for character {CharacterId}", snapshot.CharacterId);
        }
    }

    private Task PersistSerializedAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken)
        => PersistSerializedAsync(snapshot, retainedRetry: false, cancellationToken);

    private async Task PersistSerializedAsync(PersistentPetSnapshot snapshot, bool retainedRetry, CancellationToken cancellationToken)
    {
        Task prior = Task.CompletedTask;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool superseded;
        lock (_cacheGate)
        {
            if (IsAbandonedLocked(snapshot))
            {
                if (ReferenceEquals(_retained.GetValueOrDefault(snapshot.CharacterId), snapshot))
                {
                    _retained.Remove(snapshot.CharacterId);
                }
                return;
            }
            bool isRetained = ReferenceEquals(_retained.GetValueOrDefault(snapshot.CharacterId), snapshot);
            superseded = retainedRetry && !isRetained;
            if (!superseded)
            {
                if (!isRetained)
                {
                    // This newer full snapshot supersedes a retained one; if it fails in turn it is the one retained.
                    _retained.Remove(snapshot.CharacterId);
                }
                prior = _saveTails.GetValueOrDefault(snapshot.CharacterId) ?? Task.CompletedTask;
                _saveTails[snapshot.CharacterId] = done.Task;
            }
        }
        if (superseded)
        {
            // A newer save or a delete replaced the retained snapshot after the barrier read it: never write the older state
            // over that; the barrier waits for the newer write instead.
            await FlushCharacterAsync(snapshot.CharacterId).ConfigureAwait(false);
            return;
        }
        try
        {
            // The tail slot above fixed the order; the store call itself runs on the thread pool, never on the caller.
            bool wrote = await Task.Run(() => WriteSnapshotAsync(prior, snapshot, cancellationToken)).ConfigureAwait(false);
            lock (_cacheGate)
            {
                if (ReferenceEquals(_retained.GetValueOrDefault(snapshot.CharacterId), snapshot))
                {
                    _retained.Remove(snapshot.CharacterId);
                }
                if (wrote)
                {
                    // A different pet's row replaced the one a failed delete targeted, so that delete must not run again.
                    _failedDeletes.Remove(snapshot.CharacterId);
                }
            }
            done.SetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException)
            {
                FaultLedger.Shared.Record("pet.persistence", ex);
                AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {snapshot.CharacterId}", ex.Message);
                _logger.LogFault("pet-persistence", ex, "persistent pet save failed for character {CharacterId}", snapshot.CharacterId);
            }
            lock (_cacheGate)
            {
                // Retain the failed snapshot unless a later save or delete of the character is already queued behind it.
                if (ReferenceEquals(_saveTails.GetValueOrDefault(snapshot.CharacterId), done.Task))
                {
                    _retained[snapshot.CharacterId] = snapshot;
                }
            }
            done.SetException(ex);
            throw;
        }
    }

    private bool IsAbandonedLocked(PersistentPetSnapshot snapshot)
        => _abandonedPets.TryGetValue(snapshot.CharacterId, out uint petNumber) && petNumber == snapshot.PetNumber;

    /// <returns>True when a store write ran (the character_pet row or a detached row was written); false when nothing was written.</returns>
    private async Task<bool> WriteSnapshotAsync(Task prior, PersistentPetSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await prior.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed predecessor is retained (or superseded by this snapshot); it must not poison the next authoritative
            // snapshot for this character.
            if (ex is not OperationCanceledException)
            {
                FaultLedger.Shared.Record("pet.persistence", ex);
                AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {snapshot.CharacterId}", ex.Message);
                _logger.LogFault(LogLevel.Warning, "pet-persistence", ex, "previous persistent pet save failed for character {CharacterId}; continuing with newer state", snapshot.CharacterId);
            }
        }
        bool abandoned;
        lock (_cacheGate) abandoned = IsAbandonedLocked(snapshot);
        if (abandoned)
        {
            return false; // the pet was deleted while this save waited: never write it back
        }
        if (!snapshot.IsCurrent && SaveDetachedPersistence is { } detachedSave)
        {
            await detachedSave(snapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (snapshot.IsCurrent && SavePersistence is { } save)
        {
            await save(snapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (Persistence is { } store)
        {
            if (snapshot.IsCurrent) await store.SaveCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
            else await store.SaveDetachedAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Drain every character's writes during world shutdown through <see cref="FlushCharacterAsync"/>, so each retained failed
    /// snapshot is tried once more; throws naming the characters whose pet state is still not durable.
    /// </summary>
    public async Task StopAsync()
    {
        int[] characters;
        // A character whose queue slot was dropped (EndStoredPetChange) can still owe a failed delete or a retained snapshot.
        lock (_cacheGate) characters = [.. _saveTails.Keys.Concat(_failedDeletes).Concat(_retained.Keys).Distinct()];
        List<int> failed = [];
        List<Exception> failures = [];
        foreach (int characterId in characters)
        {
            try
            {
                await FlushCharacterAsync(characterId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("pet.persistence", ex);
                AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {characterId}", ex.Message);
                failed.Add(characterId);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"persistent pet writes did not drain for character(s) {string.Join(", ", failed)}",
                failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }

    /// <summary>
    /// The per-character barrier (login, character delete, cluster handoff, GM pet commands): waits for every queued write, then
    /// tries a retained failed snapshot once more. Throws while the character's pet state is still not durable (the retained
    /// snapshot stays for the next barrier) or when the last write was a delete that failed.
    /// </summary>
    public async Task FlushCharacterAsync(int characterId)
    {
        Task tail;
        lock (_cacheGate) tail = _saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask;
        try
        {
            await tail.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.persistence", ex);
            AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {characterId}", ex.Message);
            bool retainedOrNewer;
            lock (_cacheGate)
            {
                retainedOrNewer = _retained.ContainsKey(characterId) || !ReferenceEquals(_saveTails.GetValueOrDefault(characterId), tail);
            }
            bool deletePending;
            lock (_cacheGate) deletePending = _failedDeletes.Contains(characterId);
            if (!retainedOrNewer && !deletePending)
            {
                throw; // the last write failed and nothing is retained or retryable
            }
        }

        // A failed delete is durable intent: run it again (behind every queued write) before anything else is judged durable.
        await RetryFailedDeleteAsync(characterId).ConfigureAwait(false);
        lock (_cacheGate) tail = _saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask;

        PersistentPetSnapshot? pending;
        bool newer;
        lock (_cacheGate)
        {
            pending = _retained.GetValueOrDefault(characterId);
            newer = !ReferenceEquals(_saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask, tail);
        }
        if (pending is not null)
        {
            _logger.LogInformation("retrying retained persistent pet save for character {CharacterId}", characterId);
            await PersistSerializedAsync(pending, retainedRetry: true, CancellationToken.None).ConfigureAwait(false);
        }
        else if (newer)
        {
            await FlushCharacterAsync(characterId).ConfigureAwait(false); // a write queued meanwhile decides durability
        }
    }

    public void ForgetCharacter(int characterId)
    {
        lock (_cacheGate)
        {
            _cached.Remove(characterId);
            _retained.Remove(characterId);
            // The character is gone (deleted): its abandon delete and the dropped-save guard must not outlive it, or a reused id inherits them.
            _failedDeletes.Remove(characterId);
            _abandonedPets.Remove(characterId);
            // A late read must not see the same generation after deletion.
            _cacheGeneration[characterId] = _cacheGeneration.GetValueOrDefault(characterId) + 1;
            _saveTails.Remove(characterId);
        }
    }

    /// <summary>The character's pet generation: a login records it before reading the stored pet and checks it before restoring that read.</summary>
    public long PetGeneration(int characterId)
    {
        lock (_cacheGate) return _cacheGeneration.GetValueOrDefault(characterId);
    }

    /// <summary>
    /// True when the character's stored pet still matches a read made at <paramref name="generation"/>: no cache invalidation since and no
    /// <see cref="BeginStoredPetChange"/> under way.
    /// </summary>
    public bool IsPetGenerationCurrent(int characterId, long generation)
    {
        lock (_cacheGate) return !_storedPetChanges.ContainsKey(characterId) && _cacheGeneration.GetValueOrDefault(characterId) == generation;
    }

    /// <summary>Completes when no <see cref="BeginStoredPetChange"/> of the character is under way.</summary>
    public Task WaitForStoredPetChangesAsync(int characterId)
    {
        lock (_cacheGate) return _storedPetChanges.TryGetValue(characterId, out (int Count, TaskCompletionSource Done) change) ? change.Done.Task : Task.CompletedTask;
    }

    /// <summary>
    /// Fence an offline owner's stored pet rows for a change made outside the pet lifecycle (GM .pet rename / .pet delete). Call it on the
    /// world thread in the same step that finds the owner offline, so a login that has already read the rows sees the fence when it enters the
    /// world and reads them again after <see cref="EndStoredPetChange"/>. Every call needs one <see cref="EndStoredPetChange"/>.
    /// </summary>
    public void BeginStoredPetChange(int characterId)
    {
        lock (_cacheGate)
        {
            _cacheGeneration[characterId] = _cacheGeneration.GetValueOrDefault(characterId) + 1;
            _storedPetChanges[characterId] = _storedPetChanges.TryGetValue(characterId, out (int Count, TaskCompletionSource Done) change)
                ? (change.Count + 1, change.Done)
                : (1, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    /// <summary>End a <see cref="BeginStoredPetChange"/>: forget the cached pet (as <see cref="ForgetCharacter"/>) and release the fence.</summary>
    public void EndStoredPetChange(int characterId)
    {
        TaskCompletionSource? released = null;
        lock (_cacheGate)
        {
            _cached.Remove(characterId);
            _cacheGeneration[characterId] = _cacheGeneration.GetValueOrDefault(characterId) + 1;
            _saveTails.Remove(characterId);
            if (!_failedDeletes.Contains(characterId))
            {
                // Prune the dropped-save guard; a failed delete still owes its row, so its guard stays (StopAsync sees it via _failedDeletes).
                _abandonedPets.Remove(characterId);
            }
            if (_storedPetChanges.TryGetValue(characterId, out (int Count, TaskCompletionSource Done) change))
            {
                if (change.Count > 1)
                {
                    _storedPetChanges[characterId] = (change.Count - 1, change.Done);
                }
                else
                {
                    _storedPetChanges.Remove(characterId);
                    released = change.Done;
                }
            }
        }

        released?.SetResult();
    }

    /// <summary>Read the login-preloaded current snapshot on the world thread; no database I/O.</summary>
    public bool TryGetCachedCurrentPet(Player owner, out PersistentPetSnapshot snapshot)
    {
        lock (_cacheGate)
        {
            if (_cached.TryGetValue((int)owner.Guid.Low, out snapshot!)) return true;
        }
        snapshot = null!;
        return false;
    }

    /// <summary>Effect 56 hunter call-current path; synchronous and world-thread only.</summary>
    public bool TryCallCurrentHunterPet(Player owner, SpellSystem spells)
    {
        if (owner.Class != Class.Hunter || !owner.IsInWorld || owner.IsQuestSettlementPending
            || owner.Map is not { } map || !ReferenceEquals(map.FindObject(owner.Guid), owner))
        {
            return false;
        }

        if (spells.IsInTransit(owner))
        {
            return false;
        }

        // vmangos UnsummonOldPetBeforeNewSummon(entry=0) refuses an existing live or dead pet.
        if (!owner.PetGuid.IsEmpty && map.FindObject(owner.PetGuid) is Creature { Summon.Kind: SummonKind.Pet })
        {
            return false;
        }

        if (!TryGetCachedCurrentPet(owner, out PersistentPetSnapshot snapshot))
        {
            owner.Session.Send(WorldOpcode.SmsgPetTameFailure, PetPackets.BuildTameFailure(PetTameFailureReason.NoPetAvailable));
            return false;
        }

        if (snapshot.Health == 0)
        {
            owner.Session.Send(WorldOpcode.SmsgPetTameFailure, PetPackets.BuildTameFailure(PetTameFailureReason.Dead));
            return false;
        }

        if (!TryGetSystems(owner, 0, out _, out CreatureMapSystem? cachedCreatures)
            || cachedCreatures.Content.FindTemplate(snapshot.Entry) is null)
        {
            return false;
        }

        if (!snapshot.IsCurrent)
        {
            snapshot = snapshot with { IsCurrent = true };
            lock (_cacheGate)
            {
                _cached[(int)owner.Guid.Low] = snapshot;
                _cacheGeneration[(int)owner.Guid.Low] = _cacheGeneration.GetValueOrDefault((int)owner.Guid.Low) + 1;
            }
            _ = PersistPromotedAsync(snapshot);
        }
        return RestoreCurrentPet(owner, snapshot, current: false) is not null; // vmangos Call Pet: LoadPetFromDB(owner, 0), current = false
    }

    private async Task PersistPromotedAsync(PersistentPetSnapshot snapshot)
    {
        try { await PersistSerializedAsync(snapshot, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.persistence", ex);
            AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {snapshot.CharacterId}", ex.Message);
            _logger.LogError(ex, "promoting detached hunter pet failed for character {CharacterId}", snapshot.CharacterId);
        }
    }

    /// <summary>Load a cached current hunter pet after login. No static template is invented: the stored entry must exist.</summary>
    public async Task<PersistentPetSnapshot?> ReadCurrentPetAsync(Player owner, CancellationToken cancellationToken = default)
    {
        if (owner.Class != Class.Hunter)
        {
            return null;
        }

        int characterId = (int)owner.Guid.Low;
        long generation;
        lock (_cacheGate) generation = _cacheGeneration.GetValueOrDefault(characterId);
        PersistentPetSnapshot? snapshot = LoadPersistence is { } load
            ? await load(characterId, cancellationToken).ConfigureAwait(false)
            : Persistence is { } store
                ? await store.LoadCurrentAsync(characterId, cancellationToken).ConfigureAwait(false)
                : null;
        lock (_cacheGate)
        {
            if (_cacheGeneration.GetValueOrDefault(characterId) != generation)
            {
                return null;
            }
            if (snapshot is not null)
            {
                _cached[characterId] = snapshot;
            }
            else
            {
                _cached.Remove(characterId);
            }
        }

        return snapshot;
    }

    public Task<PersistentPetSnapshot?> ReadCallablePetAsync(Player owner, CancellationToken cancellationToken = default)
        => ReadPetAsync(owner, LoadCallablePersistence, callable: true, cancellationToken);

    private async Task<PersistentPetSnapshot?> ReadPetAsync(Player owner,
        Func<int, CancellationToken, Task<PersistentPetSnapshot?>>? loader, bool callable, CancellationToken cancellationToken)
    {
        if (owner.Class != Class.Hunter) return null;
        int id = (int)owner.Guid.Low;
        long generation;
        lock (_cacheGate) generation = _cacheGeneration.GetValueOrDefault(id);
        PersistentPetSnapshot? snapshot = loader is { } load
            ? await load(id, cancellationToken).ConfigureAwait(false)
            : Persistence is { } store
                ? await store.LoadCallableAsync(id, cancellationToken).ConfigureAwait(false)
                : null;
        lock (_cacheGate)
        {
            if (_cacheGeneration.GetValueOrDefault(id) != generation) return null;
            if (snapshot is null) _cached.Remove(id); else _cached[id] = snapshot;
        }
        return snapshot;
    }

    public Creature? RestoreCurrentPet(Player owner, PersistentPetSnapshot snapshot) => RestoreCurrentPet(owner, snapshot, current: true);

    private Creature? RestoreCurrentPet(Player owner, PersistentPetSnapshot snapshot, bool current)
    {
        if (owner.Class != Class.Hunter || !owner.PetGuid.IsEmpty || snapshot.CharacterId != (int)owner.Guid.Low
            || !snapshot.IsCurrent || snapshot.PetNumber == 0 || snapshot.Entry == 0)
        {
            return null;
        }

        return snapshot.Health == 0 ? null : SpawnCached(owner, snapshot, revive: false, current);
    }

    /// <summary>Effect 109: retained corpse first, otherwise the durable current pet; value is a percentage.</summary>
    public bool SummonDeadPet(Player owner, int percentage, SpellSystem spells)
    {
        if (owner.Class != Class.Hunter || percentage <= 0 || percentage > 100 || owner.Map is not { } map
            || !owner.IsInWorld || !ReferenceEquals(map.FindObject(owner.Guid), owner)
            || owner.IsQuestSettlementPending || spells.IsInTransit(owner))
        {
            return false;
        }

        if (map.FindObject(owner.PetGuid) is Creature { Summon.Kind: SummonKind.Pet } livePet)
        {
            if (livePet.IsAlive)
            {
                return false;
            }

            uint health = Math.Clamp((uint)((ulong)livePet.MaxHealth * (uint)percentage / 100), 1u, livePet.MaxHealth);
            return livePet.System is CreatureMapSystem system
                && system.TryReviveCurrentPet(livePet, owner, health, spells);
        }

        PersistentPetSnapshot? snapshot;
        lock (_cacheGate) snapshot = _cached.GetValueOrDefault((int)owner.Guid.Low);
        if (snapshot is null || snapshot.CharacterId != (int)owner.Guid.Low || snapshot.PetNumber == 0 || snapshot.Entry == 0)
        {
            return false;
        }

        // Spell.cpp:6104-6109: an unloaded pet with positive saved health is not dead.
        if (snapshot.Health > 0)
        {
            owner.Session.Send(WorldOpcode.SmsgPetTameFailure, PetPackets.BuildTameFailure(PetTameFailureReason.NotDead));
            return false;
        }
        if (SpawnCached(owner, snapshot, revive: true, current: true) is not { } cached)
        {
            return false;
        }

        cached.Health = Math.Clamp((uint)((ulong)cached.MaxHealth * (uint)percentage / 100), 1u, cached.MaxHealth);
        QueueCurrentPetSave(owner);
        return true;
    }

    /// <summary>
    /// vmangos Pet::LoadPetFromDB for a cached hunter pet. <paramref name="current"/> is LoadPetFromDB's: true for the pet that was out (login, teleport,
    /// revive), false for Call Pet; it decides which of the owner's talent pet auras the pet takes (Pet.cpp:357, CastPetAuras(current)).
    /// </summary>
    private Creature? SpawnCached(Player owner, PersistentPetSnapshot snapshot, bool revive, bool current = true)
    {
        if (!TryGetSystems(owner, 0, out PetMapSystem? pets, out CreatureMapSystem? creatures)
            || creatures.Content.FindTemplate(snapshot.Entry) is not { } template)
        {
            return null;
        }

        ReservePetNumber(snapshot.PetNumber);
        uint worldCounter = NextPetNumber();
        Creature pet = creatures.SpawnSummonedWithCounter(template, HighGuid.Pet, worldCounter, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Pet, owner.Guid, 0, TotemSlots.None, 0);
            ApplyOwner(creature, owner, 0);
            InitPet(creature, SummonKind.Pet, owner, snapshot.PetNumber);
            if (owner.Class == Class.Hunter)
            {
                creature.IgnoreTemplateResistances = true; // a tamed beast loses its innate resistances (Patch 1.3.0)
                creature.ApplyCreateResistances();
            }

            if (creature.Summon.Charm is { } initCharm)
            {
                initCharm.Name = string.IsNullOrWhiteSpace(snapshot.Name) ? creature.Template.Name : snapshot.Name;
                initCharm.NameTimestamp = snapshot.NameTimestamp;
                initCharm.RenameAllowed = snapshot.RenameAllowed;
            }
            creature.UnitFlags |= UnitFlags.PetAbandon;
            if (snapshot.RenameAllowed)
            {
                creature.UnitFlags |= UnitFlags.PetRename | UnitFlags.PetAbandon;
            }
            else
            {
                creature.UnitFlags &= ~UnitFlags.PetRename;
            }
            // LoadPetFromDB ends with SynchronizeLevelWithOwner (Pet.cpp:413); a HUNTER_PET above its owner drops to the owner's level (Pet.cpp:2353-2356).
            int loadLevel = owner.Class == Class.Hunter ? Math.Min((int)snapshot.Level, (int)owner.Level) : snapshot.Level;
            PetInitializer.InitStatsForLevel(creature, owner, loadLevel, Content);
            creature.SetUInt32(UpdateFields.UnitFieldPetnextlevelexp, PetXpForLevel(creature.Level)); // ObjectMgr::GetXPForPetLevel
            creature.SetUInt32(UpdateFields.UnitFieldPetNameTimestamp, snapshot.NameTimestamp);
            creature.SetUInt32(UpdateFields.UnitFieldPetexperience, snapshot.Experience);
            creature.Health = revive
                ? Math.Clamp(snapshot.Health, 1u, creature.MaxHealth)
                : Math.Min(snapshot.Health, creature.MaxHealth);
            creature.SetUInt32(UpdateFields.UnitFieldPower1, Math.Min(snapshot.Mana, creature.GetUInt32(UpdateFields.UnitFieldMaxpower1)));
            creature.SetUInt32(UpdateFields.UnitFieldPower5, snapshot.Happiness);
            if (creature.Summon.Charm is { } reactCharm)
            {
                reactCharm.ReactState = (ReactState)Math.Clamp((int)snapshot.ReactState, 0, 2);
            }
            PetLoyalty.Restore(creature, snapshot.LoyaltyLevel, snapshot.LoyaltyPoints, snapshot.TrainingPoints);
            return new CreatureHome(owner.X, owner.Y, owner.Z, owner.Orientation);
        }, snapshot.PetNumber);

        CharmInfo? maybeCharm = pet.Summon?.Charm;
        if (!Invariant.Check(maybeCharm is not null, $"SpawnCached pet {pet.Guid} is missing CharmInfo") || maybeCharm is not { } charm)
        {
            return null;
        }

        foreach (PersistentPetSpell spell in snapshot.Spells)
        {
            if (_spells is not { } spellSystem || spellSystem.Store.Get(spell.SpellId) is null)
            {
                continue;
            }
            charm.LearnSpell(spell.SpellId, spell.Passive ? ActionType.Passive : spell.Autocast ? ActionType.Enabled : ActionType.Disabled);
            if (spell.Passive)
            {
                spellSystem.CastSpell(pet, spell.SpellId, SpellCastTargets.ForSelf(), triggered: true);
            }
        }
        if (owner.Class == Class.Hunter)
        {
            // Pet::LoadPetFromDB rebuilds m_teachspells (Pet.cpp:321-338), and a tame goes through here too (Pet::InitPetCreateSpells AddTeachSpell):
            // they are not in the snapshot, so they are derived again from the create spells, which skips what the owner already knows.
            InitTeachSpells(owner, pet, snapshot.Entry);
        }
        if (_spells is { } restoreSpells && snapshot.Cooldowns is { Count: > 0 })
        {
            restoreSpells.RestoreCooldowns(pet, snapshot.Cooldowns.Select(c => new PersistedCooldown(
                c.Kind == 0 ? SpellCooldownKind.Spell : SpellCooldownKind.Category,
                c.Kind == 0 ? c.SpellId : c.Category, c.EndsAtUnixMs)), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        for (int i = 0; i < Math.Min(snapshot.ActionBar.Count, CharmInfo.ActionBarSize); i++)
        {
            ActionButton button = new(snapshot.ActionBar[i]);
            charm.SetActionBar(i, button.Action, button.Type);
        }
        pet.UnitFlags |= UnitFlags.PetAbandon;
        if (charm.RenameAllowed)
        {
            pet.UnitFlags |= UnitFlags.PetRename;
        }
        else
        {
            pet.UnitFlags &= ~UnitFlags.PetRename;
        }
        pets.Options = _options;
        pets.Register(pet, this);
        AttachPetAi(pet);
        owner.SetPetGuid(pet.Guid);
        GreyOutForMountedOwner(owner, charm);
        owner.Session.Send(WorldOpcode.SmsgPetSpells,
            PetPackets.BuildPetSpells(pet, charm, listSpells: true, _spells?.GetActiveCooldowns(pet) ?? []));
        RememberPetForSpiritHealer(owner, pet); // Pet::LoadPetFromDB: "save pet for resurrection by spirit healer"
        if (_spells is { } auraSpells)
        {
            PetAuras.PetAuraService.For(auraSpells).CastPetAuras(pet, current); // Pet.cpp:356-357: LearnPetPassives, then CastPetAuras(current)
        }

        return pet;
    }
}
