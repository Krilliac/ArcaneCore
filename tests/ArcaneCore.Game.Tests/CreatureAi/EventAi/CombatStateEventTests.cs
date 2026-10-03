using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The in-combat state events (cmangos CreatureEventAI.cpp CheckEvent): power and health percent of the creature and its
/// victim, range, casting, facing, aura stacks, the generic timer and the unreachable-target event. Each row casts a marker
/// spell through a recording caster; every event starts evaluating at the first 600 ms batch.
/// </summary>
public sealed class CombatStateEventTests
{
    private sealed class FakeUnitSpells : IUnitSpellQueries
    {
        public Dictionary<(Unit Unit, uint Spell), int> Stacks { get; } = [];

        public HashSet<Unit> Casting { get; } = [];

        public int GetAuraStacks(Unit unit, uint spellId) => Stacks.GetValueOrDefault((unit, spellId));

        public bool IsCasting(Unit unit) => Casting.Contains(unit);
    }

    private sealed class UnreachableGenerator : ICreatureMovementGenerator
    {
        public MovementGeneratorType Type => MovementGeneratorType.Idle;

        public void Initialize(Creature creature, ICreatureMover mover)
        {
        }

        public bool Update(Creature creature, ICreatureMover mover, uint diffMs) => true;

        public bool IsReachable => false;
    }

    private static CreatureAiEvent Row(uint id, byte type, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0, uint flags = 0, int marker = 0)
        => new()
        {
            Id = id,
            CreatureId = WolfEntry,
            EventType = type,
            Flags = flags,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, marker == 0 ? (int)id : marker, (int)EventAiTarget.Self, 0),
        };

    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, FakeCaster Spells, FakeUnitSpells UnitSpells, Player Player, Creature Wolf) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public bool Cast(uint spell) => Spells.Casts.Any(c => c.Spell == spell);

        public int Casts(uint spell) => Spells.Casts.Count(c => c.Spell == spell);
    }

    private static Fight Start(IEnumerable<CreatureAiEvent> events, Action<CreatureTemplateBuilder>? template = null, float playerX = 0, bool chase = false)
    {
        CreatureContent content = new(
            [Template(configure: t =>
            {
                t.AIName = CreatureAiFactory.EventAIName;
                template?.Invoke(t);
            })],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [], new CreatureAiContent(events, []));
        var spells = new FakeCaster();
        var unitSpells = new FakeUnitSpells();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells, UnitSpells = unitSpells });
        (Player player, _) = AddPlayer(world, 1, playerX, 0);
        Creature wolf = Assert.Single(system.Creatures);

        // Stand still when fighting so the distance-based events see the positions the test sets up.
        wolf.AI!.CombatMovement = chase;
        return new Fight(world, map, system, spells, unitSpells, player, wolf);
    }

    private static void Pull(Fight f) => f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);

    // --- power and health percent ------------------------------------------------------------------

    [Fact]
    public void ManaPercent_FiresInsideTheRange_AndOnlyForAManaCreature()
    {
        // MANA (3): max%, min% (CreatureEventAI.h:635-644); the creature needs mana (cmangos HasMana: its power type is mana).
        using Fight f = Start(
            [Row(1, 3, p1: 50, p2: 0), Row(2, 3, p1: 100, p2: 60)],
            t => { t.MinLevelMana = 100; t.MaxLevelMana = 100; });
        Pull(f);
        MapCombat.SetPower(f.Wolf, PowerType.Mana, 40);

        Run(f.World, 700);

        Assert.True(f.Cast(1));   // 40 percent is within [0, 50]
        Assert.False(f.Cast(2));  // and outside [60, 100]
    }

    [Fact]
    public void ManaPercent_NeverFiresForACreatureWithoutMana()
    {
        using Fight f = Start([Row(1, 3, p1: 100, p2: 0)]);
        Pull(f);

        Run(f.World, 1300);

        Assert.False(f.Cast(1));
    }

    [Fact]
    public void EnergyPercent_UsesTheEnergyBar()
    {
        using Fight f = Start([Row(1, 31, p1: 50, p2: 0), Row(2, 31, p1: 100, p2: 60)], t => t.UnitClass = (byte)Game.Class.Rogue);
        Pull(f);
        MapCombat.SetPower(f.Wolf, PowerType.Energy, 30);

        Run(f.World, 700);

        Assert.True(f.Cast(1));
        Assert.False(f.Cast(2));
    }

    [Fact]
    public void TargetHealthAndTargetMana_ReadTheVictim()
    {
        using Fight f = Start([Row(1, 12, p1: 50, p2: 0), Row(2, 12, p1: 100, p2: 60), Row(3, 18, p1: 30, p2: 0), Row(4, 18, p1: 100, p2: 31)]);
        f.Player.MaxHealth = 100;
        f.Player.Health = 40;
        f.Player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        f.Player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        MapCombat.SetPower(f.Player, PowerType.Mana, 25);
        Pull(f);

        Run(f.World, 700);

        Assert.True(f.Cast(1));   // victim health 40 percent
        Assert.False(f.Cast(2));
        Assert.True(f.Cast(3));   // victim mana 25 percent
        Assert.False(f.Cast(4));
    }

    [Fact]
    public void PercentEvents_NeedCombat()
    {
        using Fight f = Start([Row(1, 3, p1: 100, p2: 0), Row(2, 12, p1: 100, p2: 0)], t => { t.MinLevelMana = 100; t.MaxLevelMana = 100; });

        Run(f.World, 1300); // never pulled

        Assert.Empty(f.Spells.Casts);
    }

    // --- range -------------------------------------------------------------------------------------

    [Fact]
    public void RangeEvent_ComparesTheVictimsDistanceWithTheRangeBetweenBoundingRadii()
    {
        // The player stands 5 yd from the wolf. IsInRange adds both bounding radii to each bound (Object.cpp:1401-1420),
        // so [0, 5] holds only because of them while [5.4, 40] is still too far inside it.
        using Fight f = Start([Row(1, 9, p1: 0, p2: 10), Row(2, 9, p1: 10, p2: 40), Row(3, 9, p1: 0, p2: 4)]);
        Pull(f);
        float radii = f.Wolf.BoundingRadius + f.Player.BoundingRadius;
        Assert.InRange(5f, 4f + radii, 10f + radii); // the layout the assertions rely on

        Run(f.World, 700);

        Assert.True(f.Cast(1));   // inside [0, 10]
        Assert.False(f.Cast(2));  // closer than the 10 yd minimum
        Assert.False(f.Cast(3));  // beyond 4 yd + radii
    }

    // --- target casting -----------------------------------------------------------------------------

    [Fact]
    public void TargetCasting_FiresWhileTheVictimCasts_AndRepeatsOnParametersOneAndTwo()
    {
        using Fight f = Start([Row(1, 13, p1: 1000, p2: 1000, flags: 1)]);
        Pull(f);

        Run(f.World, 1300);
        Assert.False(f.Cast(1));

        f.UnitSpells.Casting.Add(f.Player);
        Run(f.World, 700);
        Assert.Equal(1, f.Casts(1));

        Run(f.World, 400); // the 1000 ms repeat timer: batches of 600 ms count it down to 0 on the second batch
        Assert.Equal(1, f.Casts(1));
        Run(f.World, 900);
        Assert.Equal(2, f.Casts(1));
    }

    // --- facing --------------------------------------------------------------------------------------

    [Fact]
    public void FacingTarget_DistinguishesFrontAndBack_WithinFiveYards()
    {
        // The wolf stands at +X of the player, who faces +X: the wolf is in front of its victim.
        using Fight f = Start([Row(1, 33, p1: 0, flags: 1), Row(2, 33, p1: 1, flags: 1)]);
        f.Player.Relocate(0, 0, 83.5f, 0f, 0);
        Pull(f);

        Run(f.World, 700);
        Assert.False(f.Cast(1));
        Assert.True(f.Cast(2));

        // The victim turns around: now the wolf is behind it.
        f.Player.Relocate(0, 0, 83.5f, MathF.PI, 0);
        Run(f.World, 1300);
        Assert.True(f.Cast(1));
    }

    [Fact]
    public void FacingTarget_NeedsTheCreatureCloseToTheVictim()
    {
        // The player stands 15 yd from the wolf: further than the 5 yd of isInFront/isInBack (Object.cpp:1572-1580).
        using Fight f = Start([Row(1, 33, p1: 0, flags: 1), Row(2, 33, p1: 1, flags: 1)], playerX: -10);
        f.Player.Relocate(-10, 0, 83.5f, 0f, 0);
        Pull(f);

        Run(f.World, 1300);

        Assert.False(f.Cast(1));
        Assert.False(f.Cast(2));
    }

    // --- auras ---------------------------------------------------------------------------------------

    [Fact]
    public void AuraEvents_CompareStackAmounts()
    {
        // AURA / TARGET_AURA: at least Amount stacks; MISSING_AURA / TARGET_MISSING_AURA: fewer than Amount (:458-491).
        using Fight f = Start(
        [
            Row(1, 23, p1: 4000, p2: 2),   // own aura 4000 with >= 2 stacks
            Row(2, 23, p1: 4000, p2: 3),   // >= 3: no
            Row(3, 24, p1: 4001, p2: 1),   // victim has aura 4001
            Row(4, 27, p1: 4002, p2: 1),   // own aura 4002 missing
            Row(5, 27, p1: 4000, p2: 3),   // own aura 4000 has fewer than 3 stacks: fires
            Row(6, 28, p1: 4001, p2: 2),   // victim has 1 stack of 4001: fewer than 2: fires
            Row(7, 28, p1: 4001, p2: 1),   // victim has the aura: does not fire
        ]);
        f.UnitSpells.Stacks[(f.Wolf, 4000)] = 2;
        f.UnitSpells.Stacks[(f.Player, 4001)] = 1;
        Pull(f);

        Run(f.World, 700);

        Assert.True(f.Cast(1));
        Assert.False(f.Cast(2));
        Assert.True(f.Cast(3));
        Assert.True(f.Cast(4));
        Assert.True(f.Cast(5));
        Assert.True(f.Cast(6));
        Assert.False(f.Cast(7));
    }

    [Fact]
    public void AuraEvents_WithoutASpellSystem_SeeNoAuras()
    {
        CreatureContent content = new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)], [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent([Row(1, 23, p1: 4000, p2: 1), Row(2, 27, p1: 4000, p2: 1)], []));
        var spells = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime w = world;
        AddPlayer(w, 1, 0, 0);

        Run(w, 700);

        Assert.Equal([2u], spells.Casts.Select(c => c.Spell));
    }

    // --- generic timer and unreachable target ------------------------------------------------------------

    [Fact]
    public void TimerGeneric_RunsInAndOutOfCombat_OnItsInitialAndRepeatTimers()
    {
        using Fight f = Start([Row(1, 29, p1: 1000, p2: 1000, p3: 1000, p4: 1000, flags: 1)]);

        Run(f.World, 1300); // not in combat: batches at 600 and 1200; the initial 1000 ms timer ends at the second
        Assert.Equal(1, f.Casts(1));

        Run(f.World, 1300); // repeats every two batches
        Assert.Equal(2, f.Casts(1));
    }

    [Fact]
    public void TargetNotReachable_ConsultsTheGeneratorOnTop_AndIsPolledEveryBatch()
    {
        // Nothing marks the chase generator unreachable yet (that is the no-path chase work), and the host re-pushes a chase on
        // every tick, so the condition is checked directly: victim present, combat movement on, generator on top unreachable.
        using Fight f = Start([Row(1, 36, flags: 1)], chase: true);
        Pull(f);
        var handler = new TargetNotReachableEvent();
        EventAiHolder holder = Assert.Single(((CreatureEventAI)f.Wolf.AI!).Engine.Holders);
        EventAiContext context = ((CreatureEventAI)f.Wolf.AI!).Engine.Context;

        Assert.True(handler.CheckedEveryBatch);
        Assert.False(handler.Check(context, holder, null)); // the chase can reach the player

        f.Wolf.Motion.Initialize(new UnreachableGenerator(), f.System, start: true);
        Assert.True(handler.Check(context, holder, null));

        f.Wolf.AI!.CombatMovement = false; // the creature is not chasing: nothing to be unreachable
        Assert.False(handler.Check(context, holder, null));
    }
}
