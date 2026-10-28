using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A tamed beast keeps none of its innate magic resistances (Patch 1.3.0: "creatures were keeping their innate resistances after being tamed";
/// vmangos Pet.cpp:1345-1357 applies the template resistances only when the pet is not a HUNTER_PET).
/// </summary>
public sealed class HunterPetResistanceTests
{
    private const uint ResistantBeast = 5290;
    private const uint CallSpell = 991101;

    private static CreatureTemplate Beast() =>
        CreatureTestKit.Template(ResistantBeast, b =>
        {
            b.Name = "Resistant Beast";
            b.Faction = 14;
            b.MinLevel = 5;
            b.MaxLevel = 5;
            b.MinLevelHealth = 100;
            b.MaxLevelHealth = 100;
        }) with { HolyRes = 40, FireRes = 100, NatureRes = 200, FrostRes = 300, ShadowRes = -20, ArcaneRes = 50 };

    private static void AssertNoResistance(Creature pet)
    {
        for (int school = 1; school <= 6; school++)
        {
            Assert.Equal(0, pet.GetInt32(UpdateFields.UnitFieldResistances + school));
            Assert.Equal(0, pet.CreateResistance(school));
        }
    }

    [Fact]
    public void ATamedBeast_LosesItsInnateResistances_WhileTheWildOneKeepsThem()
    {
        using PetTestKit kit = new(extraTemplates: [Beast()]);
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 10;
        Creature wild = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(ResistantBeast)!, 3, 0, 0, 0);
        Assert.Equal(100, wild.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire)); // control: the wild beast has them

        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(hunter, wild, 1515));

        AssertNoResistance(pet);
    }

    [Fact]
    public async Task ACachedHunterPet_LoadedFromTheSnapshot_HasNoInnateResistances()
    {
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))], extraTemplates: [Beast()]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 801, ResistantBeast, 5, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, CallSpell));

        AssertNoResistance(Assert.Single(kit.Creatures.Creatures, c => c.Entry == ResistantBeast));
    }
}
