using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class BlackfathomDeepsScriptTests
{
    [Fact]
    public void PortalDoor_RequiresBothShrineAndKelris_InEitherCompletionOrder()
    {
        // vmangos instance_blackfathom_deeps::SetData/OnObjectCreate: neither event alone opens the portal.
        using var run = new DungeonScriptTestKit(map => new BlackfathomDeepsInstance(map),
            [], [], [(BlackfathomDeepsInstance.GoPortalDoor, GameObjectType.Door)]);
        var script = (BlackfathomDeepsInstance)run.Script;
        var door = run.Object(BlackfathomDeepsInstance.GoPortalDoor);

        script.SetData(BlackfathomDeepsInstance.TypeShrine, EncounterState.Done);
        Assert.Equal(GameObjectState.Ready, door.State);
        script.SetData(BlackfathomDeepsInstance.TypeKelris, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, door.State);

        using var second = new DungeonScriptTestKit(map => new BlackfathomDeepsInstance(map),
            [], [], [(BlackfathomDeepsInstance.GoPortalDoor, GameObjectType.Door)]);
        second.Script.SetData(BlackfathomDeepsInstance.TypeKelris, EncounterState.Done);
        Assert.Equal(GameObjectState.Ready, second.Object(BlackfathomDeepsInstance.GoPortalDoor).State);
        second.Script.SetData(BlackfathomDeepsInstance.TypeShrine, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, second.Object(BlackfathomDeepsInstance.GoPortalDoor).State);
    }

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
        var spells = new RecordingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new BlackfathomDeepsInstance(map),
            [4832, 4825, 4978, 4823, 4977], [4832],
            [(21117, GameObjectType.Door), (21118, GameObjectType.Goober), (21119, GameObjectType.Goober),
                (21120, GameObjectType.Goober), (21121, GameObjectType.Goober)],
            aiServices: new ArcaneCore.Game.Creatures.CreatureAiServices { Spells = spells });
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
        Assert.Equal(19, spells.Casts.Count(s => s == 7741)); // vmangos DoSpawnMobs visual on each summon
        foreach (var summoned in run.Creatures.Creatures.Where(c => c.Entry is 4825 or 4978 or 4823 or 4977).ToArray())
        {
            run.Map.Combat.Kill(run.Player, summoned);
        }

        // vmangos instance_blackfathom_deeps::Update checks completion on its event timer,
        // which has 2 s left after the fourth fire's 3 s spawn delay.
        Assert.Equal(EncounterState.InProgress, script.GetData(BlackfathomDeepsInstance.TypeShrine));
        run.Tick(1_999);
        Assert.Equal(EncounterState.InProgress, script.GetData(BlackfathomDeepsInstance.TypeShrine));
        run.Tick(1);
        Assert.Equal(EncounterState.Done, script.GetData(BlackfathomDeepsInstance.TypeShrine));
        Assert.Equal(GameObjectState.Active, run.Object(21117).State);
    }
}
