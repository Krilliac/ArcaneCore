using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>Hunter pet happiness and loyalty ticks (vmangos Pet::LooseHappiness, TickLoyaltyChange, KillLoyaltyBonus, the runaway).</summary>
public sealed class PetHappinessTests
{
    private const uint BeastEntry = 5241;

    private static PetTestKit Kit() => new(extraTemplates:
    [
        CreatureTestKit.Template(BeastEntry, b =>
        {
            b.MinLevel = 20;
            b.MaxLevel = 20;
            b.MinLevelHealth = 100;
            b.MaxLevelHealth = 100;
            b.Faction = 14;
        }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
    ]);

    private static (Player Hunter, FakeSession Session, Creature Pet) Tame(PetTestKit kit)
    {
        (Player hunter, FakeSession session) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, session, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    [Theory]
    [InlineData(1, false, 8750)]   // 70 per tick ×125
    [InlineData(2, false, 4375)]
    [InlineData(6, false, 250)]
    [InlineData(1, true, 13125)]
    public void HappinessLoss_FollowsLooseHappiness(int loyalty, bool combat, int expected)
        => Assert.Equal(expected, SummonService.HappinessLoss(loyalty, combat));

    [Theory]
    [InlineData(0u, SummonService.HappinessState.Unhappy)]
    [InlineData(332_999u, SummonService.HappinessState.Unhappy)] // vmangos Pet.h:130 HAPPINESS_LEVEL_SIZE 333000, Pet.cpp:870-875
    [InlineData(333_000u, SummonService.HappinessState.Content)]
    [InlineData(350_000u, SummonService.HappinessState.Content)]
    [InlineData(665_999u, SummonService.HappinessState.Content)]
    [InlineData(666_000u, SummonService.HappinessState.Happy)]
    [InlineData(700_000u, SummonService.HappinessState.Happy)]
    public void StateOf_SplitsTheBarAtOneAndTwoLevels(uint happiness, SummonService.HappinessState expected)
        => Assert.Equal(expected, SummonService.StateOf(happiness));

    [Theory]
    [InlineData(20u, 1, 13)]
    [InlineData(60u, 6, 4)]
    [InlineData(101u, 1, 0)]
    public void KillLoyaltyBonus_FavoursLowLevelsAndLowLoyalty(uint level, int loyalty, int expected)
        => Assert.Equal(expected, SummonService.KillLoyaltyBonus(level, loyalty));

    [Fact]
    public void PetUpdate_LosesHappinessEvery7500ms_AndTicksLoyaltyEvery12s()
    {
        using PetTestKit kit = Kit();
        (_, _, Creature pet) = Tame(kit);
        PetLoyalty.Restore(pet, 2, 5000, 0);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 900_000);

        kit.Run(7_500);
        Assert.Equal(900_000u - 4_375u, pet.GetUInt32(UpdateFields.UnitFieldPower5));

        kit.Run(4_500); // 12 s: a happy pet gains 20 loyalty points
        Assert.Equal(5020, pet.Summon!.Charm!.LoyaltyPoints);
    }

    [Fact]
    public void UnhappyRebelliousPet_RunsAway_WhenItsLoyaltyRunsOut()
    {
        using PetTestKit kit = Kit();
        (Player hunter, FakeSession session, Creature pet) = Tame(kit);
        PetLoyalty.Restore(pet, PetLoyalty.Rebellious, 10, 0);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 0);
        session.Clear();

        kit.Run(12_000);

        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetBroken);
        Assert.True(hunter.PetGuid.IsEmpty);
    }

    [Fact]
    public void GmLoyalty_AddsPoints_AndARebelliousPetPushedBelowZeroRunsAway()
    {
        using PetTestKit kit = Kit();
        (Player hunter, FakeSession session, Creature pet) = Tame(kit);
        Assert.True(SummonService.IsPet(pet));
        PetLoyalty.Restore(pet, 2, 5000, 0);

        Assert.True(kit.Service.ModifyLoyaltyByCommand(pet, hunter, 250));
        Assert.Equal(5250, pet.Summon!.Charm!.LoyaltyPoints);

        PetLoyalty.Restore(pet, PetLoyalty.Rebellious, 10, 0);
        session.Clear();
        Assert.False(kit.Service.ModifyLoyaltyByCommand(pet, hunter, -100));
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetBroken);
        Assert.True(hunter.PetGuid.IsEmpty);
    }

    private static (Player Owner, Creature Pet) HunterPetOnCast(PetTestKit kit, uint happiness)
    {
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(owner, PetTestKit.PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, happiness);
        return (owner, pet);
    }

    [Theory]
    [InlineData(700_000u, 367_000u)] // vmangos Pet::SetDeathState(CORPSE) (Pet.cpp:642-645): -HAPPINESS_LEVEL_SIZE
    [InlineData(100_000u, 0u)]       // clamped, never wraps
    public async Task HunterPetDeath_LosesOneHappinessLevel_AndTheSnapshotCarriesIt(uint before, uint after)
    {
        using PetTestKit kit = new();
        (_, Creature pet) = HunterPetOnCast(kit, before);
        PersistentPetSnapshot? saved = null;
        kit.Service.SavePersistence = (snapshot, _) => { saved = snapshot; return Task.CompletedTask; };

        kit.Creatures.KillCreature(pet);
        await kit.Service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(after, pet.GetUInt32(UpdateFields.UnitFieldPower5));
        Assert.NotNull(saved);
        Assert.Equal(after, saved.Happiness);
    }

    [Fact]
    public void HunterPetDeath_OnABattleground_KeepsItsHappiness()
    {
        using PetTestKit kit = new();
        WorldMaps.Of(kit.Spells.World).Load(new MapContent(
            [new MapTemplate(0, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Battleground", "")], [], [], [], []));
        (_, Creature pet) = HunterPetOnCast(kit, 700_000);

        kit.Creatures.KillCreature(pet);

        Assert.Equal(700_000u, pet.GetUInt32(UpdateFields.UnitFieldPower5));
    }

    [Fact]
    public void WarlockDemonDeath_ChangesNoHappiness()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        kit.Cast(owner, PetTestKit.PetSpell);
        Creature demon = Assert.Single(kit.Creatures.Creatures);
        demon.SetUInt32(UpdateFields.UnitFieldPower5, 700_000);

        kit.Creatures.KillCreature(demon);

        Assert.Equal(700_000u, demon.GetUInt32(UpdateFields.UnitFieldPower5));
    }
}