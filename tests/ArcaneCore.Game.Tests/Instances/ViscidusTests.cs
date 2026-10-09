using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class ViscidusTests
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
        public ViscidusAI Ai => Assert.IsType<ViscidusAI>(Boss.AI);

        public Arena()
        {
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map);
            Map.AddUpdater(Raid);
            var content = new CreatureContent(
                [Template(15299, b => b.MinLevelHealth = b.MaxLevelHealth = 10_000), Template(15667), Template(15922)],
                [], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Casts, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 1, 0, new FakeSession(), 531);
            Tank.Relocate(1, 0, 450, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(content.FindTemplate(15299)!, 0, 0, 450, 0);
            Assert.True(Ai.AttackStart(Tank));
            Casts.Casts.Clear();
        }

        public void Freeze()
        {
            SpellInfo frost = Spell(900001, Effect(SpellEffectName.SchoolDamage, 1)) with { School = SpellSchool.Frost };
            for (int i = 0; i < 200; i++) Ai.OnSpellHit(Tank, frost);
        }

        public void Shatter()
        {
            for (int i = 0; i < 150; i++)
                Ai.OnMeleeHitReceived(new MeleeDamageInfo { Attacker = Tank, Target = Boss, Outcome = MeleeHitOutcome.Normal });
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void FrostThresholdsFreeze_PhysicalHitsExplode_AndFreezeExpiryRestartsProgress()
    {
        using var arena = new Arena();
        var ai = arena.Ai;
        SpellInfo frost = Spell(900001, Effect(SpellEffectName.SchoolDamage, 1)) with { School = SpellSchool.Frost };
        for (int i = 0; i < 99; i++) ai.OnSpellHit(arena.Tank, frost);
        Assert.DoesNotContain(arena.Casts.Casts, c => c.Spell == 26034);
        ai.OnSpellHit(arena.Tank, frost);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26034);
        for (int i = 0; i < 50; i++) ai.OnSpellHit(arena.Tank, frost);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26036);
        for (int i = 0; i < 50; i++) ai.OnSpellHit(arena.Tank, frost);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25937);

        ai.OnFreezeRemoved();
        arena.Casts.Casts.Clear();
        for (int i = 0; i < 99; i++) ai.OnSpellHit(arena.Tank, frost);
        Assert.DoesNotContain(arena.Casts.Casts, c => c.Spell == 26034);
        ai.OnSpellHit(arena.Tank, frost);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26034);

        for (int i = 0; i < 100; i++) ai.OnSpellHit(arena.Tank, frost);
        arena.Casts.Casts.Clear();
        for (int i = 0; i < 149; i++)
            ai.OnMeleeHitReceived(new MeleeDamageInfo { Attacker = arena.Tank, Target = arena.Boss, Outcome = MeleeHitOutcome.Normal });
        Assert.DoesNotContain(arena.Casts.Casts, c => c.Spell == 25938);
        ai.OnMeleeHitReceived(new MeleeDamageInfo { Attacker = arena.Tank, Target = arena.Boss, Outcome = MeleeHitOutcome.Normal });
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25938);
        Assert.Equal(20, arena.Casts.Casts.Count(c => c.Spell is >= 25865 and <= 25884));
    }

    [Fact]
    public void KilledGlobCostsFivePercent_WhileRejoinedGlobDoesNot()
    {
        using var arena = new Arena();
        arena.Freeze();
        arena.Shatter();
        Creature glob = arena.Creatures.SummonAt(arena.Boss, 15667, 40, 0, 450, 0, null, 60_000)!;
        Creature returning = arena.Creatures.SummonAt(arena.Boss, 15667, 42, 0, 450, 0, null, 60_000)!;
        Assert.IsType<ViscidusGlobAI>(glob.AI);
        arena.Ai.OnUpdate(2500);
        Assert.NotEqual(UnitFlags.None, arena.Boss.UnitFlags & UnitFlags.ImmuneToNpc);
        Assert.Equal(10_000u, arena.Boss.Health);
        arena.Map.Combat.Kill(arena.Tank, glob);
        Assert.Equal(9500u, arena.Boss.Health);

        returning.AI!.OnMovementInform(MovementGeneratorType.Point, 1);
        Assert.Equal(9500u, arena.Boss.Health);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25896);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25897);
        Assert.Equal(UnitFlags.None, arena.Boss.UnitFlags & UnitFlags.ImmuneToNpc);
        Assert.Equal(1u, arena.Boss.InvincibilityHpThreshold);
        arena.Casts.Casts.Clear();
        SpellInfo frost = Spell(900001, Effect(SpellEffectName.SchoolDamage, 1)) with { School = SpellSchool.Frost };
        for (int i = 0; i < 100; i++) arena.Ai.OnSpellHit(arena.Tank, frost);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26034);
    }

    [Fact]
    public void PhysicalDamageSpellsCount_UtilitySpellsAndShootDoNot()
    {
        using var arena = new Arena();
        arena.Freeze();
        SpellInfo utility = Spell(900002, Effect(SpellEffectName.ApplyAura, 0)) with { School = SpellSchool.Normal };
        SpellInfo shoot = Spell(5019, Effect(SpellEffectName.SchoolDamage, 1)) with { School = SpellSchool.Normal };
        SpellInfo physical = Spell(900003, Effect(SpellEffectName.WeaponDamage, 1)) with { School = SpellSchool.Normal };
        for (int i = 0; i < 200; i++) { arena.Ai.OnSpellHit(arena.Tank, utility); arena.Ai.OnSpellHit(arena.Tank, shoot); }
        Assert.DoesNotContain(arena.Casts.Casts, c => c.Spell == 25938);
        for (int i = 0; i < 150; i++) arena.Ai.OnSpellHit(arena.Tank, physical);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25938);
    }

    [Fact]
    public void PoisonTimersCreateToxinTrigger_AndWipeFailsTheEncounter()
    {
        using var arena = new Arena();
        arena.Ai.OnUpdate(40_000);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25993);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25991);
        Creature trigger = Assert.Single(arena.Creatures.Creatures, c => c.Entry == 15922);
        Assert.IsType<ViscidusToxinTriggerAI>(trigger.AI).OnUpdate(3000);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25989);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26575);
        arena.Ai.OnEvade();
        arena.Ai.OnReachedHome();
        Assert.Equal(EncounterState.Fail, arena.Raid.GetData(TempleOfAhnQirajInstance.Viscidus));
    }

    [Fact]
    public void BelowFivePercentExplosionAllowsEncounterDeath()
    {
        using var arena = new Arena();
        arena.Boss.Health = 400;
        arena.Freeze();
        arena.Shatter();
        Assert.False(arena.Boss.IsAlive);
        Assert.Equal(EncounterState.Done, arena.Raid.GetData(TempleOfAhnQirajInstance.Viscidus));
    }
}
