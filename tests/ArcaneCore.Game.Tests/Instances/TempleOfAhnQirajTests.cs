using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class TempleOfAhnQirajTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public TempleOfAhnQirajInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Spells { get; } = new();
        public Player Tank { get; }
        public Creature Boss { get; }
        public SkeramAI Ai => Assert.IsType<SkeramAI>(Boss.AI);

        public Arena()
        {
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map);
            Map.AddUpdater(Raid);
            var content = new CreatureContent([Template(15263, b => b.MinLevelHealth = b.MaxLevelHealth = 10000)], [], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Spells, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 1, 0, new FakeSession(), 531);
            Tank.Relocate(1, 0, 450, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(content.FindTemplate(15263)!, 0, 0, 450, 0);
            Assert.True(Boss.AI!.AttackStart(Tank));
            Spells.Casts.Clear();
        }
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void Skeram_PullSplitAndDeath_UseThreeDistinctBlinkPlatformsAndCleanImages()
    {
        using var a = new Arena();
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(TempleOfAhnQirajInstance.Skeram));
        a.Boss.Health = 7400;
        a.Ai.OnUpdate(1);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 747);
        Assert.Equal(50u, a.Ai.NextSplitPercent);
        Creature first = a.Creatures.SummonCorpseDespawn(a.Boss, 15263, 0, 1, 450, 0)!;
        Creature second = a.Creatures.SummonCorpseDespawn(a.Boss, 15263, 0, 2, 450, 0)!;
        Assert.True(Assert.IsType<SkeramAI>(first.AI).IsImage);
        Assert.Equal(1000u, first.MaxHealth);
        Assert.Equal(740u, first.Health);
        Assert.Equal(3, a.Spells.Casts.Count(c => c.Spell is 4801 or 8195 or 20449));
        a.Map.Combat.Kill(a.Tank, a.Boss);
        Assert.Equal(EncounterState.Done, a.Raid.GetData(TempleOfAhnQirajInstance.Skeram));
        Assert.DoesNotContain(first, a.Creatures.Creatures);
        Assert.DoesNotContain(second, a.Creatures.Creatures);
    }

    [Fact]
    public void Skeram_TimedExplosionAndFulfillment_ResetOnWipe()
    {
        using var a = new Arena();
        a.Ai.OnUpdate(8000);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 26192);
        a.Ai.OnUpdate(7000);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 785 && ReferenceEquals(c.Target, a.Tank));
        a.Ai.OnEvade();
        a.Ai.OnReachedHome();
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(TempleOfAhnQirajInstance.Skeram));
        Assert.Contains(a.Spells.RemovedAuras, entry => ReferenceEquals(entry.Unit, a.Tank) && entry.Spell == 785);
        Assert.Equal(75u, a.Ai.NextSplitPercent);
    }

    [Fact]
    public void Skeram_LaterSplitsReplaceImagesAndScaleTheirHealth()
    {
        using var a = new Arena();
        a.Boss.Health = 7400; a.Ai.OnUpdate(1);
        Creature first = a.Creatures.SummonCorpseDespawn(a.Boss, 15263, 0, 1, 450, 0)!;
        a.Boss.Health = 4900; a.Ai.OnUpdate(1);
        Assert.DoesNotContain(first, a.Creatures.Creatures);
        Creature second = a.Creatures.SummonCorpseDespawn(a.Boss, 15263, 0, 1, 450, 0)!;
        Assert.Equal(2000u, second.MaxHealth);
        a.Boss.Health = 2400; a.Ai.OnUpdate(1);
        Assert.DoesNotContain(second, a.Creatures.Creatures);
        Creature third = a.Creatures.SummonCorpseDespawn(a.Boss, 15263, 0, 1, 450, 0)!;
        Assert.Equal(5000u, third.MaxHealth);
        Assert.Equal(0u, a.Ai.NextSplitPercent);
        a.Ai.OnEvade(); a.Ai.OnReachedHome();
        Assert.DoesNotContain(third, a.Creatures.Creatures);
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(TempleOfAhnQirajInstance.Skeram));
    }

    [Fact]
    public void EncounterState_SavesOnlyCompletedSlotsAndClearsInProgressOnLoad()
    {
        using var a = new Arena();
        a.Raid.SetData(TempleOfAhnQirajInstance.Huhuran, EncounterState.Done);
        var copy = new TempleOfAhnQirajInstance(a.Map);
        copy.Load(a.Raid.GetSaveData()!);
        Assert.Equal(EncounterState.NotStarted, copy.GetData(TempleOfAhnQirajInstance.Skeram));
        Assert.True(copy.CheckConditionCriteriaMeet(a.Tank, TempleOfAhnQirajInstance.Huhuran));
        Assert.False(copy.CheckConditionCriteriaMeet(a.Tank, TempleOfAhnQirajInstance.CThun));
    }

    [Fact]
    public void SkeramAndHuhuranDeathsOpenDoors_TwinsCloseEntranceOnPullAndReopenItOnWipe()
    {
        using var a = new Arena();
        var content = new GameObjectContent(
            [GameObjectTestKit.GoTemplate(180636, GameObjectType.Door),
                GameObjectTestKit.GoTemplate(180634, GameObjectType.Door),
                GameObjectTestKit.GoTemplate(180635, GameObjectType.Door)], [], [], [], []);
        var objects = new GameObjectMapSystem(a.Map, content); a.Map.AddUpdater(objects);
        GameObject skeram = objects.Summon(180636, 1, 0, 450, 0)!;
        GameObject entrance = objects.Summon(180634, 1, 0, 450, 0)!;
        GameObject exit = objects.Summon(180635, 1, 0, 450, 0)!;
        a.Raid.SetData(TempleOfAhnQirajInstance.Skeram, EncounterState.Done);
        a.Raid.SetData(TempleOfAhnQirajInstance.Huhuran, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, skeram.State);
        Assert.Equal(GameObjectState.Active, entrance.State);
        a.Raid.SetData(TempleOfAhnQirajInstance.Twins, EncounterState.InProgress);
        Assert.Equal(GameObjectState.Ready, entrance.State);
        a.Raid.SetData(TempleOfAhnQirajInstance.Twins, EncounterState.Fail);
        Assert.Equal(GameObjectState.Active, entrance.State);
        a.Raid.SetData(TempleOfAhnQirajInstance.Twins, EncounterState.InProgress);
        a.Raid.SetData(TempleOfAhnQirajInstance.Twins, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, entrance.State);
        Assert.Equal(GameObjectState.Active, exit.State);
    }
}
