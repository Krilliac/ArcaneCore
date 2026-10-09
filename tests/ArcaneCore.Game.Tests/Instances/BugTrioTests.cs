using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class BugTrioTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public TempleOfAhnQirajInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Spells { get; } = new();
        public Player Tank { get; }
        public Creature Kri { get; }
        public Creature Yauj { get; }
        public Creature Vem { get; }
        public Arena()
        {
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map); Map.AddUpdater(Raid);
            uint[] entries = [15511, 15543, 15544, 15621];
            var content = new CreatureContent(entries.Select(e => Template(e, b => b.MinLevelHealth = b.MaxLevelHealth = 10000)),
                [Spawn(1, 15511, -8590, 2138, 0, 531), Spawn(2, 15543, -8591, 2138, 0, 531),
                    Spawn(3, 15544, -8592, 2138, 0, 531)], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Spells, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, -8590, 2138, new FakeSession(), 531);
            Tank.Relocate(-8590, 2138, 0, 0, 0); World.AddPlayer(Tank); World.RunTick(0);
            Kri = Assert.Single(Creatures.Creatures, c => c.Entry == 15511);
            Yauj = Assert.Single(Creatures.Creatures, c => c.Entry == 15543);
            Vem = Assert.Single(Creatures.Creatures, c => c.Entry == 15544);
            Assert.True(Kri.AI!.AttackStart(Tank));
            Spells.Casts.Clear();
        }
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void PullLinksAllThree_FirstTwoDeathsCauseDevour_LastDeathCompletesAndSaves()
    {
        using var a = new Arena();
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(TempleOfAhnQirajInstance.BugTrio));
        Assert.True(a.Yauj.Combat.IsInCombat);
        Assert.True(a.Vem.Combat.IsInCombat);
        a.Map.Combat.Kill(a.Tank, a.Kri);
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(TempleOfAhnQirajInstance.BugTrio));
        Assert.True(Assert.IsType<YaujAI>(a.Yauj.AI).IsEating);
        Assert.True(Assert.IsType<VemAI>(a.Vem.AI).IsEating);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 26590);
        a.Vem.Health = 4000;
        a.Vem.AI!.OnUpdate(4000);
        Assert.Equal(a.Vem.MaxHealth, a.Vem.Health);
        a.Map.Combat.Kill(a.Tank, a.Vem);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 25790);
        a.Map.Combat.Kill(a.Tank, a.Yauj);
        Assert.Equal(EncounterState.Done, a.Raid.GetData(TempleOfAhnQirajInstance.BugTrio));
        Assert.Equal(10, a.Creatures.Creatures.Count(c => c.Entry == 15621));
        var loaded = new TempleOfAhnQirajInstance(a.Map);
        loaded.Load(a.Raid.GetSaveData()!);
        Assert.Equal(EncounterState.Done, loaded.GetData(TempleOfAhnQirajInstance.BugTrio));
    }

    [Fact]
    public void KriWipeRestoresEncounterAndRespawnsTheDeadBug()
    {
        using var a = new Arena();
        a.Map.Combat.Kill(a.Tank, a.Yauj);
        Assert.False(a.Yauj.IsAlive);
        a.Kri.AI!.OnEvade(); a.Kri.AI.OnReachedHome();
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(TempleOfAhnQirajInstance.BugTrio));
        Assert.True(a.Creatures.Creatures.Any(c => c.Entry == 15543 && c.IsAlive),
            string.Join(";", a.Creatures.Creatures.Where(c => c.Entry == 15543)
                .Select(c => $"{c.Guid}:{c.Health}:{c.DeathState}:{c.Combat.DeathState}")));
        a.World.RunTick(4000);
        Assert.Contains(a.Creatures.Creatures, c => c.Entry == 15543 && c.IsAlive);
        Assert.False(Assert.IsType<KriAI>(a.Kri.AI).IsEating);
    }

    [Fact]
    public void VemKnockdown_WaitsForSomeoneInMeleeRange()
    {
        using var a = new Arena();
        a.Tank.Relocate(-8560, 2138, 0, 0, 0);
        a.Vem.AI!.OnUpdate(8000);
        Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 19128);
        a.Tank.Relocate(a.Vem.X, a.Vem.Y, a.Vem.Z, 0, 0);
        a.Vem.AI.OnUpdate(1);
        Assert.Contains(a.Spells.Casts, c => c.Spell == 19128);
    }

    [Theory]
    [InlineData(15511u, 26350u)]
    [InlineData(15543u, 26580u)]
    [InlineData(15544u, 18670u)]
    public void EachBugCastsItsReferenceAbility(uint entry, uint spell)
    {
        using var a = new Arena();
        Creature bug = new[] { a.Kri, a.Yauj, a.Vem }.Single(c => c.Entry == entry);
        bug.AI!.OnUpdate(20000);
        Assert.Contains(a.Spells.Casts, c => c.Spell == spell);
    }
}
