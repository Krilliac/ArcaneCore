using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Effects;

/// <summary>
/// Effects that had no handler: TAMECREATURE (55), FEED_PET (101), INEBRIATE (100), TRIGGER_MISSILE (32), and the EMPATHY aura (vmangos
/// SpellEffects.cpp / SpellAuras.cpp, cited on each handler).
/// </summary>
public sealed class HunterAndMiscEffectTests
{
    private const uint BeastEntry = 5101;
    private const uint HumanoidEntry = 5102;
    private const uint Drink = 920001;
    private const uint Missile = 920002;
    private const uint MissileHit = 920003;
    private const uint BeastLore = 920004;
    private const SpellEffectName ProbeEffect = (SpellEffectName)240;

    private static CreatureTemplate Wild(uint entry, uint type) => CreatureTestSupport.Template(entry, b =>
    {
        b.MinLevel = 5;
        b.MaxLevel = 5;
        b.MinLevelHealth = 100;
        b.MaxLevelHealth = 100;
        b.Faction = 14;
    }) with { CreatureType = type, TypeFlags = SummonService.TypeFlagTameable };

    private static PetTestKit NewKit() => new(extraTemplates: [Wild(BeastEntry, SummonService.CreatureTypeBeast), Wild(HumanoidEntry, 7)]);

    private static Player Hunter(PetTestKit kit, byte level = 10)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        player.Level = level;
        return player;
    }

    [Fact]
    public void Tame_MakesTheBeastTheHuntersCurrentPet_AndDespawnsTheWildOne()
    {
        using PetTestKit kit = NewKit();
        Player hunter = Hunter(kit);
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        Assert.Null(kit.Service.CheckTaming(hunter, beast));

        ObjectGuid wildGuid = beast.Guid;
        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 13481));

        Assert.Equal(pet.Guid, hunter.PetGuid);
        Assert.Equal(BeastEntry, pet.Entry);
        Assert.Equal(5u, pet.Level);
        Assert.Equal(pet.MaxHealth, pet.Health);
        Assert.Equal(SummonService.TamedHappiness, pet.GetUInt32(UpdateFields.UnitFieldPower5));
        Assert.Equal(13481u, pet.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.Null(kit.Creatures.FindCreature(wildGuid)); // a temporary creature goes; a database spawn would die and respawn later
        Assert.Equal(PetTameFailureReason.AnotherSummonActive, kit.Service.CheckTaming(hunter, beast));
    }

    [Fact]
    public void CheckTaming_RefusesNonHunters_HigherLevels_AndUntameableCreatures()
    {
        using PetTestKit kit = NewKit();
        Player hunter = Hunter(kit, level: 4);
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        Creature humanoid = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(HumanoidEntry)!, 0, 3, 0, 0);

        Assert.Equal(PetTameFailureReason.TooHighLevel, kit.Service.CheckTaming(hunter, beast));
        hunter.Level = 10;
        Assert.Equal(PetTameFailureReason.NotTameable, kit.Service.CheckTaming(hunter, humanoid));
        Assert.Equal(PetTameFailureReason.InvalidCreature, kit.Service.CheckTaming(hunter, hunter));
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        Assert.Equal(PetTameFailureReason.UnitsCantTame, kit.Service.CheckTaming(hunter, beast));
    }

    [Theory]
    [InlineData(60u, 55u, 35_000)]
    [InlineData(60u, 50u, 17_000)]
    [InlineData(60u, 46u, 8_000)]
    [InlineData(60u, 45u, 0)]
    public void FoodBenefit_FollowsTheFoodLevelBands(uint petLevel, uint itemLevel, int expected)
        => Assert.Equal(expected, SummonService.FoodBenefit(petLevel, itemLevel));

    [Fact]
    public void Inebriate_AddsTheValueTimes256_KeepsTheGenderBit_AndSobers256Every10Seconds()
    {
        using SpellTestKit kit = new([Spell(Drink, Effect(SpellEffectName.Inebriate, 50)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 }]);
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt16(UpdateFields.PlayerBytes3, 0, 1); // female

        kit.System.CastSpell(player, Drink, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(12800, SpellSystem.GetDrunkValue(player));
        Assert.Equal(2, SpellSystem.DrunkenState(SpellSystem.GetDrunkValue(player)));
        Assert.Equal(1, player.GetUInt16(UpdateFields.PlayerBytes3, 0) & 1);

        kit.Advance(10_100);
        Assert.Equal(12544, SpellSystem.GetDrunkValue(player));
    }

    [Fact]
    public void TriggerMissile_CastsTheTriggerSpellOnceAtTheDestination()
    {
        var hits = new List<(float X, float Y, float Z)>();
        using SpellTestKit kit = new(
        [
            Spell(Missile, Effect(SpellEffectName.TriggerMissile, 0, trigger: MissileHit)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(MissileHit, Effect(ProbeEffect, 1)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        ]);
        kit.System.RegisterEffect(ProbeEffect, context => hits.Add(context.Cast.Targets.Dest));
        (Player player, _) = kit.AddPlayer(1);

        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (12f, 7f, 3f) };
        kit.System.CastSpell(player, Missile, targets, triggered: true);

        Assert.Equal((12f, 7f, 3f), Assert.Single(hits));
    }

    [Fact]
    public void Empathy_MarksTheTargetForSpecialInfo_WhileTheAuraLasts()
    {
        using SpellTestKit kit = new([Spell(BeastLore, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Empathy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }]);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, BeastLore, SpellCastTargets.ForSelf(), triggered: true);
        Assert.NotEqual(0u, player.GetUInt32(UpdateFields.UnitDynamicFlags) & ArcaneCore.Game.Ranged.UnitDynFlags.SpecialInfo);

        kit.System.RemoveAuras(player, BeastLore);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitDynamicFlags) & ArcaneCore.Game.Ranged.UnitDynFlags.SpecialInfo);
    }
}
