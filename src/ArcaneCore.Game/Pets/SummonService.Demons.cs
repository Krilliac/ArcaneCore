using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

// The warlock demons (SPELL_EFFECT_SUMMON_PET 56: Summon Imp, Voidwalker, Succubus, Felhunter).
public sealed partial class SummonService
{
    /// <summary>
    /// Serve the demon half of SPELL_EFFECT_SUMMON_PET (a non-zero creature entry) on <paramref name="spells"/>; the hunter's Call Pet half (entry 0) is
    /// <see cref="Install"/>'s. Throws when the demons are installed twice, or when another owner (not this service) already handles the effect: two owners
    /// of one effect would disagree silently. Call it once, next to <see cref="Install"/>; the world installs it from <c>WarlockDemonFeature</c>.
    /// </summary>
    public void InstallDemons(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (_demonsInstalled)
        {
            throw new InvalidOperationException("SPELL_EFFECT_SUMMON_PET demons are already installed");
        }

        _spells ??= spells;
        RegisterSummonPet(spells);
        _demonsInstalled = true;
    }

    /// <summary>
    /// vmangos Spell::EffectSummonPet and Unit::EffectSummonPet (SpellEffects.cpp:3171-3327), without the database half. The level is the caster's, for a
    /// non-player caster its level plus EffectMultipleValue (at least 1). A player's old pet, alive or dead, is dismissed first
    /// (<c>UnsummonOldPetBeforeNewSummon</c>, Unit.cpp:5116-5145); a non-player keeps a living pet and the summon is refused, a dead one of the same entry
    /// is replaced. The pet appears at the owner's close point 2 yards at pi / 2 (PET_FOLLOW_DIST, PET_FOLLOW_ANGLE) facing minus the owner's orientation,
    /// gets the owner's faction, the spell, level stats from <c>pet_levelstats</c>, its create spells and the pet bar, and the Demonic Sacrifice buffs
    /// (override class script 2228) of the owner end.
    /// <para>
    /// A player first tries the demon of that entry it saved before (<c>LoadPetFromDB(player, petEntry)</c>, SpellEffects.cpp:3221; <see cref="SummonSavedDemon"/>): an
    /// Imp keeps its name, number and spells across a dismissal, a Voidwalker swap, its death or a relog, and comes back at full health and mana (a SUMMON_PET that is not the current load, Pet.cpp:360-364, 415; a relog's current load keeps the saved health instead). Only without a saved demon is a new one made, with a generated
    /// name (<c>ObjectMgr::GeneratePetName</c>, <see cref="PetContent.GeneratePetName"/>, SpellEffects.cpp:3293) and a new pet number. Either way it is saved as the
    /// owner's current pet (<see cref="QueueCurrentPetSave"/>; <c>SavePetToDB</c>) and a relog brings it back (<c>SummonService.PersistentPets.cs</c>). A dismissal or a
    /// replacement saves its final state under its entry and leaves the current slot (<see cref="AbandonSavedDemon"/>; PET_SAVE_NOT_IN_SLOT, Unit.cpp:5129-5136).
    /// </para>
    /// <para>
    /// LIMITS: entry 0 (the hunter's Call Pet) is the persistent hunter pet path of <see cref="Install"/>; a demon with no <c>pet_name_generation</c> rows
    /// for its entry keeps the creature name (vmangos then answers the creature family's pet name, and no CreatureFamily pet-name table is loaded), and the
    /// soul shard of Summon Voidwalker, Succubus and Felhunter is a reagent, not charged here.
    /// </para>
    /// </summary>
    private void EffectSummonPet(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        int petLevel = caster is Player ? caster.Level : Math.Max(caster.Level + (int)context.Effect.MultipleValue, 1);
        SummonDemon(caster, context.Spell.Id, (uint)context.Effect.MiscValue, petLevel);
    }

    /// <summary>vmangos Unit::EffectSummonPet(spellId, petEntry, petLevel) for a demon entry (SpellEffects.cpp:3183-3327), without the database half.</summary>
    private Creature? SummonDemon(Unit caster, uint spellId, uint entry, int petLevel)
    {
        if (!UnsummonOldPetBeforeNewSummon(caster, entry))
        {
            return null;
        }

        if (entry == 0 || !TryGetSystems(caster, spellId, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return null;
        }

        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            Warn($"demon-template:{entry}", "creature entry {Entry} not found for spell {Spell}", entry, spellId);
            return null;
        }

        if (caster is Player summoner && FindSavedDemon(summoner, entry) is { } saved)
        {
            return SummonSavedDemon(summoner, saved, spellId);
        }

        (float x, float y, float z) = SafeClosePoint(caster, creatures, 0.0f, PetConstants.FollowDistance, caster.Orientation + PetConstants.FollowAngle);
        uint petNumber = NextPetNumber();
        Creature pet = creatures.SpawnSummoned(template, HighGuid.Pet, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Pet, caster.Guid, spellId, TotemSlots.None, 0);
            ApplyOwner(creature, caster, spellId);
            InitPet(creature, SummonKind.Pet, caster, petNumber);
            if (caster is Player)
            {
                // Unit::EffectSummonPet: "generate new name for summon pet" (SpellEffects.cpp:3293-3298); without name parts the creature name stays
                if (Content.GeneratePetName(entry, _random) is { Length: > 0 } generated)
                {
                    creature.Summon!.Charm!.Name = generated;
                }

                // CharmInfo::SetPetNumber(petNumber, true) for a player-owned summon (SpellEffects.cpp:3269): the client's pet frame, name query and stat window need it
                creature.SetUInt32(UpdateFields.UnitFieldPetnumber, petNumber);
            }

            creature.SetUInt32(UpdateFields.UnitFieldPetexperience, 0);
            creature.SetUInt32(UpdateFields.UnitFieldPetnextlevelexp, 1000);
            creature.NpcFlags = 0;
            PetInitializer.InitStatsForLevel(creature, caster, petLevel, Content);
            return new CreatureHome(x, y, z, Creature.NormalizeOrientation(-caster.Orientation));
        }, petNumber);

        pets.Options = _options;
        pets.Register(pet, this);
        AttachPetAi(pet);
        PetInitializer.InitCreateSpells(pet, Content, _spells);
        caster.SetPetGuid(pet.Guid);
        RemoveDemonicSacrifice(caster);

        // Player::PetSpellInitialize; the spirit healer's re-summon remembers the pet (m_petEntry, m_petSpell, SpellEffects.cpp:3317-3324)
        if (caster is Player owner)
        {
            SendPetSpellsAfterCreate(owner, pet, PetPackets.BuildPetSpells(pet, pet.Summon!.Charm!, listSpells: true));
            RememberPetForSpiritHealer(owner, pet);
            QueueCurrentPetSave(owner); // Pet::SavePetToDB: a warlock's demon is the current pet of character_pet and survives a relog
        }

        return pet;
    }

    /// <summary>
    /// A warlock's demon that is leaving the current slot for good (dismissed, replaced by a new summon, or unsummoned with its reagents given back) saves its
    /// final state under its entry, so summoning that entry again loads the same pet, and its current-pet row is deleted behind every save already queued, so a
    /// relog does not bring it back (vmangos unsummons it with PET_SAVE_NOT_IN_SLOT, which keeps the row out of the slot LoadPetFromDB reads at login, Pet.cpp:495-514).
    /// A hunter's pet and every other summon are not touched. Call before <see cref="Unsummon(Creature, PetUnsummonReason)"/> clears the owner's pet link.
    /// </summary>
    internal void AbandonSavedDemon(Unit owner, Creature pet)
    {
        if (owner is Player player && pet.Summon is { Kind: SummonKind.Pet } && pet.OwnerGuid == player.Guid && IsPermanentPetFor(player, pet))
        {
            if (player.PetGuid == pet.Guid)
            {
                QueueCurrentPetSave(player);
            }

            QueueDeletePet(player, numberStaysInUse: true);
        }
    }

    /// <summary>
    /// vmangos Unit::UnsummonOldPetBeforeNewSummon(entry, canUnsummon = true): false when the summon must not go on. A player's old pet is dismissed (and
    /// its link cleared); a non-player only replaces a dead pet of the same entry.
    /// </summary>
    private bool UnsummonOldPetBeforeNewSummon(Unit caster, uint newEntry)
    {
        if (caster.GetPet() is not { } old)
        {
            return true;
        }

        if (!old.IsAlive && (newEntry == 0 || old.Entry == newEntry))
        {
            if (newEntry == 0)
            {
                return false; // a pet in corpse state can't be unsummoned by a call pet
            }

            AbandonSavedDemon(caster, old);
            Unsummon(old, PetUnsummonReason.Replaced);
            return true;
        }

        if (caster is Player && newEntry != 0)
        {
            AbandonSavedDemon(caster, old);
            Unsummon(old, PetUnsummonReason.Replaced);
            return true;
        }

        return false;
    }

    /// <summary>The Demonic Sacrifice buffs of the owner end with a new pet (SpellEffects.cpp:3218-3228).</summary>
    private void RemoveDemonicSacrifice(Unit owner)
    {
        if (_spells is not { } spells)
        {
            return;
        }

        foreach (SpellAuraHolder holder in spells.GetAuras(owner)
                     .Where(h => !h.IsRemoved && h.Auras.Any(a => a is { Type: AuraType.OverrideClassScripts } && a.MiscValue == DemonicSacrificeScript.ClassScriptMisc))
                     .ToArray())
        {
            spells.RemoveAuras(owner, holder.Spell.Id);
        }
    }
}
