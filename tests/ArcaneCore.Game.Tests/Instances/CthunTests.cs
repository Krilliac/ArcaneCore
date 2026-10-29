using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class CthunTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public TempleOfAhnQirajInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Casts { get; } = new();
        public Player Player { get; }
        public Creature Body { get; }
        public Creature Eye { get; }
        public CthunBodyAI BodyAi => Assert.IsType<CthunBodyAI>(Body.AI);
        public CthunEyeAI EyeAi => Assert.IsType<CthunEyeAI>(Eye.AI);

        public Arena()
        {
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map);
            Map.AddUpdater(Raid);
            var content = new CreatureContent(
                [Template(15727, b => b.MinLevelHealth = b.MaxLevelHealth = 10_000), Template(15589),
                 Template(15725), Template(15726), Template(15728), Template(15334), Template(15802),
                 Template(15904), Template(15910), Template(15800), Template(15922)], [], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Casts, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Player = TestWorld.CreatePlayer(1, -8570, 1986, new FakeSession(), 531);
            Player.Relocate(-8570, 1986, 100.4f, 0, 0);
            Player.MaxHealth = Player.Health = 100_000;
            World.AddPlayer(Player);
            World.RunTick(0);
            Eye = Creatures.SpawnTemporary(content.FindTemplate(15589)!, -8578.79f, 1986.18f, 100.3f, 0);
            Body = Creatures.SpawnTemporary(content.FindTemplate(15727)!, -8578.65f, 1985.85f, 100.3f, 0);
            Casts.Casts.Clear();
        }

        public void Pull()
        {
            BodyAi.AttackStart(Player);
            Assert.Equal(EncounterState.InProgress, Raid.GetData(TempleOfAhnQirajInstance.CThun));
        }

        public void EnterBodyPhase()
        {
            Pull();
            Map.Combat.Kill(Player, Eye);
            BodyAi.OnUpdate(4000);
            BodyAi.OnUpdate(8000);
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void EyeGlareAndFleshDeathsUnlockTheFortyFiveSecondWeaknessWindow()
    {
        using var arena = new Arena();
        Assert.True((arena.Body.UnitFlags & UnitFlags.NotSelectable) != 0);
        arena.Pull();
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26134);
        arena.EyeAi.OnUpdate(45_001);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26137);
        arena.EyeAi.OnUpdate(3000);
        arena.EyeAi.OnUpdate(1000);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26029);
        arena.Map.Combat.Kill(arena.Player, arena.Eye);
        arena.BodyAi.OnUpdate(4000);
        Assert.Equal(2, arena.Creatures.Creatures.Count(c => c.Entry == 15802 && c.IsAlive));
        arena.BodyAi.OnUpdate(8000);
        Assert.True((arena.Body.UnitFlags & UnitFlags.Spawning) == 0);
        arena.Map.Combat.DealDamage(arena.Player, arena.Body, 1000);
        Assert.Equal(arena.Body.MaxHealth, arena.Body.Health);
        foreach (Creature flesh in arena.Creatures.Creatures.Where(c => c.Entry == 15802 && c.IsAlive).ToArray())
            arena.Map.Combat.Kill(arena.Player, flesh);
        Assert.Equal(0u, arena.Body.InvincibilityHpThreshold);
        arena.Map.Combat.DealDamage(arena.Player, arena.Body, 1000);
        Assert.Equal(arena.Body.MaxHealth - 1000, arena.Body.Health);
        arena.BodyAi.OnUpdate(45_001);
        Assert.Equal(arena.Body.MaxHealth, arena.Body.InvincibilityHpThreshold);
        Assert.Equal(2, arena.Creatures.Creatures.Count(c => c.Entry == 15802 && c.IsAlive));
    }

    [Fact]
    public void StomachEntryExitAndWipeTrackThePlayerAndClearMembership()
    {
        using var arena = new Arena();
        Assert.False(arena.Raid.SendToCthunStomach(arena.Player));
        arena.EnterBodyPhase();
        Assert.True(arena.Raid.SendToCthunStomach(arena.Player));
        Assert.True(arena.Raid.IsInCthunStomach(arena.Player));
        Assert.InRange(arena.Player.Z, -97f, -95f);
        arena.Raid.OnAreaTrigger(arena.Player, 4033);
        Creature punt = Assert.Single(arena.Creatures.Creatures, c => c.Entry == 15922);
        Assert.IsType<CthunPuntAI>(punt.AI).OnUpdate(3000);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26224);
        arena.Raid.OnAreaTrigger(arena.Player, 4034);
        Assert.InRange(arena.Player.Z, 99f, 101f);
        arena.Raid.Update(1600);
        Assert.False(arena.Raid.IsInCthunStomach(arena.Player));

        Assert.True(arena.Raid.SendToCthunStomach(arena.Player));
        Assert.True(arena.Raid.KillPlayersInCthunStomach());
        Assert.False(arena.Player.IsAlive);
        Assert.False(arena.Raid.IsInCthunStomach(arena.Player));
    }

    [Fact]
    public void ClawBirthCreatesAndCleansItsPortal()
    {
        using var arena = new Arena();
        Creature claw = arena.Creatures.SummonCorpseTimedDespawn(arena.Body, 15725,
            arena.Player.X, arena.Player.Y, arena.Player.Z, 0, arena.Player, 1500)!;
        var ai = Assert.IsType<CthunTentacleAI>(claw.AI);
        Assert.Contains(arena.Creatures.Creatures, c => c.Entry == 15904);
        ai.OnUpdate(3000);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26139);
        arena.Map.Combat.Kill(arena.Player, claw);
        Assert.DoesNotContain(arena.Creatures.Creatures, c => c.Entry == 15904);
    }
}

/// <summary>
/// Despawn Tent Port (26404) through a real <see cref="SpellSystem"/>: the dying Giant Claw Tentacle casts it and its DUMMY, aimed by the ClassicDB
/// spell_script_target rows (15904, 15910), despawns the portal (mangos-classic boss_cthun.cpp DespawnTentPort). Spell.dbc 5875: DUMMY at
/// TARGET_UNIT_SCRIPT_NEAR_CASTER (38), range index 12.
/// </summary>
public sealed class CthunDespawnTentPortTests
{
    [Fact]
    public void GiantClawDeath_CastsDespawnTentPort_WhichDespawnsTheNearestPortalOnly()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(531);
        map.AddUpdater(new TempleOfAhnQirajInstance(map)); // the tentacle AIs come from the instance's script table
        SpellInfo despawn = SpellTestKit.Spell(26404, SpellTestKit.Effect(SpellEffectName.Dummy, 1, SpellImplicitTarget.UnitScriptNearCaster))
            with { RangeIndex = 12, Range = new SpellRange(0, 100) };
        var spells = new SpellSystem(new SpellStore([despawn], [], [], [new(26404, 1, 15904, 0), new(26404, 1, 15910, 0)]), () => 10000)
            { Units = new MapObjectResolver() };
        SpellScriptDispatcher.Install(spells, new SpellScriptRegistry([new CthunDespawnTentPortScript()]));
        var content = new CreatureContent([Template(15728), Template(15910), Template(15800)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(spells), Hostility = new AlwaysHostile() });
        map.AddUpdater(creatures);
        Player player = TestWorld.CreatePlayer(1, -8570, 1986, new FakeSession(), 531);
        player.Relocate(-8570, 1986, 100.4f, 0, 0);
        world.AddPlayer(player);
        world.RunTick(0);

        // A portal the claw does not own, next to it: the spell's own target selection (nearest listed entry) picks it.
        Creature claw = creatures.SpawnTemporary(content.FindTemplate(15728)!, -8560, 1986, 100.4f, 0);
        Assert.IsType<CthunTentacleAI>(claw.AI);
        Creature bystander = creatures.SpawnTemporary(content.FindTemplate(15800)!, -8561, 1986, 100.4f, 0);
        Creature stray = creatures.SpawnTemporary(content.FindTemplate(15910)!, -8560.5f, 1986, 100.4f, 0);
        // The claw's own portal (OnRespawn) goes first, so the AI's direct portal removal has nothing left to remove: only the spell can
        // despawn the stray.
        Creature own = Assert.Single(creatures.Creatures, c => c.Entry == 15910 && c != stray);
        creatures.ForcedDespawn(own, 0);
        world.RunTick(0);
        Assert.Contains(stray, creatures.Creatures);
        map.Combat.Kill(player, claw);
        world.RunTick(0);

        Assert.DoesNotContain(creatures.Creatures, c => c.Entry == 15910);
        Assert.Contains(bystander, creatures.Creatures);
        Assert.DoesNotContain(stray, creatures.Creatures);
    }
}
