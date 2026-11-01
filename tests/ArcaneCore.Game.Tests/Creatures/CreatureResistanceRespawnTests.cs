using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Creature resistances through a real resistance aura and the real death path (SpellSystem.OnUnitDied strips the aura through its remove handler),
/// then the respawn re-initialisation; and which creatures keep their template resistances (a warlock demon does, a hunter's permanent pet does not,
/// vmangos Pet.cpp:1345-1357).
/// </summary>
public sealed class CreatureResistanceRespawnTests
{
    private const uint Curse = 900_401;
    private const uint Ward = 900_402;
    private const uint SummonResistantImp = 900_403;
    private const uint ResistantEntry = 990_401;
    private const uint ResistantImpEntry = 5_263;
    private const int FireMask = 1 << (int)SpellSchool.Fire;
    private static readonly int s_fire = UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire;

    private static CreatureTemplate Resistant(uint entry) => new()
    {
        Entry = entry, Name = "Resistant", MinLevel = 20, MaxLevel = 20, MinLevelHealth = 1_000, MaxLevelHealth = 1_000, DisplayIds = [1], Faction = 35,
        HolyRes = 40, FireRes = 100, NatureRes = 200, FrostRes = 300, ShadowRes = -20, ArcaneRes = 50,
    };

    private static Creature Spawn(CreatureTemplate template)
        => new(template.Entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));

    private static SpellTestKit AuraKit() => new(
        RuleTestKit.Grant(Curse, AuraType.ModResistance, -75, FireMask),
        RuleTestKit.Grant(Ward, AuraType.ModResistance, 50, FireMask));

    /// <summary>A killed creature: no health, so <c>IsAlive</c> is false and the spell system strips its auras.</summary>
    private static void Kill(SpellTestKit kit, Creature creature)
    {
        creature.Health = 0;
        kit.System.OnUnitDied(creature);
    }

    [Theory]
    [InlineData(Curse, 100 - 75)]
    [InlineData(Ward, 100 + 50)]
    public void ARealResistanceAura_IsTakenBackByDeath_AndTheRespawnRestoresTheTemplateValue(uint aura, int withAura)
    {
        using SpellTestKit kit = AuraKit();
        Creature mob = Spawn(Resistant(ResistantEntry));
        Assert.Equal(100, mob.GetInt32(s_fire));

        RuleTestKit.Apply(kit, mob, aura);
        Assert.Equal(withAura, mob.GetInt32(s_fire));
        Assert.Single(kit.System.GetAuras(mob));

        Kill(kit, mob);
        Assert.Empty(kit.System.GetAuras(mob));
        Assert.Equal(100, mob.GetInt32(s_fire)); // the remove handler took exactly its own amount back

        mob.InitializeFields(); // the respawn
        Assert.Equal(100, mob.GetInt32(s_fire));
        Assert.Equal(100, mob.CreateResistance((int)SpellSchool.Fire));
        Assert.Equal(300, mob.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Frost)); // the untouched schools are not drifting either
    }

    [Fact]
    public void ARespawnWithTheAuraStillOnTheField_DoesNotStackTheTemplateValueTwice()
    {
        using SpellTestKit kit = AuraKit();
        Creature mob = Spawn(Resistant(ResistantEntry));
        RuleTestKit.Apply(kit, mob, Curse);

        mob.InitializeFields(); // a live re-initialisation (UpdateEntry): the aura delta stays, the template part is not added again

        Assert.Equal(25, mob.GetInt32(s_fire));
        Kill(kit, mob);
        Assert.Equal(100, mob.GetInt32(s_fire));
    }

    [Fact]
    public void HolyStaysZeroInTheField_ThroughTheAuraAndTheDeathPath()
    {
        using SpellTestKit kit = new(RuleTestKit.Grant(Curse, AuraType.ModResistance, -75, 1 << (int)SpellSchool.Holy));
        Creature mob = Spawn(Resistant(ResistantEntry));
        int holy = UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy;

        RuleTestKit.Apply(kit, mob, Curse);
        Kill(kit, mob);
        mob.InitializeFields();

        Assert.Equal(0, mob.GetInt32(holy));
    }

    [Fact]
    public void ACreatureMarkedAsAHunterPetKind_HasNoTemplateResistance_EvenWithANonHunterOwner()
    {
        Creature pet = Spawn(Resistant(ResistantEntry));
        Assert.Equal(100, pet.GetInt32(s_fire)); // control: it starts with them

        pet.MarkHunterPetKind();

        for (int school = 1; school <= 6; school++)
        {
            Assert.Equal(0, pet.GetInt32(UpdateFields.UnitFieldResistances + school));
            Assert.Equal(0, pet.CreateResistance(school));
        }

        pet.InitializeFields(); // a respawn keeps it bare
        Assert.Equal(0, pet.GetInt32(s_fire));
    }

    [Fact]
    public void AWarlockDemon_KeepsItsTemplateResistances()
    {
        using PetTestKit kit = new(
            [Spell(SummonResistantImp, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)ResistantImpEntry))
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0 }],
            extraTemplates: [Resistant(ResistantImpEntry) with { MinLevel = 5, MaxLevel = 5, MinLevelHealth = 100, MaxLevelHealth = 100, Faction = 14 }]);
        kit.Service.InstallDemons(kit.Spells.System);
        (Player warlock, _) = kit.AddPlayer(1);
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        warlock.Level = 30;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(warlock, SummonResistantImp));

        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.Guid == warlock.PetGuid);
        Assert.Equal(100, demon.GetInt32(s_fire));
        Assert.Equal(100, demon.CreateResistance((int)SpellSchool.Fire));
        Assert.Equal(0, demon.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy));
    }
}
