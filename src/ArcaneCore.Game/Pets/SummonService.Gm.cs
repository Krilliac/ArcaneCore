using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

/// <summary>The seams the GM <c>.pet</c> commands need.</summary>
public sealed partial class SummonService
{
    /// <summary>vmangos Creature::IsPet for a summoned pet with a charm (a hunter pet or a warlock demon), not a guardian or minipet.</summary>
    public static bool IsPet(Creature creature) => creature?.Summon is { Kind: SummonKind.Pet, Charm: not null };

    /// <summary>The name a pet shows (its charm's name, or the template's before it was named).</summary>
    public static string PetNameOf(Creature pet) => pet?.Summon?.Charm?.Name is { Length: > 0 } name ? name : pet?.Template.Name ?? string.Empty;

    /// <summary>
    /// vmangos HandlePetLoyaltyCommand -> Pet::ModifyLoyalty: add <paramref name="add"/> loyalty points to a live hunter pet. A Rebellious pet
    /// pushed below zero runs away as it would on its own (SMSG_PET_BROKEN, deleted). Returns false when it ran away.
    /// </summary>
    public bool ModifyLoyaltyByCommand(Creature pet, Player owner, int add)
    {
        ArgumentNullException.ThrowIfNull(pet);
        ArgumentNullException.ThrowIfNull(owner);
        return ModifyLoyalty(pet, owner, add);
    }

    /// <summary>
    /// vmangos HandlePetLearnSpellCommand -> Pet::LearnSpell (Pet::AddSpell, then PetSpellInitialize): the live pet learns
    /// <paramref name="spellId"/> without training points or rank checks; a passive spell is cast on the pet. The owner gets SMSG_PET_SPELLS
    /// and the pet is saved. False for an unknown spell or one the pet knows.
    /// </summary>
    public bool LearnSpellByCommand(Creature pet, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.Summon?.Charm is not { } charm || _spells is not { } spells || spells.Store.Get(spellId) is not { } spell
            || !charm.LearnSpell(spell.Id, spell.IsPassive ? ActionType.Passive : ActionType.Disabled))
        {
            return false;
        }

        if (spell.IsPassive)
        {
            spells.CastSpell(pet, spell.Id, SpellCastTargets.ForSelf(), triggered: true);
        }

        PetSpellsChanged(pet, charm, spells);
        return true;
    }

    /// <summary>
    /// vmangos HandlePetUnlearnSpellCommand -> Pet::UnlearnSpell(spell, learnPrev false, clearActionBar true): the pet forgets the spell, its
    /// auras of it go and it leaves the bar. False when the pet does not know it.
    /// </summary>
    public bool UnlearnSpellByCommand(Creature pet, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.Summon?.Charm is not { } charm || _spells is not { } spells || !charm.UnlearnSpell(spellId))
        {
            return false;
        }

        spells.RemoveAuras(pet, spellId);
        PetSpellsChanged(pet, charm, spells);
        return true;
    }

    private void PetSpellsChanged(Creature pet, CharmInfo charm, SpellSystem spells)
    {
        if (pet.GetOwner() is not Player owner)
        {
            return;
        }

        QueueCurrentPetSave(owner);
        owner.Session.Send(WorldOpcode.SmsgPetSpells, PetPackets.BuildPetSpells(pet, charm, listSpells: true, spells.GetActiveCooldowns(pet)));
    }
}