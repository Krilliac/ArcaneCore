using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The two default members the movement generators gained (vmangos MovementGenerator::GetResetPosition and
/// IsReachable, MovementGenerator.h:61-64): neutral by default, and evade consults the default generator.
/// </summary>
public sealed class CreatureMovementSeamTests
{
    private sealed class ResetPositionGenerator(CreatureHome? reset, bool reachable) : ICreatureMovementGenerator
    {
        public MovementGeneratorType Type => MovementGeneratorType.Idle;

        public void Initialize(Creature creature, ICreatureMover mover)
        {
        }

        public bool Update(Creature creature, ICreatureMover mover, uint diffMs) => true;

        public CreatureHome? GetResetPosition(Creature creature) => reset;

        public bool IsReachable => reachable;
    }

    [Fact]
    public void TheBuiltInGenerators_KeepTheDefaults()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        ICreatureMovementGenerator idle = IdleMovementGenerator.Instance;
        Assert.Null(idle.GetResetPosition(wolf));
        Assert.True(idle.IsReachable);
        Assert.True(wolf.Motion.IsReachable);
        ICreatureMovementGenerator random = new RandomMovementGenerator();
        Assert.Null(random.GetResetPosition(wolf));
        Assert.True(random.IsReachable);
    }

    [Fact]
    public void MotionMaster_ReportsTheTopGeneratorsReachability()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        wolf.Motion.Initialize(new ResetPositionGenerator(reset: null, reachable: false), system, start: true);

        Assert.False(wolf.Motion.IsReachable);
    }

    [Fact]
    public void Evade_RunsToTheDefaultGeneratorsResetPosition_WhenItSuppliesOne()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Motion.Initialize(new ResetPositionGenerator(new CreatureHome(20f, 5f, 83.5f, 2.0f), reachable: true), system, start: true);

        system.EnterEvadeMode(wolf);
        Assert.Equal(MovementGeneratorType.Home, wolf.Motion.CurrentType);
        Run(world, 4000);

        Assert.False(wolf.IsInEvadeMode);
        Assert.Equal(20f, wolf.X, 2);
        Assert.Equal(5f, wolf.Y, 2);
        Assert.Equal(2.0f, wolf.Orientation, 2);
    }

    [Fact]
    public void Evade_WithoutAResetPosition_StillRunsToTheSpawnPoint()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Relocate(15f, 3f, 83.5f, 0f, 0);

        system.EnterEvadeMode(wolf);
        Run(world, 4000);

        Assert.Equal(10f, wolf.X, 2);
        Assert.Equal(0f, wolf.Y, 2);
        Assert.Equal(1.5f, wolf.Orientation, 2);
    }

    private static CreatureContent Content(IEnumerable<CreatureTemplate> templates, IEnumerable<CreatureSpawn> spawns)
        => new(templates, spawns, [], [], []);
}
