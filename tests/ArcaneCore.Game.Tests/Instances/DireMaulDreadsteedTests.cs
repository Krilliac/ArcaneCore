using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.DireMaul;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>The Dire Maul Dreadsteed ritual (quest 7631): DireMaulDreadsteed.cs and DreadsteedRitualAI.</summary>
public sealed class DireMaulDreadsteedTests
{
    private const float Y = -383.07f;
    private const uint Imp = 14482;

    private sealed record Run(InstanceFixture Fixture, Player Player) : IDisposable
    {
        public Map Map => Player.Map!;
        public DireMaulInstance Data => Assert.IsType<DireMaulInstance>(InstanceManager.InstanceDataOf(Map));
        public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;
        public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;
        public Creature? Living(uint entry) => Creatures.Creatures.SingleOrDefault(c => c.Entry == entry && c.IsAlive);
        public GameObject Object(uint entry) => Objects.GameObjects.First(go => go.Entry == entry);
        public void Dispose() => Fixture.Dispose();
    }

    private static GameObjectSpawn Row(uint guid, uint entry, float x, float z = -29.5358f)
        => GameObjectTestKit.GoSpawn(guid, entry, x, Y, spawnTimeSeconds: -900) with { MapId = InstanceFixture.Dungeon, Z = z };

    private static Run Enter()
    {
        var fixture = new InstanceFixture();
        fixture.Manager.Scripts = new InstanceScriptRegistry().Register(InstanceFixture.Dungeon, map => new DireMaulInstance(map));
        var creatures = new CreatureContent(
            [Template(DireMaulInstance.NpcJeevee), Template(DireMaulInstance.NpcWarlockDummyInfernal), Template(Imp)], [], [], [], []);
        var objects = new GameObjectContent(
            [GameObjectTestKit.GoTemplate(DireMaulInstance.GoBellOfDethmoora, GameObjectType.Button),
             GameObjectTestKit.GoTemplate(DireMaulInstance.GoWheelOfBlackMarch, GameObjectType.Button),
             GameObjectTestKit.GoTemplate(DireMaulInstance.GoDoomsdayCandle, GameObjectType.Button),
             GameObjectTestKit.GoTemplate(DireMaulInstance.GoWarlockRitualCircle, GameObjectType.Generic),
             GameObjectTestKit.GoTemplate(DireMaulInstance.GoDreadsteedPortal, GameObjectType.Generic),
             GameObjectTestKit.GoTemplate(DireMaulInstance.GoMountQuestSymbol1, GameObjectType.Generic),
             GameObjectTestKit.GoTemplate(DireMaulInstance.GoRitualCandleAura, GameObjectType.Trap)],
            [Row(4290344, DireMaulInstance.GoBellOfDethmoora, -20f), Row(4290342, DireMaulInstance.GoWheelOfBlackMarch, -19f),
             Row(4290343, DireMaulInstance.GoDoomsdayCandle, -18f), Row(4290332, DireMaulInstance.GoWarlockRitualCircle, -17f),
             Row(4290345, DireMaulInstance.GoDreadsteedPortal, -16f), Row(4290346, DireMaulInstance.GoDreadsteedPortal, -15f, z: -28.6f),
             Row(4290333, DireMaulInstance.GoMountQuestSymbol1, -14f),
             Row(4290366, DireMaulInstance.GoRitualCandleAura, -18f) with { SpawnTimeSeconds = 900 }], [], [], []);
        fixture.World.MapCreated += map =>
        {
            if (map.MapId == InstanceFixture.Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, creatures, random: new Random(1)));
                map.AddUpdater(new GameObjectMapSystem(map, objects));
            }
        };

        Player player = fixture.AddPlayer(1);
        Assert.True(fixture.EnterDungeon(player));
        fixture.Tick();
        return new Run(fixture, player);
    }

    [Fact]
    public void SummonEvents_OpenOnlyForAPlayerInTheirStep()
    {
        using Run run = Enter();
        // The Dire Maul instance decides, so the gate of the unported handlers is open here.
        Assert.False(ScriptedEvents.GatedOff(run.Map, ScriptedEvents.SummonJeevee, run.Player, null));
        Assert.True(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonDreadsteed, null)); // not before the ritual

        Assert.False(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonJeevee, null)); // the DB script runs
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(DireMaulInstance.TypeDreadsteed));
        Creature jeevee = Assert.IsType<Creature>(run.Living(DireMaulInstance.NpcJeevee));
        Creature ritual = Assert.IsType<Creature>(run.Living(DireMaulInstance.NpcWarlockDummyInfernal));
        Assert.IsType<DreadsteedRitualAI>(ritual.AI);

        // Once started, neither another J'eevee nor the Dreadsteed; a creature never opens either step.
        Assert.True(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonJeevee, null));
        Assert.True(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonDreadsteed, null));
        Assert.Single(run.Creatures.Creatures, c => c.Entry == DireMaulInstance.NpcJeevee);

        run.Data.SetData(DireMaulInstance.TypeDreadsteed, EncounterState.Special);
        Assert.True(run.Data.OnSpellEvent(jeevee, ScriptedEvents.SummonDreadsteed, null));
        Assert.False(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonDreadsteed, null));
        Assert.Equal(EncounterState.Done, run.Data.GetData(DireMaulInstance.TypeDreadsteed));
        Assert.True(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonJeevee, null));
    }

    [Fact]
    public void RitualFails_OnceJeeveeIsGoneAndAllThreeObjectsAreDeactivated_AndCleansUp()
    {
        using Run run = Enter();
        Assert.False(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonJeevee, null));
        GameObject bell = run.Object(DireMaulInstance.GoBellOfDethmoora);
        GameObject wheel = run.Object(DireMaulInstance.GoWheelOfBlackMarch);
        GameObject candle = run.Object(DireMaulInstance.GoDoomsdayCandle);
        foreach (GameObject go in new[] { bell, wheel, candle })
        {
            run.Creatures.RespawnGameObjectFor(go, 900);
            Assert.True(go.IsSpawned);
            Assert.Equal(GameObjectUseResult.Ok, run.Objects.UseByUnit(run.Player, go));
            Assert.Equal(GameObjectLootState.Activated, go.LootState);
        }

        run.Creatures.ForcedDespawn(run.Living(DireMaulInstance.NpcJeevee)!, 0);
        run.Fixture.Tick();
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(DireMaulInstance.TypeDreadsteed));

        // The controller deactivates one object at 70 s, then one every 30-45 s.
        var ai = Assert.IsType<DreadsteedRitualAI>(run.Living(DireMaulInstance.NpcWarlockDummyInfernal)!.AI);
        ai.OnUpdate(70000);
        Assert.Equal(2, new[] { bell, wheel, candle }.Count(go => go.LootState == GameObjectLootState.Activated));
        ai.OnUpdate(45000);
        ai.OnUpdate(45000);
        Assert.All(new[] { bell, wheel, candle }, go => Assert.NotEqual(GameObjectLootState.Activated, go.LootState));

        run.Fixture.Tick();
        Assert.Equal(EncounterState.Fail, run.Data.GetData(DireMaulInstance.TypeDreadsteed));
        Assert.Null(run.Living(DireMaulInstance.NpcWarlockDummyInfernal));
        Assert.All(new[] { bell, wheel, candle }, go => Assert.False(go.IsSpawned));

        // A failed ritual can be started again.
        Assert.False(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonJeevee, null));
    }

    [Fact]
    public void Controller_RunsTheWaves_GuidesTheDemons_AndEndsInSpecial()
    {
        using Run run = Enter();
        Assert.False(run.Data.OnSpellEvent(run.Player, ScriptedEvents.SummonJeevee, null));
        Creature controller = run.Living(DireMaulInstance.NpcWarlockDummyInfernal)!;
        var ai = Assert.IsType<DreadsteedRitualAI>(controller.AI);

        ai.OnUpdate(48000); // phase 0: the wait ends
        ai.OnUpdate(48000); // phase 1: circle and side portals
        GameObject circle = run.Object(DireMaulInstance.GoWarlockRitualCircle);
        Assert.True(circle.IsSpawned);
        Assert.True(run.Objects.GameObjects.Single(go => go.Entry == DireMaulInstance.GoDreadsteedPortal && go.Z > -29f).IsSpawned);
        Assert.False(run.Objects.GameObjects.Single(go => go.Entry == DireMaulInstance.GoDreadsteedPortal && go.Z < -29f).IsSpawned);

        ai.OnUpdate(1000); // phase 2: wave 1 shows symbol 1
        Assert.Equal(3, ai.Phase);
        Assert.True(run.Object(DireMaulInstance.GoMountQuestSymbol1).IsSpawned);

        Creature demon = run.Creatures.SpawnTemporary(Template(Imp), -60f, Y, -27f, 0);
        ai.OnJustSummoned(demon);
        var halfway = Assert.IsType<PointMovementGenerator>(demon.Motion.Top);
        Assert.Equal(DreadsteedRitualAI.PointHalfway, halfway.Id);
        demon.Motion.Clear(); // arrived
        ai.OnUpdate(1);
        Assert.Equal(DreadsteedRitualAI.PointPedestal, Assert.IsType<PointMovementGenerator>(demon.Motion.Top).Id);
        demon.Motion.Clear();
        ai.OnUpdate(1);
        Assert.Equal(MovementGeneratorType.Random, demon.Motion.CurrentType);

        for (int wave = 2; wave <= 8; wave++) ai.OnUpdate(45000);
        ai.OnUpdate(45000); // phase 10: the last wave
        Assert.Equal(11, ai.Phase);
        ai.OnUpdate(17500); // phase 11: the end
        Assert.Equal(EncounterState.Special, run.Data.GetData(DireMaulInstance.TypeDreadsteed));
        Assert.Equal(GameObjectState.Active, circle.State);
        Assert.False(controller.IsAlive && controller.IsInWorld);
        run.Fixture.Tick(1100);
        run.Fixture.Tick(100);
        Assert.False(demon.IsAlive && demon.IsInWorld);
    }
}
