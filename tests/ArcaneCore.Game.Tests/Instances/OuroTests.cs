using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class OuroTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public TempleOfAhnQirajInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Casts { get; } = new();
        public Player Tank { get; }
        public Creature Boss { get; }
        public OuroAI Ai => Assert.IsType<OuroAI>(Boss.AI);

        public Arena()
        {
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map);
            Map.AddUpdater(Raid);
            var content = new CreatureContent(
                [Template(15517, b => b.MinLevelHealth = b.MaxLevelHealth = 10_000),
                 Template(15957), Template(15712), Template(15717), Template(15718)],
                [], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Casts, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 1, 0, new FakeSession(), 531);
            Tank.Relocate(1, 0, -64, 0, 0);
            Tank.MaxHealth = Tank.Health = 100_000;
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(content.FindTemplate(15517)!, 0, 0, -64, 0);
            Casts.Casts.Clear();
        }

        public void Pull() => Assert.True(Ai.AttackStart(Tank));
        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void SweepSandblastSubmergeEmergeAndLowHealthBerserkDriveTheFight()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Ai.OnUpdate(20_501);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26103);
        arena.Boss.Combat.Threat.AddThreat(arena.Tank, 100);
        arena.Ai.OnSpellHitTarget(arena.Tank, Spell(26102, Effect(SpellEffectName.SchoolDamage, 10)));
        Assert.Equal(0f, arena.Boss.Combat.Threat.GetThreat(arena.Tank));

        arena.Ai.OnUpdate(90_001);
        Assert.True((arena.Boss.UnitFlags & UnitFlags.NotSelectable) != 0);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26063);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26058);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26284);
        arena.Creatures.SummonAt(arena.Boss, 15717, 25, 0, -64, 0, null, 40_000);
        arena.Ai.OnUpdate(30_001);
        Assert.InRange(arena.Boss.X, 24f, 26f);
        Assert.True((arena.Boss.UnitFlags & UnitFlags.NotSelectable) == 0);

        arena.Boss.Health = 1999;
        arena.Ai.OnUpdate(1);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26615);
        arena.Ai.OnUpdate(10_001);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26617);
        arena.Map.Combat.Kill(arena.Tank, arena.Boss);
        Assert.Equal(EncounterState.Done, arena.Raid.GetData(TempleOfAhnQirajInstance.Ouro));
    }

    [Fact]
    public void WipeRemovesMoundsAndTheirScarabs()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Creatures.SummonAt(arena.Boss, 15712, 2, 0, -64, 0, null, 60_000);
        arena.Creatures.SummonAt(arena.Boss, 15718, 3, 0, -64, 0, null, 60_000);
        arena.Ai.OnEvade();
        Assert.DoesNotContain(arena.Creatures.Creatures, c => c.Entry is 15712 or 15718);
        arena.Ai.OnReachedHome();
        Assert.Equal(EncounterState.Fail, arena.Raid.GetData(TempleOfAhnQirajInstance.Ouro));
    }

    [Fact]
    public void SpawnerOnlyCallsOuroOnceWhenAPlayerComesWithinTwentyFiveYards()
    {
        using var arena = new Arena();
        Creature spawner = arena.Creatures.SpawnTemporary(arena.Creatures.Content.FindTemplate(15957)!, 0, 0, -64, 0);
        var ai = Assert.IsType<OuroSpawnerAI>(spawner.AI);
        arena.Casts.Casts.Clear();
        ai.MoveInLineOfSight(arena.Tank);
        ai.MoveInLineOfSight(arena.Tank);
        Assert.Single(arena.Casts.Casts, c => c.Spell == 26061);
    }
}
