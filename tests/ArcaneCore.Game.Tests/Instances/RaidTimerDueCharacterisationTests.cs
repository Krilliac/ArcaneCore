using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Instances.Scripts.ZulGurub;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Characterisation of the per-class countdown helpers (<c>Due</c>) that the ScriptTimers refactor consolidates. Anubisath's latch variant
/// (0 = disabled, re-arms to 1 on firing) and the Zul'Gurub trash variant (fires one tick later at exact equality, no zeroing) must not be
/// merged into the plain "fires when timer is at or below diff" semantics.
/// </summary>
public sealed class RaidTimerDueCharacterisationTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public CreatureMapSystem Creatures { get; }
        public Creature Boss { get; }
        public Player Tank { get; }

        public Arena(uint map, uint entry)
        {
            Map m = World.GetMap(map);
            var data = Assert.IsAssignableFrom<InstanceData>(InstanceScriptRegistry.Default.Create(m));
            data.Initialize();
            m.AddUpdater(data);
            Creatures = new CreatureMapSystem(m, Content([Template(entry)], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            m.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), map);
            float z = map == 309 ? 50 : 450;
            Tank.Relocate(0, 0, z, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, z, 0);
        }

        public void Dispose() => World.Dispose();
    }

    private static AnubisathDefenderAI AggroUntil(Arena a, Func<(uint Meteor, uint Plague, uint Thunderclap, uint ShadowStorm, uint Summon), bool> armed)
    {
        var ai = Assert.IsType<AnubisathDefenderAI>(a.Boss.AI);
        a.Boss.AI!.AttackStart(a.Tank);
        for (int i = 0; i < 200; i++)
        {
            ai.OnAggro(a.Tank);
            if (armed(ai.Timers)) break;
        }
        Assert.True(armed(ai.Timers), "no seeded aggro roll armed the wanted ability");
        a.Caster.Casts.Clear();
        return ai;
    }

    [Fact]
    public void AnubisathDefender_DisabledTimerNeverFiresAndStaysZero()
    {
        using var a = new Arena(531, AnubisathDefenderAI.Entry);
        var ai = AggroUntil(a, t => t.Plague != 0);
        Assert.Equal(0u, ai.Timers.Meteor);
        ai.OnUpdate(60_000);
        Assert.Equal(0u, ai.Timers.Meteor);
        Assert.DoesNotContain(a.Caster.Casts, c => c.Spell == AnubisathDefenderAI.Meteor);
    }

    [Fact]
    public void AnubisathDefender_ArmedTimerFiresExactlyWhenCountdownReachesZero()
    {
        using var a = new Arena(531, AnubisathDefenderAI.Entry);
        var ai = AggroUntil(a, t => t.Thunderclap != 0);
        uint wait = ai.Timers.Thunderclap;
        ai.OnUpdate(wait - 1);
        Assert.DoesNotContain(a.Caster.Casts, c => c.Spell == AnubisathDefenderAI.Thunderclap);
        Assert.Equal(1u, ai.Timers.Thunderclap);
        ai.OnUpdate(1);
        Assert.Contains(a.Caster.Casts, c => c.Spell == AnubisathDefenderAI.Thunderclap);
        Assert.InRange(ai.Timers.Thunderclap, 6000u, 10000u);
    }

    [Fact]
    public void AnubisathDefender_FailedCastLeavesTimerLatchedAtOneAndRetriesEveryTick()
    {
        using var a = new Arena(531, AnubisathDefenderAI.Entry);
        var ai = AggroUntil(a, t => t.Plague != 0);
        // The tank is the only threat entry and Plague skips the tank, so there is no target and the cast never happens.
        ai.OnUpdate(60_000);
        Assert.Equal(1u, ai.Timers.Plague);
        Assert.DoesNotContain(a.Caster.Casts, c => c.Spell == AnubisathDefenderAI.Plague);
        ai.OnUpdate(1);
        Assert.Equal(1u, ai.Timers.Plague);
        ai.OnUpdate(5000);
        Assert.Equal(1u, ai.Timers.Plague);
        Assert.DoesNotContain(a.Caster.Casts, c => c.Spell == AnubisathDefenderAI.Plague);
    }

    [Fact]
    public void AnubisathDefender_SuccessfulMeteorRearmsWithTheRepeatDelay()
    {
        using var a = new Arena(531, AnubisathDefenderAI.Entry);
        var ai = AggroUntil(a, t => t.Meteor != 0);
        ai.OnUpdate(60_000);
        Assert.Contains(a.Caster.Casts, c => c.Spell == AnubisathDefenderAI.Meteor && ReferenceEquals(c.Target, a.Tank));
        Assert.InRange(ai.Timers.Meteor, 8000u, 12000u);
    }

    [Fact]
    public void Soulflayer_TimerAtExactEqualityDoesNotFire_ButFiresOnTheNextTick()
    {
        using var a = new Arena(309, SoulflayerAI.Entry);
        var ai = Assert.IsType<SoulflayerAI>(a.Boss.AI);
        ai.OnRespawn();
        a.Boss.AI!.AttackStart(a.Tank);
        uint cc = ai.Timers.Cc;
        a.Caster.Casts.Clear();
        ai.OnUpdate(cc);
        Assert.DoesNotContain(a.Caster.Casts, c => c.Spell == ai.CcSpell);
        Assert.Equal(0u, ai.Timers.Cc);
        ai.OnUpdate(1);
        Assert.Contains(a.Caster.Casts, c => c.Spell == ai.CcSpell);
        Assert.InRange(ai.Timers.Cc, 8000u, 10000u);
    }
}
