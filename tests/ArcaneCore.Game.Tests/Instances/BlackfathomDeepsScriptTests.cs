using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class BlackfathomDeepsScriptTests
{
    [Fact]
    public void FathomStoneSummonsBaronOnlyOnce_AndHisDeathMarksAquanisDone()
    {
        using var run = new DungeonScriptTestKit(map => new BlackfathomDeepsInstance(map),
            [12876], [], [(177964, GameObjectType.Goober)]);
        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(177964).Guid));
        Assert.Equal(EncounterState.InProgress, run.Script.GetData(BlackfathomDeepsInstance.TypeAquanis));
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 12876);
        run.Objects.Use(run.Player, run.Object(177964).Guid);
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 12876);
        run.Kill(12876);
        Assert.Equal(EncounterState.Done, run.Script.GetData(BlackfathomDeepsInstance.TypeAquanis));
    }

    [Fact]
    public void FiresRequireKelris_ThenSpawnFourWaves_AndOpenThePortalAfterTheirDeaths()
    {
        using var run = new DungeonScriptTestKit(map => new BlackfathomDeepsInstance(map),
            [4832, 4825, 4978, 4823, 4977], [4832],
            [(21117, GameObjectType.Door), (21118, GameObjectType.Goober), (21119, GameObjectType.Goober),
                (21120, GameObjectType.Goober), (21121, GameObjectType.Goober)]);
        BlackfathomDeepsInstance script = (BlackfathomDeepsInstance)run.Script;
        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(21118).Guid));
        Assert.Equal(EncounterState.NotStarted, script.GetData(BlackfathomDeepsInstance.TypeShrine));

        script.SetData(BlackfathomDeepsInstance.TypeKelris, EncounterState.Done);
        foreach (uint entry in new uint[] { 21118, 21119, 21120, 21121 })
        {
            Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(entry).Guid));
            run.Tick(3_000);
        }

        Assert.Equal(3, run.Creatures.Creatures.Count(c => c.Entry == 4825));
        Assert.Equal(2, run.Creatures.Creatures.Count(c => c.Entry == 4978));
        Assert.Equal(4, run.Creatures.Creatures.Count(c => c.Entry == 4823));
        Assert.Equal(10, run.Creatures.Creatures.Count(c => c.Entry == 4977));
        foreach (var summoned in run.Creatures.Creatures.Where(c => c.Entry is 4825 or 4978 or 4823 or 4977).ToArray())
        {
            run.Map.Combat.Kill(run.Player, summoned);
        }

        Assert.Equal(EncounterState.Done, script.GetData(BlackfathomDeepsInstance.TypeShrine));
        Assert.Equal(GameObjectState.Active, run.Object(21117).State);
    }
}
