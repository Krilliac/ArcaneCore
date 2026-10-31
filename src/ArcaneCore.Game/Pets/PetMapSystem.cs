using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Pets;

/// <summary><c>map.Pets</c>: the map's <see cref="PetMapSystem"/> (attached to every map by <see cref="DefaultMapUpdaters"/>).</summary>
public static class MapPetsExtensions
{
    extension(Map map)
    {
        /// <summary>The summoned units of this map (docs/integration/pets.md), or null before the updater is attached.</summary>
        public PetMapSystem? Pets => map.FindUpdater<PetMapSystem>();
    }
}

/// <summary>
/// The summoned units of one map and the rules that end them (totems belong to the shaman lane's
/// <c>Game/Totems/TotemSystem</c>, not to this system): a pet, guardian or mini pet is unsummoned when its owner is
/// gone, more than <see cref="PetOptions.PetLeashDistance"/> away, no longer owns it, dead and
/// out of combat, or when its duration ends (vmangos Pet::Update, Pet.cpp:662-712). The creature
/// itself is an ordinary <see cref="Creature"/> in the map's <see cref="CreatureMapSystem"/>;
/// this system only owns the summon bookkeeping and the timers.
/// <para>Thread affinity: world thread.</para>
/// </summary>
[DefaultMapUpdater(Order = 150)]
public sealed class PetMapSystem : IMapUpdater
{
    private readonly Map _map;
    private readonly List<Creature> _summons = [];
    private readonly Dictionary<ObjectGuid, List<Creature>> _byOwner = [];
    private readonly Dictionary<Creature, ObjectGuid> _ownerOf = new(ReferenceEqualityComparer.Instance);
    private readonly List<Creature> _updateScratch = [];
    private readonly List<Creature> _eventScratch = [];
    private bool _updateScratchInUse;
    private bool _eventScratchInUse;
    private SummonService? _service;
    private bool _combatSubscribed;

    internal PetMapSystem(Map map, WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(world);
        _map = map;
    }

    /// <summary>Tuning (retail defaults); the world feature replaces it from configuration.</summary>
    public PetOptions Options { get; set; } = new();

    /// <summary>Every live summoned creature of this map.</summary>
    public IReadOnlyList<Creature> Summons => _summons;

    /// <summary>The owner's guardians (not totems, not its pet), optionally of one entry (vmangos Unit::m_guardianPets).</summary>
    public IEnumerable<Creature> GuardiansOf(Unit owner, uint entry = 0)
        => _byOwner.TryGetValue(owner.Guid, out List<Creature>? owned)
            ? owned.Where(c => c.Summon is { Kind: SummonKind.Guardian } && c.OwnerGuid == owner.Guid && (entry == 0 || c.Entry == entry))
            : [];

    /// <summary>Every summon the unit owns.</summary>
    public IEnumerable<Creature> SummonsOf(Unit owner)
        => _byOwner.TryGetValue(owner.Guid, out List<Creature>? owned)
            ? owned.Where(c => c.OwnerGuid == owner.Guid)
            : [];

    /// <summary>The owner's current mini pet, if any.</summary>
    public Creature? MiniPetOf(Unit owner)
    {
        if (!_byOwner.TryGetValue(owner.Guid, out List<Creature>? owned))
        {
            return null;
        }

        foreach (Creature c in owned)
        {
            if (c.Summon is { Kind: SummonKind.MiniPet } && c.OwnerGuid == owner.Guid)
            {
                return c;
            }
        }

        return null;
    }

    internal void Register(Creature creature, SummonService service)
    {
        _service = service;
        SubscribeCombat();
        _summons.Add(creature);
        ObjectGuid ownerGuid = creature.OwnerGuid;
        if (!ownerGuid.IsEmpty)
        {
            _ownerOf[creature] = ownerGuid;
            if (!_byOwner.TryGetValue(ownerGuid, out List<Creature>? owned))
            {
                owned = [];
                _byOwner[ownerGuid] = owned;
            }

            owned.Add(creature);
        }
    }

    private void SubscribeCombat()
    {
        if (!_combatSubscribed && _map.FindUpdater<MapCombat>() is { } combat)
        {
            combat.DamageDealt += OnDamageDealt;
            combat.UnitKilled += OnUnitKilled;
            _combatSubscribed = true;
        }
    }

    /// <summary>
    /// vmangos Player::KilledMonster / Unit::DealDamage: when the owner kills a unit, its living pet and guardians hear
    /// <see cref="PetAI.OnKilledUnit"/> so they select the next target or leave combat and return.
    /// </summary>
    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (killer is null || !_byOwner.TryGetValue(killer.Guid, out List<Creature>? owned) || owned.Count == 0)
        {
            return;
        }

        bool ownsScratch = !_eventScratchInUse;
        List<Creature> snapshot = ownsScratch ? _eventScratch : new List<Creature>(owned.Count);
        if (ownsScratch)
        {
            _eventScratchInUse = true;
            snapshot.Clear();
        }

        snapshot.AddRange(owned);
        try
        {
            foreach (Creature pet in snapshot)
            {
                try
                {
                    if (!ReferenceEquals(pet, killer) && pet.OwnerGuid == killer.Guid && pet.IsAlive
                        && pet.Summon is { Kind: SummonKind.Pet or SummonKind.Guardian } && PetAI.Of(pet.AI) is { } ai)
                    {
                        ai.OnKilledUnit(victim);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.pet", ex);
                }
            }
        }
        finally
        {
            if (ownsScratch)
            {
                snapshot.Clear();
                _eventScratchInUse = false;
            }
        }
    }

    /// <summary>
    /// vmangos Unit::AttackedBy and the spell/melee damage path (Unit.cpp:4541, 4563, 6080): the pets and
    /// guardians of a unit that was hit hear <c>OwnerAttackedBy</c>; those of a unit that dealt damage hear
    /// <c>OwnerAttacked</c>.
    /// </summary>
    private void OnDamageDealt(Unit attacker, Unit victim, uint damage, bool direct, bool meleeDamage)
    {
        if (_byOwner.Count == 0)
        {
            return;
        }

        _byOwner.TryGetValue(victim.Guid, out List<Creature>? victimPets);
        _byOwner.TryGetValue(attacker.Guid, out List<Creature>? attackerPets);
        int vCount = victimPets?.Count ?? 0;
        int aCount = attackerPets is not null && !ReferenceEquals(attackerPets, victimPets) ? attackerPets.Count : 0;
        if (vCount == 0 && aCount == 0)
        {
            return;
        }

        bool ownsScratch = !_eventScratchInUse;
        List<Creature> snapshot = ownsScratch ? _eventScratch : new List<Creature>(vCount + aCount);
        if (ownsScratch)
        {
            _eventScratchInUse = true;
            snapshot.Clear();
        }

        if (vCount > 0)
        {
            snapshot.AddRange(victimPets!);
        }

        if (aCount > 0)
        {
            snapshot.AddRange(attackerPets!);
        }

        try
        {
            foreach (Creature pet in snapshot)
            {
                try
                {
                    if (PetAI.Of(pet.AI) is not { } ai || !pet.IsAlive || pet.Summon is not { Kind: SummonKind.Pet or SummonKind.Guardian })
                    {
                        continue;
                    }

                    if (pet.OwnerGuid == victim.Guid)
                    {
                        ai.OwnerAttackedBy(attacker);
                    }
                    else if (pet.OwnerGuid == attacker.Guid)
                    {
                        ai.OwnerAttacked(victim);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.pet", ex);
                }
            }
        }
        finally
        {
            if (ownsScratch)
            {
                snapshot.Clear();
                _eventScratchInUse = false;
            }
        }
    }

    /// <summary>Drop the bookkeeping of a creature that left the map (it was killed, unloaded or unsummoned).</summary>
    internal void Forget(Creature creature)
    {
        if (!_summons.Remove(creature))
        {
            return;
        }

        if (_ownerOf.Remove(creature, out ObjectGuid ownerGuid) && _byOwner.TryGetValue(ownerGuid, out List<Creature>? owned))
        {
            owned.Remove(creature);
            if (owned.Count == 0)
            {
                _byOwner.Remove(ownerGuid);
            }
        }
    }

    public void Update(Map map, uint diffMs)
    {
        if (_summons.Count == 0)
        {
            return;
        }

        bool ownsScratch = !_updateScratchInUse;
        List<Creature> snapshot = ownsScratch ? _updateScratch : new List<Creature>(_summons.Count);
        if (ownsScratch)
        {
            _updateScratchInUse = true;
            snapshot.Clear();
        }

        snapshot.AddRange(_summons);
        try
        {
            foreach (Creature creature in snapshot)
            {
                try
                {
                    if (creature.System is not { } system || !ReferenceEquals(system.FindCreature(creature.Guid), creature) || creature.Summon is not { } links)
                    {
                        // Killed and decayed, or its grid unloaded. vmangos Pet::Update unsummons a pet whose
                        // corpse timer ran out (Pet.cpp:679-686): the owner's pet link and action bar go too.
                        if (creature.Summon is { Kind: SummonKind.Pet } && _map.FindObject(creature.OwnerGuid) is Player petOwner)
                        {
                            _service?.QueueCurrentPetSave(petOwner);
                            SummonService.ReleasePetLink(creature, petOwner);
                        }

                        Forget(creature);
                        continue;
                    }

                    Unit? owner = creature.GetOwner();
                    switch (links.Kind)
                    {
                        case SummonKind.Wild:
                            UpdateWild(creature, links, diffMs);
                            break;
                        default:
                            UpdatePet(creature, links, owner, diffMs);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.pet", ex);
                }
            }
        }
        finally
        {
            if (ownsScratch)
            {
                snapshot.Clear();
                _updateScratchInUse = false;
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        // A map removal also happens on far-map transfer. Save the stable pet identity and state
        // before the transient world GUID is destroyed (vmangos Pet::SavePetToDB before removal).
        _service?.QueueCurrentPetSave(player);

        // The owner leaves the map, so its summons cannot stay (vmangos Player::RemoveFromWorld unsummons totems and the mini pet,
        // Unit::RemoveFromWorld the guardians and the pet). A far teleport has already put the pet away by then and brings it back in
        // the new map (PetTeleportFollow, vmangos UnsummonPetTemporaryIfAny); guardians and mini pets stay gone.
        foreach (Creature creature in SummonsOf(player).ToArray())
        {
            _service?.Unsummon(creature);
        }
    }

    /// <summary>
    /// vmangos Pet::SetDeathState(CORPSE) (Pet.cpp:642-645): a hunter pet that dies loses one happiness level, except on a battleground map.
    /// </summary>
    internal void LoseHappinessOnDeath(Creature pet, Player owner)
    {
        if (owner.Class != Class.Hunter || pet.Summon is not { Kind: SummonKind.Pet })
        {
            return;
        }

        if (Maps.Templates.WorldMaps.Of(_map.World).Registry.Find(_map.MapId) is { IsBattleground: true })
        {
            return;
        }

        uint happiness = pet.GetUInt32(UpdateFields.UnitFieldPower5);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, happiness > SummonService.HappinessLevelSize ? happiness - SummonService.HappinessLevelSize : 0);
    }

    internal void SaveCurrentPet(Player owner) => _service?.QueueCurrentPetSave(owner);

    /// <summary>
    /// vmangos TemporarySummon::Update, TEMPSUMMON_TIMED_DEATH_AND_DEAD_DESPAWN (TemporarySummon.cpp:200-218):
    /// when the timer has run out the creature is killed unless it is in combat (it is retried every
    /// tick, <c>m_timer = 0</c>); the corpse is then removed by its creature system like any temporary
    /// summon. Without a duration (TEMPSUMMON_DEAD_DESPAWN) it only ends with its death. A summon marked
    /// <see cref="SummonLinks.DespawnsWhenTimeRunsOut"/> (TEMPSUMMON_TIMED_COMBAT_OR_DEAD_DESPAWN, TemporarySummon.cpp:171-196: the Doomguard)
    /// is unsummoned instead, once it is out of combat.
    /// </summary>
    private void UpdateWild(Creature wild, SummonLinks links, uint diffMs)
    {
        if (!links.HasTimer)
        {
            return;
        }

        if (links.RemainingMs <= diffMs)
        {
            links.RemainingMs = 0;
            if (links.DespawnsWhenTimeRunsOut)
            {
                // TEMPSUMMON_TIMED_COMBAT_OR_DEAD_DESPAWN: unsummoned once out of combat; a dead one is left to its corpse.
                if (wild.IsAlive && !wild.Combat.IsInCombat)
                {
                    _service?.Unsummon(wild);
                }

                return;
            }

            if (!wild.Combat.IsInCombat && wild.IsAlive)
            {
                wild.System?.KillCreature(wild);
            }
        }
        else
        {
            links.RemainingMs -= (int)diffMs;
        }
    }

    /// <summary>vmangos Pet::Update (Pet.cpp:662-712) for the states a creature can be in here.</summary>
    private void UpdatePet(Creature pet, SummonLinks links, Unit? owner, uint diffMs)
    {
        // The leash does not hold a pet its owner is possessing (!(owner->GetCharmGuid() == GetObjectGuid()), Pet.cpp:670). A pet that lost its
        // owner, is left behind or whose owner has no pet any more gives its reagents back (Unsummon(PET_SAVE_REAGENTS), Pet.cpp:668-674); one
        // its owner replaced does not (PET_SAVE_NOT_IN_SLOT, Pet.cpp:688-694).
        if (owner is null || (!IsWithinLeash(pet, owner, Options) && owner.CharmGuid != pet.Guid)
            || (links.Kind == SummonKind.Pet && owner.PetGuid.IsEmpty))
        {
            // vmangos Unsummon(PET_SAVE_REAGENTS), which SavePetToDB stores as PET_SAVE_NOT_IN_SLOT: the owner's current pet is saved
            // first (a pet that is no longer the owner's current one is not, as it is not captured as current).
            if (owner is Player petOwner && links.Kind == SummonKind.Pet && petOwner.PetGuid == pet.Guid)
            {
                _service?.QueueCurrentPetSave(petOwner);
            }

            if (_service is { } service)
            {
                service.UnsummonReturningReagents(pet);
            }

            return;
        }

        if (links.Kind == SummonKind.Pet && owner.PetGuid != pet.Guid)
        {
            _service?.Unsummon(pet); // replaced by another pet: not the owner's current one, so not saved
            return;
        }

        // vmangos Player::SetDeathState(JUST_DIED): RemovePet(PET_SAVE_REAGENTS) and RemoveMiniPet() (Player.cpp:1527-1531)
        // take a dead player's pet and mini pet at once, in combat or not; its guardians keep Pet::Update's rule below.
        if (owner is Player deadOwner && !owner.IsAlive && links.Kind is SummonKind.Pet or SummonKind.MiniPet)
        {
            if (links.Kind == SummonKind.Pet)
            {
                _service?.QueueCurrentPetSave(deadOwner);
                _service?.UnsummonReturningReagents(pet); // RemovePet(PET_SAVE_REAGENTS)
            }
            else
            {
                _service?.Unsummon(pet);
            }

            return;
        }

        if (pet.DeathState != CreatureDeathState.Alive)
        {
            // vmangos CORPSE: the decay timer running out unsummons (Pet.cpp:677-686); the creature
            // system decays the corpse itself, and the removal is handled in Update.
            if (pet.DeathState == CreatureDeathState.Corpse && pet.CorpseDecayMs <= diffMs)
            {
                if (owner is Player player)
                {
                    _service?.QueueCurrentPetSave(player);
                }
                _service?.Unsummon(pet);
            }

            return;
        }

        // Despawn if the owner is dead and the pet is out of combat.
        if (!owner.IsAlive && pet.Combat.Victim is null && pet.Combat.Attackers.Count == 0)
        {
            if (owner is Player player)
            {
                _service?.QueueCurrentPetSave(player);
            }
            _service?.Unsummon(pet);
            return;
        }

        // vmangos Pet::Update (HUNTER_PET): happiness loss and the loyalty tick; a Rebellious pet whose loyalty runs out leaves.
        if (links.Kind == SummonKind.Pet && owner is Player { Class: Class.Hunter } hunter && _service is { } happiness
            && !happiness.TickHappiness(pet, hunter, diffMs))
        {
            return;
        }

        if (links.RemainingMs > 0)
        {
            if (links.RemainingMs > (int)diffMs)
            {
                links.RemainingMs -= (int)diffMs;
            }
            else
            {
                _service?.Unsummon(pet);
            }
        }
    }

    /// <summary>vmangos <c>IsWithinDistInMap(owner, 120)</c>: the 3D distance less both bounding radii.</summary>
    private static bool IsWithinLeash(Creature pet, Unit owner, PetOptions options)
    {
        float dx = pet.X - owner.X;
        float dy = pet.Y - owner.Y;
        float dz = pet.Z - owner.Z;
        float limit = options.PetLeashDistance + pet.BoundingRadius + owner.BoundingRadius;
        return ReferenceEquals(pet.Map, owner.Map) && (dx * dx) + (dy * dy) + (dz * dz) <= limit * limit;
    }
}
