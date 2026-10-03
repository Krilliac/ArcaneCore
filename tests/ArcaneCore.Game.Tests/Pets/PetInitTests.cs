using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>Pet::InitStatsForLevel (pet_levelstats) and Pet::InitPetCreateSpells (petcreateinfo_spell) on a summoned pet.</summary>
public sealed class PetInitTests
{
    private static PetContent Content() => new(
        [
            new PetLevelStats(PetEntry, 1, Health: 80, Mana: 40, Armor: 30, MinDamage: 3, MaxDamage: 5, Strength: 11, Agility: 12, Stamina: 13, Intellect: 14, Spirit: 15),
            new PetLevelStats(PetEntry, 20, Health: 400, Mana: 300, Armor: 200, MinDamage: 0, MaxDamage: 0, Strength: 31, Agility: 32, Stamina: 33, Intellect: 34, Spirit: 35),
        ],
        [new PetCreateSpells(PetEntry, [LearnShieldSpell, PetBiteSpell, 999_999, PetPassiveSpell])]);

    [Fact]
    public void SummonedPet_TakesItsStatsFromPetLevelstats_AndStartsWithItsFlagsCleared()
    {
        using var kit = new PetTestKit(petContent: Content());
        (Player owner, _) = kit.AddPlayer(1);
        owner.UnitFlags |= UnitFlags.PlayerControlled | UnitFlags.Pvp;

        kit.Cast(owner, PetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(1, pet.Level);
        Assert.Equal((80u, 80u), (pet.MaxHealth, pet.Health));
        Assert.Equal(80u, pet.GetUInt32(UpdateFields.UnitFieldBaseHealth));
        Assert.Equal(PowerType.Mana, pet.PowerType);
        Assert.Equal((40u, 40u, 40u), (pet.GetUInt32(UpdateFields.UnitFieldBaseMana), pet.GetUInt32(UpdateFields.UnitFieldMaxpower1), pet.GetUInt32(UpdateFields.UnitFieldPower1)));
        Assert.Equal(30u, pet.GetUInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal((3f, 5f), (pet.GetFloat(UpdateFields.UnitFieldMindamage), pet.GetFloat(UpdateFields.UnitFieldMaxdamage)));
        Assert.Equal([11u, 12u, 13u, 14u, 15u], Enumerable.Range(0, 5).Select(i => pet.GetUInt32(UpdateFields.UnitFieldStat0 + i)));

        // SUMMON_PET: the template's UNIT_FIELD_FLAGS are dropped, the owner's player-controlled and PvP flags copied
        Assert.Equal(UnitFlags.PlayerControlled | UnitFlags.Pvp, pet.UnitFlags);
    }

    [Fact]
    public void LevelLookup_UsesTheLevelBelowWhenTheCasterIsBetweenRows_AndKeepsTemplateDamageWhenTheRowHasNone()
    {
        using var kit = new PetTestKit(petContent: Content());
        (Player owner, _) = kit.AddPlayer(1);
        owner.Level = 25;

        kit.Cast(owner, PetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(25, pet.Level);
        Assert.Equal(400u, pet.MaxHealth);                                           // level 20 data: the level below 25 with data
        Assert.Equal(200u, pet.GetUInt32(UpdateFields.UnitFieldResistances));

        // dmg_min/dmg_max 0: the creature's own damage stays (vmangos falls back to the class-level stats this build lacks)
        Assert.Equal(0f, Content().FindLevelStats(PetEntry, 20)!.MinDamage);
        Assert.Equal(pet.Template.MinMeleeDamage, pet.GetFloat(UpdateFields.UnitFieldMindamage));
    }

    [Fact]
    public void ACreatureWithoutPetData_KeepsItsTemplateStats()
    {
        using var kit = new PetTestKit(petContent: new PetContent([new PetLevelStats(9999, 1, 10, 0, 0, 0, 0, 1, 1, 1, 1, 1)], []));
        (Player owner, _) = kit.AddPlayer(1);

        kit.Cast(owner, PetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(100u, pet.MaxHealth); // the template's
        Assert.Equal(1, pet.Level);        // but the level is the caster's
        Assert.Empty(pet.Summon!.Charm!.SpellStates);
    }

    [Fact]
    public void CreateSpells_AreLearned_LearnSpellsStandForWhatTheyTeach_AndPassivesAreCastOnThePet()
    {
        using var kit = new PetTestKit(petContent: Content());
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        session.Clear();

        kit.Cast(owner, PetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        CharmInfo charm = pet.Summon!.Charm!;

        // SPELL_EFFECT_LEARN_PET_SPELL stands for the spell it triggers; the unknown spell 999999 is skipped
        Assert.Equal(
            [(PetShieldSpell, ActionType.Disabled), (PetBiteSpell, ActionType.Disabled), (PetPassiveSpell, ActionType.Passive)],
            charm.SpellStates.Select(s => (s.Key, s.Value)));

        // autocast starts off (ACT_DECIDE), a passive spell is not on the bar but is cast on the pet at once
        Assert.Equal((PetShieldSpell, (byte)ActionType.Disabled), (charm.GetButton(3).Action, charm.GetButton(3).Type));
        Assert.Equal((PetBiteSpell, (byte)ActionType.Disabled), (charm.GetButton(4).Action, charm.GetButton(4).Type));
        Assert.Equal(0u, charm.GetButton(5).Action);
        Assert.True(kit.Spells.System.HasAura(pet, PetPassiveSpell));

        // the owner's bar lists all three with their states
        var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
        r.Skip(8 + 4 + 4 + 40);
        Assert.Equal(3, r.ReadByte());
        Assert.Equal(
            [ActionButton.Make(PetShieldSpell, ActionType.Disabled).Packed, ActionButton.Make(PetBiteSpell, ActionType.Disabled).Packed, ActionButton.Make(PetPassiveSpell, ActionType.Passive).Packed],
            [r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()]);
    }

    [Fact]
    public void PetsWithoutCreateSpells_LearnNothing_AndAGuardianKeepsItsTemplate()
    {
        using var kit = new PetTestKit(petContent: Content());
        (Player owner, _) = kit.AddPlayer(1);

        kit.Cast(owner, GuardianSpell);

        Creature guardian = Assert.Single(kit.Creatures.Creatures);
        Assert.Empty(guardian.Summon!.Charm!.SpellStates);
        Assert.Equal(100u, guardian.MaxHealth);
    }

    [Fact]
    public void PassiveSpells_HaveNoAutocast()
    {
        var charm = new CharmInfo(ReactState.Defensive);
        charm.LearnSpell(1, ActionType.Passive);
        charm.LearnSpell(2, ActionType.Disabled);

        charm.SetSpellAutocast(1, true);
        charm.SetSpellAutocast(2, true);

        Assert.Equal(ActionType.Passive, charm.SpellStates[1]);
        Assert.Equal(ActionType.Enabled, charm.SpellStates[2]);
        Assert.Equal(2u, charm.GetButton(CharmInfo.SpellSlotStart).Action); // the passive spell is not on the bar
        Assert.Equal((byte)ActionType.Enabled, charm.GetButton(CharmInfo.SpellSlotStart).Type);
    }
}
