using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Warlock;
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
    /// LIMITS: entry 0 (the hunter's Call Pet) is the persistent hunter pet path of <see cref="Install"/>; no demon is saved or loaded (vmangos <c>LoadPetFromDB</c> / <c>SavePetToDB</c>: a warlock gets a
    /// fresh demon of its level each time and no saved name, level or happiness), the random pet name (<c>GeneratePetName</c>) is not generated so the pet keeps
    /// the creature name, and the soul shard of Summon Voidwalker, Succubus and Felhunter is a reagent, not charged here.
    /// </para>
    /// </summary>
    private void EffectSummonPet(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        SpellInfo spell = context.Spell;
        uint entry = (uint)context.Effect.MiscValue;
        int petLevel = caster is Player ? caster.Level : Math.Max(caster.Level + (int)context.Effect.MultipleValue, 1);
        if (!UnsummonOldPetBeforeNewSummon(caster, entry))
        {
            return;
        }

        if (entry == 0 || !TryGetSystems(caster, spell.Id, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return;
        }

        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            Warn($"demon-template:{entry}", "creature entry {Entry} not found for spell {Spell}", entry, spell.Id);
            return;
        }

        (float x, float y) = ClosePoint(caster, 0.0f, PetConstants.FollowDistance, caster.Orientation + PetConstants.FollowAngle);
        float z = caster.Z;
        uint petNumber = NextPetNumber();
        Creature pet = creatures.SpawnSummoned(template, HighGuid.Pet, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Pet, caster.Guid, spell.Id, TotemSlots.None, 0);
            ApplyOwner(creature, caster, spell.Id);
            InitPet(creature, SummonKind.Pet, caster, petNumber);
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

        // Player::PetSpellInitialize
        if (caster is Player owner)
        {
            owner.Session.Send(WorldOpcode.SmsgPetSpells, PetPackets.BuildPetSpells(pet, pet.Summon!.Charm!, listSpells: true));
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

            Unsummon(old);
            return true;
        }

        if (caster is Player && newEntry != 0)
        {
            Unsummon(old);
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
