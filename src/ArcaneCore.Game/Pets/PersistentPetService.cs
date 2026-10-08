using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Pets;

/// <summary>Persistence and effect-109 bridge for hunter pets.</summary>
public sealed partial class SummonService
{
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

    /// <summary>Capture only the current player controlled pet; guardians, critters and wild summons are excluded.</summary>
    public PersistentPetSnapshot? CaptureCurrentPet(Player owner)
    {
        if (owner.Class != Class.Hunter || owner.Map?.FindObject(owner.PetGuid) is not Creature { Summon: { Kind: SummonKind.Pet, Charm: not null } } pet
            || pet.OwnerGuid != owner.Guid)
        {
            return null;
        }

        CharmInfo charm = pet.Summon!.Charm!;
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
            Cooldowns: cooldowns, Name: charm.Name, NameTimestamp: charm.NameTimestamp, RenameAllowed: charm.RenameAllowed);
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
        { _logger.LogError(ex, "detached hunter pet save failed for character {CharacterId}", owner.Guid.Low); }
    }

    /// <summary>Capture on the world thread, then finish persistence off-thread without blocking map work.</summary>
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
        Task prior;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cacheGate)
        {
            _cached.Remove(characterId);
            _cacheGeneration[characterId] = _cacheGeneration.GetValueOrDefault(characterId) + 1;
            prior = _saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _saveTails[characterId] = done.Task;
        }
        _ = DeleteQueuedAsync(characterId, prior, done);
    }

    private async Task DeleteQueuedAsync(int characterId, Task prior, TaskCompletionSource done)
    {
        try
        {
            try
            {
                await prior.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "previous persistent pet write failed for character {CharacterId}; continuing with abandon delete", characterId);
            }

            if (DeletePersistence is { } delete)
            {
                await delete(characterId, CancellationToken.None).ConfigureAwait(false);
            }
            else if (Persistence is { } store)
            {
                await store.DeleteAsync(characterId, CancellationToken.None).ConfigureAwait(false);
            }
            done.SetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "persistent hunter pet delete failed for character {CharacterId}", characterId);
            done.SetException(ex);
        }
    }

    private async Task PersistQueuedAsync(PersistentPetSnapshot snapshot)
    {
        try { await PersistSerializedAsync(snapshot, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _logger.LogError(ex, "persistent pet save failed for character {CharacterId}", snapshot.CharacterId); }
    }

    private async Task PersistSerializedAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken)
    {
        Task prior;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cacheGate)
        {
            prior = _saveTails.GetValueOrDefault(snapshot.CharacterId) ?? Task.CompletedTask;
            _saveTails[snapshot.CharacterId] = done.Task;
        }
        try
        {
            try
            {
                await prior.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failed predecessor is retained/logged by its caller; it must not poison
                // the next authoritative snapshot for this character.
                _logger.LogWarning(ex, "previous persistent pet save failed for character {CharacterId}; continuing with newer state", snapshot.CharacterId);
            }
            if (!snapshot.IsCurrent && SaveDetachedPersistence is { } detachedSave)
            {
                await detachedSave(snapshot, cancellationToken).ConfigureAwait(false);
            }
            else if (snapshot.IsCurrent && SavePersistence is { } save)
            {
                await save(snapshot, cancellationToken).ConfigureAwait(false);
            }
            else if (Persistence is { } store)
            {
                if (snapshot.IsCurrent) await store.SaveCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
                else await store.SaveDetachedAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            done.SetResult();
        }
        catch (Exception ex)
        {
            done.SetException(ex);
            throw;
        }
    }

    /// <summary>Drain all per-character writes during world shutdown.</summary>
    public async Task StopAsync()
    {
        Task[] tails;
        lock (_cacheGate) tails = _saveTails.Values.ToArray();
        await Task.WhenAll(tails).ConfigureAwait(false);
    }

    public async Task FlushCharacterAsync(int characterId)
    {
        Task tail;
        lock (_cacheGate) tail = _saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask;
        await tail.ConfigureAwait(false);
    }

    public void ForgetCharacter(int characterId)
    {
        lock (_cacheGate)
        {
            _cached.Remove(characterId);
            // A late read must not see the same generation after deletion.
            _cacheGeneration[characterId] = _cacheGeneration.GetValueOrDefault(characterId) + 1;
            _saveTails.Remove(characterId);
        }
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
        { _logger.LogError(ex, "promoting detached hunter pet failed for character {CharacterId}", snapshot.CharacterId); }
    }

    private async Task SaveQueuedAsync(Player owner)
    {
        try
        {
            await SaveCurrentPetAsync(owner).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "persistent pet save failed for character {CharacterId}", owner.Guid.Low);
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
            creature.Summon.Charm!.Name = string.IsNullOrWhiteSpace(snapshot.Name) ? creature.Template.Name : snapshot.Name;
            creature.Summon.Charm.NameTimestamp = snapshot.NameTimestamp;
            creature.Summon.Charm.RenameAllowed = snapshot.RenameAllowed;
            creature.UnitFlags |= UnitFlags.PetAbandon;
            if (snapshot.RenameAllowed)
            {
                creature.UnitFlags |= UnitFlags.PetRename | UnitFlags.PetAbandon;
            }
            else
            {
                creature.UnitFlags &= ~UnitFlags.PetRename;
            }
            PetInitializer.InitStatsForLevel(creature, owner, snapshot.Level, Content);
            creature.SetUInt32(UpdateFields.UnitFieldPetNameTimestamp, snapshot.NameTimestamp);
            creature.SetUInt32(UpdateFields.UnitFieldPetexperience, snapshot.Experience);
            creature.Health = revive
                ? Math.Clamp(snapshot.Health, 1u, creature.MaxHealth)
                : Math.Min(snapshot.Health, creature.MaxHealth);
            creature.SetUInt32(UpdateFields.UnitFieldPower1, Math.Min(snapshot.Mana, creature.GetUInt32(UpdateFields.UnitFieldMaxpower1)));
            creature.SetUInt32(UpdateFields.UnitFieldPower5, snapshot.Happiness);
            creature.Summon.Charm!.ReactState = (ReactState)Math.Clamp((int)snapshot.ReactState, 0, 2);
            return new CreatureHome(owner.X, owner.Y, owner.Z, owner.Orientation);
        }, snapshot.PetNumber);

        foreach (PersistentPetSpell spell in snapshot.Spells)
        {
            if (_spells is not { } spellSystem || spellSystem.Store.Get(spell.SpellId) is null)
            {
                continue;
            }
            pet.Summon!.Charm!.LearnSpell(spell.SpellId, spell.Passive ? ActionType.Passive : spell.Autocast ? ActionType.Enabled : ActionType.Disabled);
            if (spell.Passive)
            {
                spellSystem.CastSpell(pet, spell.SpellId, SpellCastTargets.ForSelf(), triggered: true);
            }
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
            pet.Summon!.Charm!.SetActionBar(i, button.Action, button.Type);
        }
        pet.UnitFlags |= UnitFlags.PetAbandon;
        if (pet.Summon?.Charm?.RenameAllowed == true)
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
        owner.Session.Send(WorldOpcode.SmsgPetSpells,
            PetPackets.BuildPetSpells(pet, pet.Summon!.Charm!, listSpells: true, _spells?.GetActiveCooldowns(pet) ?? []));
        RememberPetForSpiritHealer(owner, pet); // Pet::LoadPetFromDB: "save pet for resurrection by spirit healer"
        if (_spells is { } auraSpells)
        {
            PetAuras.PetAuraService.For(auraSpells).CastPetAuras(pet, current); // Pet.cpp:356-357: LearnPetPassives, then CastPetAuras(current)
        }

        return pet;
    }
}
