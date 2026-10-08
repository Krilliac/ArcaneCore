using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.RazorfenDowns;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RazorfenDownsScriptTests
{
    [Fact]
    public void Gong_SpawnsEightFiends_AndCannotStartAnotherWaveUntilTheyDie()
    {
        using DungeonScriptHarness run = new(map => new RazorfenDownsInstance(map),
            [BelnistraszAi.Entry, RazorfenDownsInstance.TombFiend, RazorfenDownsInstance.TombReaver, RazorfenDownsInstance.TutenKash],
            [BelnistraszAi.Entry], (RazorfenDownsInstance.Gong, GameObjectType.Goober));
        var data = Assert.IsType<RazorfenDownsInstance>(run.Data);
        Assert.IsType<BelnistraszAi>(run.Creature(BelnistraszAi.Entry).AI);
        Assert.True(data.SpawnWaveIfCan(run.Object(RazorfenDownsInstance.Gong)));
        Assert.Equal(8, data.WaveMobCount);
        Assert.Equal(1, data.WaveCounter);
        Assert.False(data.SpawnWaveIfCan(run.Object(RazorfenDownsInstance.Gong)));
        Assert.Equal(EncounterState.InProgress, data.GetData(RazorfenDownsInstance.TypeTutenKash));
        foreach (var fiend in run.Creatures.Creatures.Where(c => c.Template.Entry == RazorfenDownsInstance.TombFiend).ToArray())
            run.Map.Combat.Kill(run.Player, fiend);
        Assert.Equal(0, data.WaveMobCount);
        Assert.True(data.SpawnWaveIfCan(run.Object(RazorfenDownsInstance.Gong)));
        Assert.Equal(4, run.Creatures.Creatures.Count(c => c.Template.Entry == RazorfenDownsInstance.TombReaver));
    }
}
