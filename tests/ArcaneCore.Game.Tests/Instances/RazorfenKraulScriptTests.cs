using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.RazorfenKraul;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Collision;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RazorfenKraulScriptTests
{
    [Fact]
    public void ExistingWardScript_AlsoSelectsWillixEscortAndSnufflenoseGopherAis()
    {
        using DungeonScriptHarness run = new(map => new RazorfenKraulInstance(map),
            [WillixAi.Entry, SnufflenoseGopherAi.Entry], [WillixAi.Entry, SnufflenoseGopherAi.Entry]);
        Assert.IsType<WillixAi>(run.Creature(WillixAi.Entry).AI);
        Assert.IsType<SnufflenoseGopherAi>(run.Creature(SnufflenoseGopherAi.Entry).AI);
        Assert.IsType<LeftForDeadSpell>(SpellScriptRegistry.Discover(typeof(LeftForDeadSpell).Assembly).Find(8555));
    }

    [Fact]
    public void Gopher_CommandFindsAnUnspawnedTubberAndEnablesItsRespawnOnArrival()
    {
        using DungeonScriptHarness run = new(map => new RazorfenKraulInstance(map),
            [SnufflenoseGopherAi.Entry], [SnufflenoseGopherAi.Entry],
            (SnufflenoseGopherAi.BlueleafTubber, GameObjectType.Chest));
        GameObject tubber = run.Object(SnufflenoseGopherAi.BlueleafTubber);
        tubber.Flags |= GameObjectFlags.InteractCond;
        Assert.True(run.Objects.DespawnForRespawn(tubber));
        run.Tick();
        Assert.False(tubber.IsSpawned);
        Assert.Contains(tubber, run.Objects.GameObjects);
        Assert.True((tubber.Flags & GameObjectFlags.InteractCond) != 0);
        var ai = Assert.IsType<SnufflenoseGopherAi>(run.Creature(SnufflenoseGopherAi.Entry).AI);
        Assert.True(ai.FindNewTubber());
        ai.OnMovementInform(MovementGeneratorType.Point, 1);
        Assert.False(ai.IsMovingToTubber);
        Assert.Equal(GameObjectFlags.None, tubber.Flags & GameObjectFlags.InteractCond);
    }

    [Fact]
    public void Gopher_TubberMustBeInSightOfItsOwner_FromTheTubber()
    {
        // vmangos npc_snufflenose_gopherAI::IsValidTuber (razorfen_kraul.cpp:271-280): tuber->IsWithinLOSInMap(viewPoint), the viewpoint being
        // the gopher's owner when it has one. A game object looks from 1 yd (Object.cpp:1770) to 1 yd above the owner (:1777-1781): a 1.5 yd
        // wall between the owner and the tubber hides it, though the gopher beside the tubber sees it. On the tubber's side of the wall the
        // owner sees it again (the unspawned tubber is out of the map, as in the test above).
        using DungeonScriptHarness run = new(map => new RazorfenKraulInstance(map),
            [SnufflenoseGopherAi.Entry], [SnufflenoseGopherAi.Entry],
            (SnufflenoseGopherAi.BlueleafTubber, GameObjectType.Chest));
        GameObject tubber = run.Object(SnufflenoseGopherAi.BlueleafTubber);
        tubber.Flags |= GameObjectFlags.InteractCond;
        Assert.True(run.Objects.DespawnForRespawn(tubber));
        run.Tick();
        Creature gopher = run.Creature(SnufflenoseGopherAi.Entry);
        run.Player.Relocate(-6.4f, -380f, 61.78f, 0f, 0);
        gopher.SetUInt64(UpdateFields.UnitFieldSummonedby, run.Player.Guid.Value);
        using VMapWall wall = VMapWall.AcrossX(run.Map.World, -10f, -380f, 61.78f, 1.5f, run.Map.MapId);

        var ai = Assert.IsType<SnufflenoseGopherAi>(gopher.AI);
        Assert.False(ai.FindNewTubber());

        run.Player.Relocate(-13.4f, -380f, 61.78f, 0f, 0);
        Assert.True(ai.FindNewTubber());
        Assert.Equal(tubber.Guid, ai.TargetTubber);
    }
}
