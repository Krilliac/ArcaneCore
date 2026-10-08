using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.ScarletMonastery;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class ScarletMonasteryScriptTests
{
    [Fact]
    public void CathedralEvent_OpensItsDoor_AndMakesMograineFriendly()
    {
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map),
            [3976, 3977, 3975, 6487], [3976, 3977, 3975, 6487],
            (ScarletMonasteryInstance.WhitemaneDoor, GameObjectType.Door),
            (ScarletMonasteryInstance.ChapelDoor, GameObjectType.Door));
        var data = Assert.IsType<ScarletMonasteryInstance>(run.Data);
        Assert.IsType<MograineAi>(run.Creature(3976).AI);
        Assert.IsType<WhitemaneAi>(run.Creature(3977).AI);
        Assert.IsType<HerodAi>(run.Creature(3975).AI);
        Assert.IsType<DoanAi>(run.Creature(6487).AI);

        Assert.True(data.EnterCathedral());
        Assert.False(data.EnterCathedral());
        Assert.Equal(GameObjectState.Active, run.Object(ScarletMonasteryInstance.ChapelDoor).State);
        Assert.Equal(35u, run.Creature(3976).FactionTemplate);
        run.Kill(3976);
        run.Creatures.ForceRespawn(run.Creature(3976));
        Assert.Equal(35u, run.Creature(3976).FactionTemplate);
    }

    [Fact]
    public void Mograine_LethalDamage_TriggersFakeDeathAndCallsWhitemane()
    {
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map),
            [3976, 3977], [3976, 3977], (ScarletMonasteryInstance.WhitemaneDoor, GameObjectType.Door));
        var data = Assert.IsType<ScarletMonasteryInstance>(run.Data);
        var mograine = run.Creature(3976);
        run.Map.Combat.DealDamage(run.Player, mograine, mograine.Health, direct: false);
        Assert.True(Assert.IsType<MograineAi>(mograine.AI).IsFeigningDeath);
        Assert.True(mograine.IsAlive);
        Assert.Equal(1u, mograine.Health);
        Assert.Equal(EncounterState.InProgress, data.GetData(ScarletMonasteryInstance.TypeMograineAndWhitemane));
        Assert.Equal(GameObjectState.Active, run.Object(ScarletMonasteryInstance.WhitemaneDoor).State);
    }

    [Fact]
    public void AshbringerHit_StartsClassicDbRelayBeforeMarkingEventDone()
    {
        var relay = new RelayScriptCatalog(
            [new RelayScriptStep(9001, 1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)], []);
        using DungeonScriptHarness run = new(map => new ScarletMonasteryInstance(map), [3976], [3976], relay);
        var data = Assert.IsType<ScarletMonasteryInstance>(run.Data);
        Assert.True(data.EnterCathedral());
        data.CompleteAshbringer(run.Player);
        Assert.Equal(EncounterState.Done, data.GetData(ScarletMonasteryInstance.TypeAshbringer));
        Assert.Equal(1, run.Creatures.PendingRelaySteps);
    }
}
