using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts.WorldBosses;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.WorldBosses;

/// <summary>
/// Kazzak's ability timers use the "retry" countdown: a timer that ran out stays at 0 when the cast does not happen (no target) and the
/// ability fires on the first update where it can, without waiting for a new delay. Merging this with the latched or zero-disabled
/// variants would disarm failed casts.
/// </summary>
public sealed class WorldBossTimerRetryTests
{
    private sealed class Field : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public Creature Boss { get; }
        public Player Tank { get; }

        public Field()
        {
            WorldMaps.Of(World).Load(new MapContent([new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", "")], [], [], [], []));
            Map map = World.GetMap(1);
            var creatures = new CreatureMapSystem(map, Content([Template(KazzakAI.Entry)], []), new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 },
                random: new Random(1), aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            map.AddUpdater(creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 1);
            Tank.Relocate(0, 0, 450, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = creatures.SpawnTemporary(Template(KazzakAI.Entry), 1, 0, 450, 0);
            Boss.AI!.AttackStart(Tank);
        }

        public int CountOf(uint spell) => Caster.Casts.Count(c => c.Spell == spell);

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void Kazzak_Cleave_FiresOnlyOnceItsSevenSecondTimerHasRunOut()
    {
        using var f = new Field();
        f.Boss.AI!.OnUpdate(6999);
        Assert.Equal(0, f.CountOf(KazzakAI.SpellCleave));
        f.Boss.AI.OnUpdate(1);
        Assert.Equal(1, f.CountOf(KazzakAI.SpellCleave));
        // re-armed to 8-12 s: nothing within 7.999 s, exactly one more by 12 s
        f.Boss.AI.OnUpdate(7999);
        Assert.Equal(1, f.CountOf(KazzakAI.SpellCleave));
        f.Boss.AI.OnUpdate(4001);
        Assert.Equal(2, f.CountOf(KazzakAI.SpellCleave));
    }

    [Fact]
    public void Kazzak_Mark_WithNoManaUserStaysDueAndFiresOnTheFirstUpdateOnceOneExists()
    {
        using var f = new Field();
        f.Boss.AI!.OnUpdate(25000);
        Assert.Equal(0, f.CountOf(KazzakAI.SpellMarkOfKazzak));
        f.Boss.AI.OnUpdate(1000);
        Assert.Equal(0, f.CountOf(KazzakAI.SpellMarkOfKazzak));

        f.Tank.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        f.Tank.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        f.Tank.SetUInt32(UpdateFields.UnitFieldPower1, 500);
        f.Boss.AI.OnUpdate(1);
        Assert.Equal(1, f.CountOf(KazzakAI.SpellMarkOfKazzak));

        // re-armed to 20 s after the successful cast
        f.Boss.AI.OnUpdate(19999);
        Assert.Equal(1, f.CountOf(KazzakAI.SpellMarkOfKazzak));
        f.Boss.AI.OnUpdate(1);
        Assert.Equal(2, f.CountOf(KazzakAI.SpellMarkOfKazzak));
    }
}
