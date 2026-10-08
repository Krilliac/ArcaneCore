using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Pets.PetAuras;

/// <summary>
/// The owner-to-pet talent auras of one spell system (vmangos <c>Unit::m_petAuras</c>, <c>Unit::AddPetAura</c> / <c>RemovePetAura</c>, Unit.cpp:9761-9771,
/// and <c>Pet::CastPetAuras</c> / <c>CastPetAura</c>, Pet.cpp:2302-2330). An owner keeps the set of its <see cref="PetAuraTable"/> spells that are in
/// force; its current pet carries the matching aura, cast by the pet on itself, triggered.
/// <list type="bullet">
/// <item>The owner's spell takes effect (the DUMMY aura is applied, SpellAuras.cpp:2201-2208, or the DUMMY effect runs, SpellEffects.cpp:1497-1502):
/// <see cref="AddPetAura"/> adds it and the pet, if any, gets its aura now.</item>
/// <item>The DUMMY aura goes, or the owner unlearns the spell (Player::RemoveSpell, Player.cpp:3850-3852): <see cref="RemovePetAura"/> drops it and the
/// pet loses the aura.</item>
/// <item>A pet arrives (<see cref="CastPetAuras"/>): a new summon (Pet::InitPetCreateSpells, Pet.cpp:2101, <c>current = false</c>) first drops the owner's
/// spells that end with a pet change (those whose DUMMY effect targets TARGET_UNIT_CASTER_PET, Soul Link) and gets the rest; a loaded current pet
/// (Pet::LoadPetFromDB, Pet.cpp:357) gets them all. Only a permanent pet takes them (Pet::IsPermanentPetFor: a hunter's pet, or a warlock's demon).</item>
/// </list>
/// State is in memory, as in vmangos (m_petAuras is not saved): the passive talent auras restore it at login.
/// </summary>
public sealed class PetAuraService
{
    private static readonly ConditionalWeakTable<SpellSystem, PetAuraService> Services = new();

    private readonly ConditionalWeakTable<Unit, HashSet<uint>> _owners = new();

    private PetAuraService(SpellSystem spells) => Spells = spells;

    /// <summary>The service of <paramref name="spells"/> (created on first use).</summary>
    public static PetAuraService For(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        return Services.GetValue(spells, static s => new PetAuraService(s));
    }

    public SpellSystem Spells { get; }

    /// <summary>The owner's pet aura spells in force (vmangos <c>m_petAuras</c>), in no particular order.</summary>
    public IReadOnlyCollection<uint> GetPetAuras(Unit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _owners.TryGetValue(owner, out HashSet<uint>? set) ? [.. set] : [];
    }

    /// <summary>vmangos Unit::AddPetAura: the spell joins the owner's set and the current pet gets its aura.</summary>
    public void AddPetAura(Unit owner, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (PetAuraTable.Find(spellId) is null)
        {
            return;
        }

        _owners.GetOrCreateValue(owner).Add(spellId);
        if (CurrentPet(owner) is { } pet)
        {
            CastPetAura(pet, spellId);
        }
    }

    /// <summary>vmangos Unit::RemovePetAura: the spell leaves the owner's set and the current pet loses its aura.</summary>
    public void RemovePetAura(Unit owner, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (PetAuraTable.Find(spellId) is not { } entry)
        {
            return;
        }

        if (_owners.TryGetValue(owner, out HashSet<uint>? set))
        {
            set.Remove(spellId);
        }

        if (CurrentPet(owner) is { } pet && entry.AuraFor(pet.Entry) is var aura and not 0)
        {
            Spells.RemoveAuras(pet, aura);
        }
    }

    /// <summary>
    /// vmangos Pet::CastPetAuras(current): for a permanent pet of a player, each spell of the owner's set is cast on it, except that a pet that is
    /// not the current one (a new summon) first ends the spells removed on a pet change.
    /// </summary>
    public void CastPetAuras(Creature pet, bool current)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.GetOwner() is not Player owner || !IsPermanentPetFor(pet, owner))
        {
            return;
        }

        foreach (uint spellId in GetPetAuras(owner))
        {
            if (!current && IsRemovedOnChangePet(spellId))
            {
                RemovePetAura(owner, spellId);
            }
            else
            {
                CastPetAura(pet, spellId);
            }
        }
    }

    /// <summary>
    /// The owner learned a talent: every pet aura of its set that its current pet does not carry is cast on the pet (the talent pet hook; mangos-classic
    /// casts the owner's talent auras here, WorldSession::HandleLearnTalentOpcode).
    /// </summary>
    public void RecastMissing(Unit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (CurrentPet(owner) is not { } pet || owner is not Player player || !IsPermanentPetFor(pet, player))
        {
            return;
        }

        foreach (uint spellId in GetPetAuras(owner))
        {
            if (PetAuraTable.Find(spellId)?.AuraFor(pet.Entry) is { } aura and not 0 && !Spells.HasAura(pet, aura))
            {
                CastPetAura(pet, spellId);
            }
        }
    }

    /// <summary>vmangos Pet::CastPetAura: the pet casts the aura for its entry on itself, triggered (nothing when the spell has none for it).</summary>
    public void CastPetAura(Creature pet, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (PetAuraTable.Find(spellId)?.AuraFor(pet.Entry) is { } aura and not 0)
        {
            Spells.CastSpell(pet, aura, SpellCastTargets.ForSelf(), triggered: true);
        }
    }

    /// <summary>
    /// vmangos <c>PetAura::IsRemovedOnChangePet</c>: the spell's first DUMMY effect or DUMMY aura effect targets TARGET_UNIT_CASTER_PET
    /// (SpellMgr.cpp:2262-2279). A spell missing from the store is kept.
    /// </summary>
    public bool IsRemovedOnChangePet(uint spellId)
    {
        if (Spells.Store.Get(spellId) is not { } spell)
        {
            return false;
        }

        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if ((effect.Effect == SpellEffectName.ApplyAura && effect.AuraType == AuraType.Dummy) || effect.Effect == SpellEffectName.Dummy)
            {
                return effect.TargetA == SpellImplicitTarget.UnitCasterPet;
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos Pet::IsPermanentPetFor: a hunter's pet, or a warlock's summoned pet that is a demon (CREATURE_TYPE_DEMON). Here both are
    /// <see cref="SummonKind.Pet"/>; the owner's class tells them apart.
    /// </summary>
    public static bool IsPermanentPetFor(Creature pet, Player owner)
    {
        ArgumentNullException.ThrowIfNull(pet);
        ArgumentNullException.ThrowIfNull(owner);
        return pet.Summon?.Kind == SummonKind.Pet && owner.Class switch
        {
            Class.Hunter => true,
            Class.Warlock => pet.Template.CreatureType == CharmService.CreatureTypeDemon,
            _ => false,
        };
    }

    /// <summary>vmangos Unit::GetPet: the owner's pet in its map (UNIT_FIELD_SUMMON).</summary>
    private static Creature? CurrentPet(Unit owner) => owner.GetPet() is { Summon.Kind: SummonKind.Pet } pet && pet.OwnerGuid == owner.Guid ? pet : null;
}
