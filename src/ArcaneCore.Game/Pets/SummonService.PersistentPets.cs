using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Pets;

/// <summary>Persistence and effect-109 bridge for hunter pets, and the save and login restore of a warlock's demon.</summary>
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

    /// <summary>
    /// Fence seam, assigned once while the world is wired (before any write runs, like the store seams). True when the process no longer owns the character (a handoff retired it): the pet write
    /// or delete is skipped silently and counted by the seam, and a retained snapshot or owed delete is dropped, because the characters fence would refuse it. Null: nothing is ever retired.
    /// </summary>
    public Func<int, bool>? WritesRetired { get; set; }

    /// <summary>
    /// Fence seam, assigned once while the world is wired. True while a handoff is sealed (flushed, takeover undecided): a fence refusal of a pet write is expected, logged as information, and the
    /// snapshot (or owed delete) stays retained so an aborted handoff, which resumes this world, writes it at the next barrier. Null: never sealed.
    /// </summary>
    public Func<bool>? WritesSealed { get; set; }

    private static bool IsOwnershipRefusal(Exception? failure)
    {
        int depth = 0;
        for (Exception? e = failure; e is not null && depth < 16; e = e.InnerException, depth++)
        {
            if (e is ArcaneCore.Kernel.Characters.CharacterOwnershipRejectedException)
            {
                return true;
            }

            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsOwnershipRefusal))
            {
                return true;
            }
        }

        return false;
    }
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

    // A warlock's saved demons by character and creature entry: the demon summon loads from here (vmangos Unit::EffectSummonPet tries
    // LoadPetFromDB(owner, petEntry) first, SpellEffects.cpp:3221). Filled at login (ReadCurrentPetAsync) and by every demon capture; the store keeps the same rows.
    private readonly Dictionary<int, Dictionary<uint, PersistentPetSnapshot>> _demonCache = [];

    /// <summary>Test seam for <see cref="IPersistentPetStore.LoadDemonsAsync"/>: replaces the store's read of the saved demons at login. Assigned once while the world is wired.</summary>
    public Func<int, CancellationToken, Task<IReadOnlyList<PersistentPetSnapshot>>>? LoadDemonsPersistence { get; set; }

    private const int DeleteAttempts = 3;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Capture only the current player controlled pet: a hunter's pet or a warlock's permanent demon (vmangos Pet::IsPermanentPetFor,
    /// <see cref="IsPermanentPetFor"/>; Pet::SavePetToDB saves both, Pet.cpp:520-600). Guardians, critters, wild summons and a warlock's other
    /// summoned pets are excluded. World thread.
    /// </summary>
    public PersistentPetSnapshot? CaptureCurrentPet(Player owner)
    {
        return owner.Map?.FindObject(owner.PetGuid) is Creature pet ? CapturePet(owner, pet) : null;
    }

    /// <summary>
    /// The pet's state as the owner's current pet, taken from the creature itself, so a pet that already left the map (a corpse decayed first) is
    /// captured with the health it died with (0). Null for anything <see cref="CaptureCurrentPet"/> excludes. World thread.
    /// </summary>
    private PersistentPetSnapshot? CapturePet(Player owner, Creature pet)
    {
        if ((owner.Class != Class.Hunter && owner.Class != Class.Warlock)
            || pet is not { Summon: { Kind: SummonKind.Pet, Charm: { } charm } links }
            || pet.OwnerGuid != owner.Guid)
        {
            return null;
        }

        bool demon = owner.Class != Class.Hunter;
        if (demon && !IsPermanentPetFor(owner, pet))
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
        // A demon has no loyalty or training points (the defaults of the snapshot); it keeps the spell that summoned it (character_pet.created_by_spell).
        return new PersistentPetSnapshot((int)owner.Guid.Low, charm.PetNumber, pet.Entry, pet.Level, pet.GetUInt32(UpdateFields.UnitFieldPetexperience),
            pet.Health, pet.GetUInt32(UpdateFields.UnitFieldPower1), pet.GetUInt32(UpdateFields.UnitFieldPower5), (byte)charm.ReactState,
            charm.ActionBar.Select(button => button.Packed).ToArray(),
            charm.SpellStates.Select(s => new PersistentPetSpell(s.Key, s.Value == ActionType.Enabled, s.Value == ActionType.Passive)).ToArray(),
            Cooldowns: cooldowns, Name: charm.Name, NameTimestamp: charm.NameTimestamp, RenameAllowed: charm.RenameAllowed,
            LoyaltyLevel: demon ? (byte)1 : PetLoyalty.Level(pet), LoyaltyPoints: demon ? 1000 : charm.LoyaltyPoints, TrainingPoints: demon ? 0 : charm.TrainingPoints,
            CreatedBySpell: demon ? links.SpellId : 0, ResetTalentsCost: charm.ResetTalentsCost, ResetTalentsTime: charm.ResetTalentsTime);
    }

    public async Task SaveCurrentPetAsync(Player owner, CancellationToken cancellationToken = default)
    {
        if (CaptureCurrentPet(owner) is { } snapshot)
        {
            lock (_cacheGate)
            {
                _cached[(int)owner.Guid.Low] = snapshot;
                RememberDemonLocked(snapshot);
                _cacheGeneration[(int)owner.Guid.Low] = _cacheGeneration.GetValueOrDefault((int)owner.Guid.Low) + 1;
            }
            await PersistSerializedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Test seam: replaces <see cref="CaptureCurrentPet"/> for <see cref="QueueDetachedPetSave"/> only, so a capture failure can be provoked.
    /// Null in production.
    /// </summary>
    internal Func<Player, PersistentPetSnapshot?>? DetachedCaptureOverride { get; set; }

    public void QueueDetachedPetSave(Player owner)
    {
        _ = SaveDetachedQueuedAsync(owner);
    }

    private async Task SaveDetachedQueuedAsync(Player owner)
    {
        PersistentPetSnapshot? snapshot;
        try { snapshot = (DetachedCaptureOverride ?? CaptureCurrentPet)(owner); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {owner.Guid.Low}", ex.Message);
            _logger.LogFault("pet.persistence", ex, "detached hunter pet capture failed for character {CharacterId}", owner.Guid.Low);
            return;
        }
        if (snapshot is null)
            return;
        snapshot = snapshot with { IsCurrent = false };
        lock (_cacheGate)
        {
            _cached[(int)owner.Guid.Low] = snapshot;
            RememberDemonLocked(snapshot);
            _cacheGeneration[(int)owner.Guid.Low] = _cacheGeneration.GetValueOrDefault((int)owner.Guid.Low) + 1;
        }
        try { await PersistSerializedAsync(snapshot, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Observe only: PersistSerializedAsync already recorded and logged this failure once.
            _logger.LogDebug(ex, "detached hunter pet save for character {CharacterId} ended in a failure that was already recorded", owner.Guid.Low);
        }
    }

    /// <summary>Capture on the world thread; the queue slot is taken here, the store write runs on the thread pool in per-character order.</summary>
    public void QueueCurrentPetSave(Player owner) => QueueCapturedPetSave(owner, CaptureCurrentPet(owner));

    /// <summary>
    /// <see cref="QueueCurrentPetSave(Player)"/> for <paramref name="pet"/> after it left the map: only when it is still the owner's current pet link (a decayed
    /// corpse is released after this call), so the row keeps the health 0 it died with and a relog does not restore it alive.
    /// </summary>
    internal void QueueCurrentPetSave(Player owner, Creature pet)
        => QueueCapturedPetSave(owner, owner.PetGuid == pet.Guid ? CapturePet(owner, pet) : null);

    private void QueueCapturedPetSave(Player owner, PersistentPetSnapshot? captured)
    {
        if (captured is { } snapshot)
        {
            lock (_cacheGate)
            {
                _cached[(int)owner.Guid.Low] = snapshot;
                RememberDemonLocked(snapshot);
                _cacheGeneration[(int)owner.Guid.Low] = _cacheGeneration.GetValueOrDefault((int)owner.Guid.Low) + 1;
            }
            _ = PersistQueuedAsync(snapshot);
        }
    }

    /// <summary>Keep a warlock demon's captured state as its saved row by entry (<c>_cacheGate</c> held). A hunter pet has no creating spell and is not kept.</summary>
    private void RememberDemonLocked(PersistentPetSnapshot snapshot)
    {
        if (snapshot.CreatedBySpell == 0)
        {
            return;
        }

        if (!_demonCache.TryGetValue(snapshot.CharacterId, out Dictionary<uint, PersistentPetSnapshot>? byEntry))
        {
            byEntry = [];
            _demonCache[snapshot.CharacterId] = byEntry;
        }

        byEntry[snapshot.Entry] = snapshot with { IsCurrent = false };
    }

    /// <summary>
    /// The owner's saved demon of <paramref name="entry"/> (vmangos LoadPetFromDB(owner, petEntry), Pet.cpp:146-153: selected by owner and entry only), or null.
    /// A demon that died is found too: a SUMMON_PET loaded by entry is not dead on load (Pet.cpp:415), so it keeps its name and number. Reads only memory.
    /// </summary>
    private PersistentPetSnapshot? FindSavedDemon(Player owner, uint entry)
    {
        lock (_cacheGate)
        {
            return _demonCache.TryGetValue((int)owner.Guid.Low, out Dictionary<uint, PersistentPetSnapshot>? byEntry)
                && byEntry.TryGetValue(entry, out PersistentPetSnapshot? saved) && saved.PetNumber != 0 && saved.Entry == entry
                ? saved
                : null;
        }
    }

    /// <summary>
    /// Invalidate a character's cached pet and delete its durable current-pet row after every prior save. A warlock's saved demons (one per entry) stay.
    /// <paramref name="numberStaysInUse"/> is true for a demon, whose pet number is loaded again when its entry is summoned again: its number is then not
    /// recorded as abandoned (which would drop every later save of that number).
    /// </summary>
    public void QueueDeletePet(Player owner, bool numberStaysInUse = false)
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
            if (petNumber != 0 && !numberStaysInUse)
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
            catch (Exception ex)
            {
                // Observe only, whatever the exception (an OutOfMemoryException included): a failed predecessor was already recorded when it
                // ran (an OutOfMemoryException is the one exception that is never recorded), and its failure must not stop this delete or pass down the queue.
                _logger.LogDebug(ex, "previous persistent pet write failed for character {CharacterId}; continuing with abandon delete", characterId);
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

            if (WritesRetired?.Invoke(characterId) == true)
            {
                lock (_cacheGate) _failedDeletes.Remove(characterId); // the target world owns the row now: this delete must not touch it
                done.SetResult();
                return;
            }

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DeleteOnceAsync(characterId).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && attempt < DeleteAttempts && !IsOwnershipRefusal(ex)) // a fence refusal is final, not transient
                {
                    _logger.LogFault(LogLevel.Warning, "pet.persistence", ex, "persistent hunter pet delete attempt {Attempt} failed for character {CharacterId}; retrying", attempt, characterId);
                    await Task.Delay(DeleteRetryDelay * attempt).ConfigureAwait(false);
                }
            }

            lock (_cacheGate) _failedDeletes.Remove(characterId);
            done.SetResult();
        }
        catch (Exception ex)
        {
            // Unfiltered: the queue slot must complete for every exception, or every later write and barrier of this character waits forever.
            // Only the fault recording skips OutOfMemoryException.
            if (ex is not OutOfMemoryException && IsOwnershipRefusal(ex))
            {
                if (WritesRetired?.Invoke(characterId) == true)
                {
                    lock (_cacheGate) _failedDeletes.Remove(characterId); // refused because the character moved to another world: counted, neither logged nor retained
                    done.SetResult();
                    return;
                }

                if (WritesSealed?.Invoke() == true)
                {
                    _logger.LogInformation("persistent hunter pet delete of character {CharacterId} refused while the handoff is undecided; kept in case it aborts", characterId);
                }
                else
                {
                    AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {characterId}", ex.Message);
                    _logger.LogFault("pet.persistence", ex, "persistent hunter pet delete failed for character {CharacterId}; kept for retry at the next barrier", characterId);
                }
            }
            else if (ex is not OutOfMemoryException)
            {
                AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {characterId}", ex.Message);
                _logger.LogFault("pet.persistence", ex, "persistent hunter pet delete failed for character {CharacterId}; kept for retry at the next barrier", characterId);
            }
            lock (_cacheGate) _failedDeletes.Add(characterId); // durable intent: FlushCharacterAsync retries it
            done.TrySetException(ex);
            if (ex is OutOfMemoryException)
            {
                throw;
            }
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
            // Observe only: PersistSerializedAsync already recorded and logged this failure once.
            _logger.LogDebug(ex, "queued persistent pet save for character {CharacterId} ended in a failure that was already recorded", snapshot.CharacterId);
        }
    }

    private Task PersistSerializedAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken)
        => PersistSerializedAsync(snapshot, retainedRetry: false, cancellationToken);

    private async Task PersistSerializedAsync(PersistentPetSnapshot snapshot, bool retainedRetry, CancellationToken cancellationToken)
    {
        if (WritesRetired?.Invoke(snapshot.CharacterId) == true)
        {
            // The takeover moved the character to another world: the fence would refuse this write, so it is dropped (and counted) with anything retained for the character.
            lock (_cacheGate) _retained.Remove(snapshot.CharacterId);
            return;
        }

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
        catch (Exception ex)
        {
            // Unfiltered: the queue slot must complete for every exception, or every later write and barrier of this character waits forever.
            // This is the one place a save failure is recorded and logged; the callers below only observe or propagate it.
            if (ex is not OutOfMemoryException and not OperationCanceledException && IsOwnershipRefusal(ex))
            {
                if (WritesRetired?.Invoke(snapshot.CharacterId) == true)
                {
                    // Refused because the character moved to another world: counted, neither logged nor retained, and the write counts as drained.
                    lock (_cacheGate)
                    {
                        if (ReferenceEquals(_retained.GetValueOrDefault(snapshot.CharacterId), snapshot))
                        {
                            _retained.Remove(snapshot.CharacterId);
                        }
                    }

                    done.SetResult();
                    return;
                }

                if (WritesSealed?.Invoke() == true)
                {
                    _logger.LogInformation("persistent pet save of character {CharacterId} refused while the handoff is undecided; retained in case it aborts", snapshot.CharacterId);
                }
                else
                {
                    AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {snapshot.CharacterId}", ex.Message);
                    _logger.LogFault("pet.persistence", ex, "persistent pet save failed for character {CharacterId}", snapshot.CharacterId);
                }
            }
            else if (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // LogFault records the fault ledger entry once; no separate Record.
                AnomalyLedger.Shared.Report("pet", "persistence-fault", $"character {snapshot.CharacterId}", ex.Message);
                _logger.LogFault("pet.persistence", ex, "persistent pet save failed for character {CharacterId}", snapshot.CharacterId);
            }
            lock (_cacheGate)
            {
                // Retain the failed snapshot unless a later save or delete of the character is already queued behind it.
                if (ReferenceEquals(_saveTails.GetValueOrDefault(snapshot.CharacterId), done.Task))
                {
                    _retained[snapshot.CharacterId] = snapshot;
                }
            }
            done.TrySetException(ex);
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
        catch (Exception ex)
        {
            // A failed predecessor is retained (or superseded by this snapshot); it must not poison the next authoritative
            // snapshot for this character. Observe only, an OutOfMemoryException included: a failed predecessor was already recorded when
            // it failed (an OutOfMemoryException is never recorded), and only a fresh failure of this snapshot's own store call may propagate.
            _logger.LogDebug(ex, "previous persistent pet save failed for character {CharacterId}; continuing with newer state", snapshot.CharacterId);
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
            catch (Exception ex) when (WritesSealed?.Invoke() == true && IsOwnershipRefusal(ex))
            {
                // The handoff never decided (its retirement did not reach this process, for example with the gateway link down) and the fence refuses the write: after the takeover it can never
                // be written, so the stop names it in a warning instead of failing on a write the new owner has superseded.
                _logger.LogWarning("persistent pet state of character {CharacterId} was refused by the fence while a handoff was undecided at stop and is dropped", characterId);
            }
            catch (Exception ex)
            {
                // Not recorded here: a failed write or delete was already recorded when it ran, except an OutOfMemoryException, which is deliberately never
                // recorded. An OutOfMemoryException
                // observed from a tail must not abort the drain of the other characters; it is reported with the rest.
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
        catch
        {
            // Not recorded here: a failed write or delete was already recorded when it ran, except an OutOfMemoryException, which is deliberately never
            // recorded. An OutOfMemoryException
            // observed from the tail is treated like any other failed tail so the retained snapshot or owed delete still runs.
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
            _demonCache.Remove(characterId);
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
            _demonCache.Remove(characterId);
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
            // Observe only: PersistSerializedAsync already recorded and logged this failure once.
            _logger.LogDebug(ex, "promoting detached hunter pet for character {CharacterId} ended in a failure that was already recorded", snapshot.CharacterId);
        }
    }

    /// <summary>Load a cached current hunter pet or warlock demon after login. No static template is invented: the stored entry must exist.</summary>
    public async Task<PersistentPetSnapshot?> ReadCurrentPetAsync(Player owner, CancellationToken cancellationToken = default)
    {
        if (owner.Class != Class.Hunter && owner.Class != Class.Warlock)
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
        // A warlock's other saved demons come with the login read, so summoning an entry again needs no database call on the world thread.
        IReadOnlyList<PersistentPetSnapshot> demons = owner.Class != Class.Warlock ? []
            : LoadDemonsPersistence is { } loadDemons ? await loadDemons(characterId, cancellationToken).ConfigureAwait(false)
            : Persistence is { } demonStore ? await demonStore.LoadDemonsAsync(characterId, cancellationToken).ConfigureAwait(false)
            : [];
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

            _demonCache.Remove(characterId);
            foreach (PersistentPetSnapshot demon in demons.Where(d => d.CharacterId == characterId))
            {
                RememberDemonLocked(demon);
            }

            if (snapshot is not null)
            {
                RememberDemonLocked(snapshot);
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
        if (owner.Class == Class.Warlock)
        {
            return RestoreCurrentDemon(owner, snapshot);
        }

        if (owner.Class != Class.Hunter || !owner.PetGuid.IsEmpty || snapshot.CharacterId != (int)owner.Guid.Low
            || !snapshot.IsCurrent || snapshot.PetNumber == 0 || snapshot.Entry == 0)
        {
            return null;
        }

        return snapshot.Health == 0 ? null : SpawnCached(owner, snapshot, revive: false, current);
    }

    /// <summary>
    /// vmangos Pet::LoadPetFromDB for the warlock's saved demon at login (the current pet, Pet.cpp:130-420): the same pet number, entry, summoning spell,
    /// saved name (a SUMMON_PET is only renamed when its saved <c>renamed</c> flag is 0, and SavePetToDB writes 1 for a pet without the PET_RENAME flag,
    /// Pet.cpp:270 and :529), health, mana, experience, react state, spells, bar and cooldowns, at the owner's level and close point. A dead demon is not
    /// brought back, the same rule as a dead hunter pet. A dead owner or one on a taxi flight gets no pet either: <c>LoadPetFromDB(current)</c> refuses
    /// when <c>IsPetNeedBeTemporaryUnsummoned</c> (Pet.cpp:217-223, Player.cpp:20941-20944) and only remembers the pet number
    /// (<c>SetTemporaryUnsummonedPetNumber</c>), so the pet comes back with <see cref="ResummonTemporarilyUnsummonedPet"/> and the saved row stays the current one.
    /// Refuses when the owner has a pet, the snapshot is another character's or not current, or the entry is not a demon template. World thread.
    /// </summary>
    private Creature? RestoreCurrentDemon(Player owner, PersistentPetSnapshot snapshot)
    {
        if (!owner.PetGuid.IsEmpty || snapshot.CharacterId != (int)owner.Guid.Low || !snapshot.IsCurrent || snapshot.PetNumber == 0
            || snapshot.Entry == 0 || snapshot.Health == 0
            || !TryGetSystems(owner, snapshot.CreatedBySpell, out _, out CreatureMapSystem? creatures)
            || creatures.Content.FindTemplate(snapshot.Entry) is not { } template || template.CreatureType != CharmService.CreatureTypeDemon)
        {
            return null;
        }

        TemporarilyUnsummonedPet remembered = RememberedDemon(snapshot, snapshot.CreatedBySpell);
        if (!owner.IsAlive || (owner.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            _temporarilyUnsummoned[owner.Guid] = remembered; // Player::SetTemporaryUnsummonedPetNumber; the pet is not summoned, so no reagent is given back for it
            return null;
        }

        return RestoreSummonedPet(owner, remembered); // restores the cooldowns before the pet bar is sent
    }

    /// <summary>The saved demon <paramref name="snapshot"/> as the pet <see cref="RestoreSummonedPet"/> rebuilds, summoned by <paramref name="spellId"/>.</summary>
    private static TemporarilyUnsummonedPet RememberedDemon(PersistentPetSnapshot snapshot, uint spellId) => new(
        snapshot.PetNumber, snapshot.Entry, spellId, null, snapshot.Health, snapshot.Mana,
        (ReactState)Math.Clamp((int)snapshot.ReactState, 0, 2), snapshot.ActionBar,
        [.. snapshot.Spells.Select(s => new KeyValuePair<uint, ActionType>(s.SpellId, s.Passive ? ActionType.Passive : s.Autocast ? ActionType.Enabled : ActionType.Disabled))],
        snapshot.Name, snapshot.Experience, snapshot.Cooldowns);

    /// <summary>
    /// Unit::EffectSummonPet's <c>LoadPetFromDB(player, petEntry)</c> (SpellEffects.cpp:3221) for a demon the owner saved before: the pet comes back with its
    /// number, name, spells and bar at full health and mana (a SUMMON_PET that is not the current load starts full, Pet.cpp:360-364, and is never loaded dead,
    /// Pet.cpp:415), and the Demonic Sacrifice buffs end as for any known pet (SpellEffects.cpp:3222-3228). Null when the load fails.
    /// </summary>
    private Creature? SummonSavedDemon(Player owner, PersistentPetSnapshot saved, uint spellId)
    {
        // RestoreSummonedPet clamps health and mana to the maximums, so the largest value is "full"
        if (RestoreSummonedPet(owner, RememberedDemon(saved, spellId) with { Health = uint.MaxValue, Mana = uint.MaxValue }) is not { } pet)
        {
            return null;
        }

        RemoveDemonicSacrifice(owner);
        QueueCurrentPetSave(owner); // Pet::SavePetToDB at the end of the summon: it is the current pet again
        return pet;
    }

    private void RestorePetCooldowns(Creature pet, IReadOnlyList<PersistentPetCooldown>? cooldowns)
    {
        if (_spells is { } restoreSpells && cooldowns is { Count: > 0 })
        {
            restoreSpells.RestoreCooldowns(pet, cooldowns.Select(c => new PersistedCooldown(
                c.Kind == 0 ? SpellCooldownKind.Spell : SpellCooldownKind.Category,
                c.Kind == 0 ? c.SpellId : c.Category, c.EndsAtUnixMs)), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
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
            creature.SetUInt32(UpdateFields.UnitFieldPetnumber, snapshot.PetNumber); // a permanent pet: SetPetNumber(n, true), Pet.cpp:262
            // A persistent pet is a HUNTER_PET by kind: a tamed beast loses its innate resistances (Patch 1.3.0), keyed on the pet and not the owner's class.
            creature.MarkHunterPetKind();

            if (creature.Summon.Charm is { } initCharm)
            {
                initCharm.Name = string.IsNullOrWhiteSpace(snapshot.Name) ? creature.Template.Name : snapshot.Name;
                initCharm.NameTimestamp = snapshot.NameTimestamp;
                initCharm.RenameAllowed = snapshot.RenameAllowed;
                // Pet::LoadPetFromDB reads reset_talents_cost and reset_talents_time (Pet.cpp:132-160); the untraining price keeps climbing across a relog.
                initCharm.ResetTalentsCost = snapshot.ResetTalentsCost;
                initCharm.ResetTalentsTime = snapshot.ResetTalentsTime;
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
            if (owner.Class == Class.Hunter)
            {
                // Pet.cpp:382-384: happiness bar (max 1 050 000), Focus power type, 100 focus.
                PetPower.InitHunterPet(creature, snapshot.Happiness);
            }
            else
            {
                creature.SetUInt32(UpdateFields.UnitFieldPower5, snapshot.Happiness);
            }
            if (creature.Summon.Charm is { } reactCharm)
            {
                reactCharm.ReactState = (ReactState)Math.Clamp((int)snapshot.ReactState, 0, 2);
            }
            PetLoyalty.Restore(creature, snapshot.LoyaltyLevel, snapshot.LoyaltyPoints, snapshot.TrainingPoints);
            if (loadLevel < snapshot.Level && creature.Summon.Charm is { } clampCharm)
            {
                // Pet::GivePetLevel (Pet.cpp:1198-1211), run by the clamp after LoadPetFromDB restored XP (Pet.cpp:286) and TP (Pet.cpp:380):
                // experience restarts and the pet earns loyalty level minus one training points (as PetLoyalty.GivePetLevel does in the world).
                creature.SetUInt32(UpdateFields.UnitFieldPetexperience, 0);
                PetLoyalty.SetTrainingPoints(creature, clampCharm.TrainingPoints + (PetLoyalty.Level(creature) - 1));
                // GivePetLevel -> InitStatsForLevel ends with SetHealth(GetMaxHealth()) and SetPower(POWER_MANA, max) (Pet.cpp:1484-1485), after the restore
                // (Pet.cpp:367-368), so a living pet comes back full; a dead one (no saved health) stays dead (JUST_DIED, Pet.cpp:415) unless it is being revived.
                if (snapshot.Health > 0 || revive)
                {
                    creature.Health = creature.MaxHealth;
                    creature.SetUInt32(UpdateFields.UnitFieldPower1, creature.GetUInt32(UpdateFields.UnitFieldMaxpower1));
                }
            }
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
        RestorePetCooldowns(pet, snapshot.Cooldowns);
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
        SendPetSpellsAfterCreate(owner, pet, PetPackets.BuildPetSpells(pet, charm, listSpells: true, _spells?.GetActiveCooldowns(pet) ?? []));
        RememberPetForSpiritHealer(owner, pet); // Pet::LoadPetFromDB: "save pet for resurrection by spirit healer"
        if (_spells is { } auraSpells)
        {
            PetAuras.PetAuraService.For(auraSpells).CastPetAuras(pet, current); // Pet.cpp:356-357: LearnPetPassives, then CastPetAuras(current)
        }

        return pet;
    }
}
