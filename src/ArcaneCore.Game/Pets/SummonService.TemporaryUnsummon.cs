using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

// A pet that is put away for a while and comes back (vmangos Player::UnsummonPetTemporaryIfAny / ResummonPetTemporaryUnSummonedIfAny,
// Player.cpp:20911-20940, and m_temporaryUnsummonedPetNumber): a far teleport, a near teleport out of the pet's reach.
public sealed partial class SummonService
{
    private readonly Dictionary<ObjectGuid, TemporarilyUnsummonedPet> _temporarilyUnsummoned = [];

    /// <summary>Whether the player has a pet put away by <see cref="UnsummonPetTemporarily"/> (vmangos <c>m_temporaryUnsummonedPetNumber != 0</c>).</summary>
    public bool HasTemporarilyUnsummonedPet(Player owner) => _temporarilyUnsummoned.ContainsKey(owner.Guid);

    /// <summary>
    /// vmangos Player::UnsummonPetTemporaryIfAny: the player's pet is unsummoned (a hunter pet saved as current, PET_SAVE_AS_CURRENT) and,
    /// when it is a controlled pet that is not a temporary summon and none is remembered yet, remembered for
    /// <see cref="ResummonTemporarilyUnsummonedPet"/>. The pet number, level, health, mana, react state, action bar and spells come back;
    /// a hunter pet comes back from its current-pet snapshot (vmangos LoadPetFromDB(current = true)). A dead pet is unsummoned but not
    /// remembered: vmangos would load it as a corpse (Pet.cpp:410-413); here a dead hunter pet stays dead in its saved snapshot, where
    /// Revive Pet (effect 109) finds it. Returns false when the player has no pet.
    /// </summary>
    public bool UnsummonPetTemporarily(Player owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.PetGuid.IsEmpty || owner.Map?.FindObject(owner.PetGuid) is not Creature { Summon: { Kind: SummonKind.Pet } links } pet
            || pet.OwnerGuid != owner.Guid)
        {
            return false;
        }

        // "if (!m_temporaryUnsummonedPetNumber && pet->IsControlled() && !pet->IsTemporarySummoned())"
        if (!_temporarilyUnsummoned.ContainsKey(owner.Guid) && links.Charm is { } charm && !links.HasTimer && pet.IsAlive
            && Capture(owner, pet, links, charm) is { } remembered)
        {
            _temporarilyUnsummoned[owner.Guid] = remembered;
        }

        if (owner.Class == Class.Hunter)
        {
            QueueCurrentPetSave(owner);
        }

        Unsummon(pet);
        return true;
    }

    /// <summary>
    /// vmangos Player::ResummonPetTemporaryUnSummonedIfAny: bring back the remembered pet next to the player. Nothing happens while the
    /// player could not have a pet (vmangos IsPetNeedBeTemporaryUnsummoned: not in the world, dead, on a taxi flight; the pet stays
    /// remembered) or already has one. Returns the pet, or null.
    /// </summary>
    public Creature? ResummonTemporarilyUnsummonedPet(Player owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!_temporarilyUnsummoned.TryGetValue(owner.Guid, out TemporarilyUnsummonedPet? remembered))
        {
            return null;
        }

        if (!owner.IsInWorld || owner.Map is null || !owner.IsAlive || (owner.UnitFlags & UnitFlags.TaxiFlight) != 0 || !owner.PetGuid.IsEmpty)
        {
            return null;
        }

        _temporarilyUnsummoned.Remove(owner.Guid); // m_temporaryUnsummonedPetNumber = 0, whether the load worked or not
        return remembered.Hunter is { } hunter
            ? RestoreHunterPet(owner, hunter)
            : RestoreSummonedPet(owner, remembered);
    }

    /// <summary>Forget the remembered pet of a player that leaves the world (its saved hunter pet is the logout's to keep).</summary>
    public void ForgetTemporarilyUnsummonedPet(Player owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _temporarilyUnsummoned.Remove(owner.Guid);
    }

    private TemporarilyUnsummonedPet? Capture(Player owner, Creature pet, SummonLinks links, CharmInfo charm)
    {
        if (owner.Class == Class.Hunter && links.SpellId == 0)
        {
            return CaptureCurrentPet(owner) is { } snapshot ? new TemporarilyUnsummonedPet(charm.PetNumber, pet.Entry, links.SpellId, snapshot, 0, 0, 0, [], [], "", 0) : null;
        }

        return new TemporarilyUnsummonedPet(
            charm.PetNumber, pet.Entry, links.SpellId, null, pet.Health, pet.GetUInt32(UpdateFields.UnitFieldPower1), charm.ReactState,
            [.. charm.ActionBar.Select(button => button.Packed)], [.. charm.SpellStates], charm.Name, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
    }

    private Creature? RestoreHunterPet(Player owner, PersistentPetSnapshot snapshot)
    {
        // The current-pet snapshot written at the unsummon, or a newer one (a rename meanwhile) when the cache holds it.
        PersistentPetSnapshot current = TryGetCachedCurrentPet(owner, out PersistentPetSnapshot cached) && cached.PetNumber == snapshot.PetNumber
            ? cached
            : snapshot;
        return RestoreCurrentPet(owner, current with { IsCurrent = true });
    }

    /// <summary>
    /// The summoned pet (a warlock demon, a SPELL_EFFECT_SUMMON pet) as vmangos Pet::LoadPetFromDB rebuilds it for its owner: the same
    /// pet number, entry and spell, the owner's level (SynchronizeLevelWithOwner), the saved health, mana, experience, react state, name,
    /// spells and action bar, at the owner's close point (PET_FOLLOW_DIST, PET_FOLLOW_ANGLE).
    /// </summary>
    private Creature? RestoreSummonedPet(Player owner, TemporarilyUnsummonedPet remembered)
    {
        if (!TryGetSystems(owner, remembered.SpellId, out PetMapSystem? pets, out CreatureMapSystem? creatures)
            || creatures.Content.FindTemplate(remembered.Entry) is not { } template)
        {
            Warn($"resummon-template:{remembered.Entry}", "creature entry {Entry} of a temporarily unsummoned pet not found", remembered.Entry);
            return null;
        }

        ReservePetNumber(remembered.PetNumber);
        (float x, float y) = ClosePoint(owner, 0.0f, PetConstants.FollowDistance, owner.Orientation + PetConstants.FollowAngle);
        Creature pet = creatures.SpawnSummoned(template, HighGuid.Pet, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Pet, owner.Guid, remembered.SpellId, TotemSlots.None, 0);
            ApplyOwner(creature, owner, remembered.SpellId);
            InitPet(creature, SummonKind.Pet, owner, remembered.PetNumber);
            creature.SetUInt32(UpdateFields.UnitFieldPetexperience, remembered.Experience);
            creature.SetUInt32(UpdateFields.UnitFieldPetnextlevelexp, 1000);
            creature.NpcFlags = 0;
            PetInitializer.InitStatsForLevel(creature, owner, owner.Level, Content);
            creature.Health = Math.Clamp(remembered.Health, 1u, creature.MaxHealth);
            creature.SetUInt32(UpdateFields.UnitFieldPower1, Math.Min(remembered.Mana, creature.GetUInt32(UpdateFields.UnitFieldMaxpower1)));
            creature.Summon.Charm!.ReactState = remembered.ReactState;
            if (!string.IsNullOrWhiteSpace(remembered.Name))
            {
                creature.Summon.Charm.Name = remembered.Name;
            }

            return new CreatureHome(x, y, owner.Z, Creature.NormalizeOrientation(-owner.Orientation));
        }, remembered.PetNumber);

        pets.Options = _options;
        pets.Register(pet, this);
        AttachPetAi(pet);
        CharmInfo charm = pet.Summon!.Charm!;
        foreach ((uint spellId, ActionType state) in remembered.Spells)
        {
            if (_spells is not { } spellSystem || spellSystem.Store.Get(spellId) is null)
            {
                continue;
            }

            charm.LearnSpell(spellId, state);
            if (state == ActionType.Passive)
            {
                spellSystem.CastSpell(pet, spellId, SpellCastTargets.ForSelf(), triggered: true);
            }
        }

        for (int i = 0; i < Math.Min(remembered.ActionBar.Count, CharmInfo.ActionBarSize); i++)
        {
            ActionButton button = new(remembered.ActionBar[i]);
            charm.SetActionBar(i, button.Action, button.Type);
        }

        owner.SetPetGuid(pet.Guid);
        owner.Session.Send(WorldOpcode.SmsgPetSpells,
            PetPackets.BuildPetSpells(pet, charm, listSpells: true, _spells?.GetActiveCooldowns(pet) ?? []));
        RememberPetForSpiritHealer(owner, pet); // Pet::LoadPetFromDB: "save pet for resurrection by spirit healer"
        return pet;
    }

    private sealed record TemporarilyUnsummonedPet(
        uint PetNumber,
        uint Entry,
        uint SpellId,
        PersistentPetSnapshot? Hunter,
        uint Health,
        uint Mana,
        ReactState ReactState,
        IReadOnlyList<uint> ActionBar,
        IReadOnlyList<KeyValuePair<uint, ActionType>> Spells,
        string Name,
        uint Experience);
}
