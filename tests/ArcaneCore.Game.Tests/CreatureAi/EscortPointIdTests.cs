using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// <see cref="EscortAI"/>'s waypoint callback reports the 1-based DB point id; <see cref="EscortAI.SetCurrentWaypoint"/> takes a 0-based index. The
/// point-id API keeps scripts from confusing the two ("jump so that point 10 is next" means index 9).
/// </summary>
public sealed class EscortPointIdTests
{
    private const uint Entry = 47101;
    private const float Z = 83.5f;

    private sealed class NeverHostile : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => false;

        public bool CanAssist(Creature helper, Creature caller) => false;
    }

    private sealed class JumpEscort(Creature creature) : EscortAI(creature)
    {
        public List<uint> Reached { get; } = [];

        public Action<JumpEscort, uint>? OnReached { get; set; }

        protected override void WaypointReached(uint pointId)
        {
            Reached.Add(pointId);
            OnReached?.Invoke(this, pointId);
        }

        protected override void Reset()
        {
        }
    }

    private static (WorldRuntime World, JumpEscort Ai) Setup()
    {
        CreatureWaypoint[] path = [.. Enumerable.Range(1, 6).Select(i => new CreatureWaypoint((uint)i, 10 + (i * 3), 0, Z, 0, 0))];
        var content = new CreatureContent([Template(Entry)], [Spawn(1, Entry, 5, 0, Z)], [], [], [],
            entryWaypoints: path.Select(p => (Entry, EscortAI.EscortPathId, p)));
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new NeverHostile() });
        AddPlayer(world, 1, 0, 0);
        system.RegisterEntryAi(Entry, c => new JumpEscort(c));
        return (world, (JumpEscort)Assert.Single(system.Creatures).AI!);
    }

    [Fact]
    public void IndexOfPointId_MapsTheOneBasedIdToTheZeroBasedPosition()
    {
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        Assert.True(ai.Start());

        Assert.Equal(0, ai.IndexOfPointId(1));
        Assert.Equal(5, ai.IndexOfPointId(6));
        Assert.Equal(-1, ai.IndexOfPointId(0));
        Assert.Equal(-1, ai.IndexOfPointId(7));
    }

    [Fact]
    public void SetNextWaypointByPointId_FromOutsideTheCallback_HeadsForThatPoint()
    {
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        Assert.True(ai.Start());

        Assert.True(ai.SetNextWaypointByPointId(4));
        Assert.Equal(3, ai.CurrentWaypointIndex); // point 4 is index 3
        Assert.False(ai.SetNextWaypointByPointId(99)); // refused, nothing changes
        Assert.Equal(3, ai.CurrentWaypointIndex);

        RunUntil(world, () => ai.Reached.Count > 0);
        Assert.Equal(4u, ai.Reached[0]);
    }

    [Fact]
    public void SetNextWaypointByPointId_InsideTheCallback_MakesThatPointTheNextOne_NotTheOneAfterIt()
    {
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        bool jumped = false;
        ai.OnReached = (escort, point) =>
        {
            if (point == 1 && !jumped)
            {
                jumped = true;
                Assert.True(escort.SetNextWaypointByPointId(5)); // "after point 1, point 5 is next"
            }
        };
        Assert.True(ai.Start());

        RunUntil(world, () => ai.Reached.Count >= 2);
        Assert.Equal([1u, 5u], ai.Reached);
    }

    [Fact]
    public void TheZeroBasedSetter_StaysAnIndex_AndLandsOnePointLateInsideTheCallback()
    {
        // The trap this API exists for: index 4 set inside the callback of point 1 is followed by the usual advance, so point 6 is next.
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        bool jumped = false;
        ai.OnReached = (escort, point) =>
        {
            if (point == 1 && !jumped)
            {
                jumped = true;
                escort.SetCurrentWaypoint(4);
            }
        };
        Assert.True(ai.Start());

        RunUntil(world, () => ai.Reached.Count >= 2);
        Assert.Equal([1u, 6u], ai.Reached);
    }

    private static void RunUntil(WorldRuntime world, Func<bool> done)
    {
        for (int i = 0; i < 600 && !done(); i++)
        {
            world.RunTick(100);
        }

        Assert.True(done());
    }
}
