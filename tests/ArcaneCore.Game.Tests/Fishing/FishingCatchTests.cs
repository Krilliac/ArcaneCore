using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Fishing;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Fishing;

/// <summary>
/// The click half of fishing (vmangos GameObject::Use for a FISHINGNODE, GameObject.cpp:1635-1731; getFishLoot :878-887;
/// SendLoot Player.cpp:7651-7703; DoLootRelease LootHandler.cpp:458-495). Zone base skills mirror classic-db skill_fishing_base_level
/// (-70, -20, 55, 130, 205, 330) and the shapes of its fishing_loot_template.
/// </summary>
public sealed class FishingCatchTests
{
    // --- pure rules ------------------------------------------------------------------------

    [Theory]
    [InlineData(55, 55, 5)]
    [InlineData(55, 75, 25)]
    [InlineData(55, 100, 50)]
    [InlineData(55, 150, 100)]
    [InlineData(130, 150, 25)]
    [InlineData(-70, 0, 75)]
    [InlineData(-20, 0, 25)]
    public void ChanceIsSkillMinusZoneSkillPlusFive(int zoneSkill, int skill, int percent)
    {
        Assert.Equal(percent, FishingCatch.Chance(skill, zoneSkill));
        int successes = Enumerable.Range(1, 100).Count(roll => FishingCatch.Succeeds(skill, zoneSkill, roll));
        Assert.Equal(Math.Min(percent, 100), successes);
    }

    [Theory]
    [InlineData(130, 129)]
    [InlineData(330, 300)]
    [InlineData(205, 204)]
    public void ASkillBelowTheZoneBaseNeverCatches_WhateverTheRoll(int zoneSkill, int skill)
        => Assert.DoesNotContain(Enumerable.Range(1, 100), roll => FishingCatch.Succeeds(skill, zoneSkill, roll));

    [Fact]
    public void ZoneSkill_UsesTheSubZoneThenTheZone_AndTreatsZeroAsMissing_KeepingNegativeValues()
    {
        var content = new LootContent([], [], [new(10, 55), new(1, 130), new(2, -70), new(20, 0)], []);

        Assert.Equal(55, FishingCatch.ZoneSkill(content, 1, 10));
        Assert.Equal(130, FishingCatch.ZoneSkill(content, 1, 99));   // sub-zone without a row falls back to its zone
        Assert.Equal(-70, FishingCatch.ZoneSkill(content, 2, 98));   // negative bases survive (a "<= 0 means absent" read would break them)
        Assert.Equal(130, FishingCatch.ZoneSkill(content, 1, 20));   // an explicit 0 is indistinguishable from none
        Assert.Equal(0, FishingCatch.ZoneSkill(content, 77, 78));
    }

    [Fact]
    public void LootEntry_IsTheSubZoneTableWhenItExists_ElseTheZoneTable_NeverBecauseARollCameUpEmpty()
    {
        var content = new LootContent(
            [(LootTableKind.Fishing, Row(10, 1, 100)), (LootTableKind.Fishing, Row(1, 2, 100)), (LootTableKind.Fishing, Row(3, 3, 0))], []);

        Assert.Equal(10u, FishingCatch.LootEntry(content, 1, 10));
        Assert.Equal(1u, FishingCatch.LootEntry(content, 1, 99));   // no sub-zone template: the zone's
        Assert.Equal(3u, FishingCatch.LootEntry(content, 3, 3));    // a template whose rows can never drop still counts as existing
        Assert.Null(FishingCatch.LootEntry(content, 4, 5));
        Assert.Null(FishingCatch.LootEntry(content, 4, 4));
    }

    [Theory]
    [InlineData(11u, -4074.74f, -1315.79f, true)]
    [InlineData(11u, -4000f, -1315.79f, true)]    // 74.7 yd away
    [InlineData(11u, -3900f, -1315.79f, false)]   // 174.7 yd away
    [InlineData(12u, -4074.74f, -1315.79f, false)]
    public void HiddenWetlandsLake_IsDecidedByTheSubZoneAndThePlayersPosition(uint area, float x, float y, bool hidden)
        => Assert.Equal(hidden, FishingCatch.IsHiddenLake(area, x, y));

    // --- the click ---------------------------------------------------------------------------

    private static List<byte[]> Packets(FakeSession session, WorldOpcode opcode) => SpellTestKit.Packets(session, opcode);

    private static ParsedLoot Window(FishingRig rig) => ParsedLoot.Parse(Assert.Single(Packets(rig.Session, WorldOpcode.SmsgLootResponse)));

    private static byte[] Packets_(FishingRig rig, WorldOpcode opcode) => Assert.Single(Packets(rig.Session, opcode));

    private static GameObjectUseResult Click(FishingRig rig, Player? by = null)
        => rig.Objects.Use(by ?? rig.Player, rig.Bobber!.Guid);

    /// <summary>Cast, wait for the bite (lastSec 3 of a 20 s channel: 17 s) and clear the packet log.</summary>
    private static void CastAndWaitForTheBite(FishingRig rig)
    {
        rig.Pick.Index = 0;
        rig.Cast();
        rig.Step(17000);
        Assert.Equal(GameObjectLootState.Ready, rig.Bobber!.LootState);
        rig.Session.Clear();
    }

    [Fact]
    public void OnlyTheOwnerMayUseTheBobber()
    {
        using var rig = new FishingRig();
        (Player other, FakeSession otherSession) = rig.Kit.AddPlayer(2, 1, 0);
        CastAndWaitForTheBite(rig);

        Assert.Equal(GameObjectUseResult.NotUsable, Click(rig, other));

        Assert.Equal(GameObjectLootState.Ready, rig.Bobber!.LootState);
        Assert.Empty(Packets(otherSession, WorldOpcode.SmsgLootResponse));
        Assert.Equal(FishingRig.FishingSpell, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell)); // still fishing
    }

    [Fact]
    public void ClickingBeforeTheBite_SendsNotHooked_RemovesTheBobber_AndEndsTheChannel()
    {
        using var rig = new FishingRig();
        rig.Cast();
        rig.Step(1000);
        rig.Session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, Click(rig));

        Assert.Empty(Packets_(rig, WorldOpcode.SmsgFishNotHooked));
        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgLootResponse));
        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
        rig.Step(100);
        Assert.Null(rig.Bobber);
    }

    [Fact]
    public void ASuccessfulCatch_OpensPersonalLootOfTheSubZoneTable_AsWireType3_AndEndsTheChannelAtOnce()
    {
        using var rig = new FishingRig(skill: 150);
        CastAndWaitForTheBite(rig);
        GameObject bobber = rig.Bobber!;

        Assert.Equal(GameObjectUseResult.Ok, Click(rig));

        ParsedLoot window = Window(rig);
        Assert.Equal((bobber.Guid.Value, LootType.Fishing), (window.Guid, window.Type));
        Assert.Equal(FishingRig.SubZoneFish, Assert.Single(window.Items).ItemId);
        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));   // FinishSpell(CURRENT_CHANNELED_SPELL) runs after every branch
        Assert.NotNull(rig.Bobber);                                              // detached from the spell: the window still needs it
        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgFishEscaped));
    }

    [Fact]
    public void ASubZoneWithoutAFishingTemplate_RollsTheZoneTable()
    {
        using var rig = new FishingRig(lootRows: [(LootTableKind.Fishing, Row(FishingRig.Zone, FishingRig.ZoneFish, 100))]);
        CastAndWaitForTheBite(rig);

        Click(rig);

        Assert.Equal(FishingRig.ZoneFish, Assert.Single(Window(rig).Items).ItemId);
    }

    [Fact]
    public void TakingTheCatch_AndReleasingTheWindow_RemovesTheBobber()
    {
        using var rig = new FishingRig();
        CastAndWaitForTheBite(rig);
        Click(rig);
        ObjectGuid bobber = rig.Bobber!.Guid;

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
        Assert.Equal(1u, rig.Player.Inventory.GetItemCount(FishingRig.SubZoneFish));
        rig.Loot.Release(rig.Player, bobber);

        Assert.Equal(GameObjectLootState.JustDeactivated, rig.Objects.Find(bobber)!.LootState);
        Assert.Null(rig.Loot.FindLoot(bobber));
        rig.Step(100);
        Assert.Null(rig.Bobber);
    }

    [Fact]
    public void TheOwnedBobberIsExemptFromTheLootDistance()
    {
        using var rig = new FishingRig();
        CastAndWaitForTheBite(rig);
        Click(rig);
        rig.Player.Relocate(60, 0, rig.Player.Z, 0, 0); // > 5 yd away from the bobber

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
    }

    [Fact]
    public void FishingLoot_IsPersonal_EvenInAGroup_AndNeverMovesTheGroupsLooter()
    {
        using var rig = new FishingRig();
        (Player mate, _) = rig.Kit.AddPlayer(2, 1, 0);
        var groups = new ArcaneCore.Game.Tests.GameObjects.FakeGroups();
        Group group = groups.Create(LootMethod.RoundRobin, rig.Player, mate);
        rig.Loot.Groups = groups;
        ObjectGuid looterBefore = group.LooterGuid;
        CastAndWaitForTheBite(rig);

        Click(rig);

        LootBag bag = rig.Loot.OpenLootOf(rig.Player)!;
        Assert.Equal([rig.Player.Guid], bag.Recipients);
        Assert.True(bag.Owner.IsEmpty);
        Assert.Equal(looterBefore, group.LooterGuid);
    }

    [Fact]
    public void ASkillBelowTheZoneBase_FailsWithFishEscaped_NoLoot_AndNoSkillGain()
    {
        using var rig = new FishingRig(skill: 54); // sub-zone 10 needs 55
        rig.SkillRandom.Ints.Enqueue(1);
        CastAndWaitForTheBite(rig);

        Click(rig);

        Assert.Empty(Packets_(rig, WorldOpcode.SmsgFishEscaped));
        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgLootResponse));
        Assert.Equal((ushort)54, rig.Skills.GetValuePure(SkillIds.Fishing));
        Assert.Null(rig.Bobber); // JUST_DEACTIVATED, and the ending channel deletes the bobber it still owns
        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
    }

    [Fact]
    public void ASuccessRaisesTheFishingSkill_Once()
    {
        using var rig = new FishingRig(skill: 100);
        rig.SkillRandom.Ints.Enqueue(1); // the lowest roll always gains
        CastAndWaitForTheBite(rig);

        Click(rig);

        Assert.Equal((ushort)101, rig.Skills.GetValuePure(SkillIds.Fishing));
    }

    [Fact]
    public void AFailureRaisesTheSkillOnlyWithFailGain()
    {
        using var rig = new FishingRig(skill: 54, options: new FishingOptions { FailGain = true });
        rig.SkillRandom.Ints.Enqueue(1);
        CastAndWaitForTheBite(rig);

        Click(rig);

        Assert.Equal((ushort)55, rig.Skills.GetValuePure(SkillIds.Fishing));
        Assert.Empty(Packets_(rig, WorldOpcode.SmsgFishEscaped));
    }

    [Fact]
    public void FailLoot_GivesTheJunkTableEntryZero_AsFishingWireType()
    {
        using var rig = new FishingRig(skill: 54, options: new FishingOptions { FailLoot = true });
        CastAndWaitForTheBite(rig);

        Click(rig);

        ParsedLoot window = Window(rig);
        Assert.Equal(LootType.Fishing, window.Type);
        Assert.Equal(FishingRig.JunkFish, Assert.Single(window.Items).ItemId);
        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgFishEscaped));
    }

    [Fact]
    public void TheHiddenWetlandsLake_YieldsAnEmptyWindow()
    {
        using var rig = new FishingRig(baseSkills: [new KeyValuePair<uint, int>(FishingCatch.HiddenLakeArea, 55)]);
        rig.Fishing.AreaOf = (_, _, _) => (FishingRig.Zone, FishingCatch.HiddenLakeArea);
        CastAndWaitForTheBite(rig);
        rig.Player.Relocate(FishingCatch.HiddenLakeX + 10, FishingCatch.HiddenLakeY, rig.Player.Z, 0, 0);
        rig.Bobber!.SetPosition(FishingCatch.HiddenLakeX + 25, FishingCatch.HiddenLakeY, rig.Bobber.Z, 0); // keep the bobber in reach

        Assert.Equal(GameObjectUseResult.Ok, Click(rig));

        Assert.Empty(Window(rig).Items);
    }

    // --- holes -----------------------------------------------------------------------------------

    private static GameObjectSpawn HoleAt(float x, float y = 0) => GoSpawn(100, FishingRig.HoleEntry, x, y);

    [Fact]
    public void ASuccessfulCatchNearAHole_LootsTheHole_ForTheFisherOnly_AndTheBobberGoes()
    {
        using var rig = new FishingRig(spawns: [HoleAt(15)]); // 3.5 yd from the bobber, radius 12
        GameObject hole = rig.Objects.GameObjects.Single(g => g.Type == GameObjectType.FishingHole);
        CastAndWaitForTheBite(rig);
        GameObject bobber = rig.Bobber!;

        Click(rig);

        ParsedLoot window = Window(rig);
        Assert.Equal((hole.Guid.Value, LootType.Fishing), (window.Guid, window.Type));   // FISHINGHOLE is sent as FISHING
        Assert.Equal(FishingRig.HoleFish, Assert.Single(window.Items).ItemId);
        Assert.Equal(GameObjectLootState.JustDeactivated, bobber.LootState);
        Assert.Equal(GameObjectLootState.Activated, hole.LootState);
        Assert.Equal([rig.Player.Guid], rig.Loot.OpenLootOf(rig.Player)!.Recipients);
    }

    [Fact]
    public void AHoleLootedOut_CountsAUse_AndDespawnsAtTheDrawnUseCount()
    {
        using var rig = new FishingRig(spawns: [HoleAt(15)]); // data2 = data3 = 2: gone after the second use
        GameObject hole = rig.Objects.GameObjects.Single(g => g.Type == GameObjectType.FishingHole);

        for (int use = 1; use <= 2; use++)
        {
            CastAndWaitForTheBite(rig);
            Click(rig);
            Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(rig.Player, 0));
            rig.Loot.Release(rig.Player, hole.Guid);
            Assert.Equal(use == 1 ? GameObjectLootState.Ready : GameObjectLootState.JustDeactivated, hole.LootState);
            Assert.Equal((uint)use, hole.UseCount);
            rig.Step(100);
        }

        Assert.False(hole.IsSpawned); // despawned; the spawn respawns after its spawntimesecs
    }

    [Fact]
    public void ALeftoverHoleWindow_KeepsTheHoleActivated()
    {
        using var rig = new FishingRig(spawns: [HoleAt(15)]);
        GameObject hole = rig.Objects.GameObjects.Single(g => g.Type == GameObjectType.FishingHole);
        CastAndWaitForTheBite(rig);
        Click(rig);

        rig.Loot.Release(rig.Player, hole.Guid); // nothing taken

        Assert.Equal(GameObjectLootState.Activated, hole.LootState);
        Assert.Equal(0u, hole.UseCount);
    }

    [Theory]
    [InlineData(false, true)]   // retail default (Possible.FishingPool = true): a failed roll stays a failure
    [InlineData(true, false)]   // the option false turns a failed roll near a hole into a catch
    public void AFailedRollNearAHole_IsAFailureByDefault_AndACatchOnlyWithFailPossibleFishingPoolFalse(bool poolOptionFalse, bool failure)
    {
        using var rig = new FishingRig(skill: 54, spawns: [HoleAt(15)], options: new FishingOptions { FailPossibleFishingPool = !poolOptionFalse });
        CastAndWaitForTheBite(rig);

        Click(rig);

        Assert.Equal(failure, Packets(rig.Session, WorldOpcode.SmsgFishEscaped).Count == 1);
        Assert.Equal(!failure, Packets(rig.Session, WorldOpcode.SmsgLootResponse).Count == 1);
    }

    [Theory]
    [InlineData(40f, 0f, false)]   // the hole is 25+ yd from the bobber: outside the 20.5 search
    [InlineData(35f, 0f, false)]   // 20 yd from the bobber but its own radius is 12
    [InlineData(27f, 0f, true)]    // 12 yd away, inside the hole's radius 12 and the 20.5 search
    public void TheHoleSearch_NeedsTheBobberWithinTheSearchRangeAndTheHolesOwnRadius(float holeX, float holeY, bool found)
    {
        using var rig = new FishingRig(spawns: [HoleAt(holeX, holeY)]);
        CastAndWaitForTheBite(rig);

        GameObject? hole = FishingHoles.FindAround(rig.Objects, rig.Bobber!);

        Assert.Equal(found, hole is not null);
    }

    [Fact]
    public void AHoleInUseByAnotherFisher_FallsBackToTheZoneLoot()
    {
        using var rig = new FishingRig(spawns: [HoleAt(15)]);
        GameObject hole = rig.Objects.GameObjects.Single(g => g.Type == GameObjectType.FishingHole);
        hole.LootState = GameObjectLootState.Activated;
        CastAndWaitForTheBite(rig);

        Click(rig);

        ParsedLoot window = Window(rig);
        Assert.NotEqual(hole.Guid.Value, window.Guid);
        Assert.Equal(FishingRig.SubZoneFish, Assert.Single(window.Items).ItemId);
    }
}