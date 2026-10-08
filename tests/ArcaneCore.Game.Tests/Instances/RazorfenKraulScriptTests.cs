using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.RazorfenKraul;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells.Scripts;
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
}
