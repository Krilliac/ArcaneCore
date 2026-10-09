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

public sealed class SarturaTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public TempleOfAhnQirajInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Casts { get; } = new();
        public Player Tank { get; }
        public Player Other { get; }
        public Creature Boss { get; }
        public Creature Guard { get; }
        public SarturaAI BossAi => Assert.IsType<SarturaAI>(Boss.AI);
        public SarturaRoyalGuardAI GuardAi => Assert.IsType<SarturaRoyalGuardAI>(Guard.AI);

        public Arena()
        {
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map);
            Map.AddUpdater(Raid);
            var content = new CreatureContent(
                [Template(15516, b => b.MinLevelHealth = b.MaxLevelHealth = 10000), Template(15984)],
                [Spawn(1, 15516, 0, 1700, 450, 531), Spawn(2, 15984, 0, 1702, 450, 531)], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Casts, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 1, 1700, new FakeSession(), 531);
            Tank.Relocate(1, 1700, 450, 0, 0);
            Other = TestWorld.CreatePlayer(2, 2, 1700, new FakeSession(), 531);
            Other.Relocate(2, 1700, 450, 0, 0);
            World.AddPlayer(Tank);
            World.AddPlayer(Other);
            World.RunTick(0);
            Boss = Assert.Single(Creatures.Creatures, c => c.Entry == 15516);
            Guard = Assert.Single(Creatures.Creatures, c => c.Entry == 15984);
        }

        public void Pull()
        {
            Assert.True(BossAi.AttackStart(Tank));
            Assert.True(GuardAi.AttackStart(Tank));
            Boss.Combat.Threat.AddThreat(Other, 100);
            Guard.Combat.Threat.AddThreat(Other, 100);
            Casts.Casts.Clear();
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void BossAndGuardWhirlwindShiftThreatThenResumeMeleeAndKnockback()
    {
        using var a = new Arena();
        a.Pull();
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(TempleOfAhnQirajInstance.Sartura));

        a.BossAi.OnUpdate(12_000);
        a.GuardAi.OnUpdate(12_000);
        Assert.Contains(a.Casts.Casts, c => c.Spell == 26083);
        Assert.Contains(a.Casts.Casts, c => c.Spell == 26038);
        Assert.True(a.BossAi.IsWhirling);
        Assert.True(a.GuardAi.IsWhirling);
        Assert.False(a.Boss.Combat.IsMeleeAttacking);
        Assert.False(a.Guard.Combat.IsMeleeAttacking);
        Assert.Single(a.Boss.Combat.Threat.Entries, e => e.Threat > 0);
        Assert.Single(a.Guard.Combat.Threat.Entries, e => e.Threat > 0);

        a.BossAi.OnUpdate(15_000);
        a.GuardAi.OnUpdate(8_000);
        Assert.False(a.BossAi.IsWhirling);
        Assert.False(a.GuardAi.IsWhirling);
        Assert.True(a.Boss.Combat.IsMeleeAttacking);
        Assert.True(a.Guard.Combat.IsMeleeAttacking);
        a.Tank.Relocate(0, 1702, 450, 0, 0);
        a.Other.Relocate(0, 1702, 450, 0, 0);
        for (int i = 0; i < 60 && !a.Casts.Casts.Any(c => c.Spell == 19813); i++)
            a.GuardAi.OnUpdate(1000);
        Assert.Contains(a.Casts.Casts, c => c.Spell == 19813);
    }

    [Fact]
    public void SoftAndTimedEnrageWipeAndKillUpdateSarturaEncounterState()
    {
        using var a = new Arena();
        a.Pull();
        a.Boss.Health = 2000;
        a.BossAi.OnUpdate(1);
        Assert.Contains(a.Casts.Casts, c => c.Spell == 26527);
        a.BossAi.OnUpdate(600_001);
        Assert.Contains(a.Casts.Casts, c => c.Spell == 27680);

        a.GuardAi.OnEvade(); // one guard evading resets the encounter
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(TempleOfAhnQirajInstance.Sartura));
        using var finished = new Arena();
        finished.Pull();
        finished.Map.Combat.Kill(finished.Tank, finished.Boss);
        Assert.Equal(EncounterState.Done, finished.Raid.GetData(TempleOfAhnQirajInstance.Sartura));
    }

    [Fact]
    public void SarturasScriptedSightRangeAcceptsAPlayerAtEightyYards()
    {
        using var a = new Arena();
        a.Other.Relocate(0, 1780, 450, 0, 0);
        a.BossAi.MoveInLineOfSight(a.Other);
        Assert.Same(a.Other, a.Boss.Combat.Victim);
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(TempleOfAhnQirajInstance.Sartura));
    }

    [Fact]
    public void SarturaWipeRestoresAFallenRoyalGuard()
    {
        using var a = new Arena();
        a.Pull();
        a.Map.Combat.Kill(a.Tank, a.Guard);
        Assert.False(a.Guard.IsAlive);

        a.BossAi.EnterEvadeMode();

        Assert.Equal(EncounterState.Fail, a.Raid.GetData(TempleOfAhnQirajInstance.Sartura));
        Assert.True(a.Guard.IsAlive);
    }
}
