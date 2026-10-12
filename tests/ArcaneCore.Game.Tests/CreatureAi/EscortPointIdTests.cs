using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// <see cref="EscortAI"/>'s waypoint callback reports the DB point id; <see cref="EscortAI.SetCurrentWaypoint"/> takes a 0-based index. The
/// point-id API keeps scripts from confusing the two. The path here uses the ids 10, 20 ... 60, so an id is never an index plus one.
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

        public List<uint> Started { get; } = [];

        protected override void WaypointStart(uint pointId) => Started.Add(pointId);

        public Action<JumpEscort, uint>? OnReached { get; set; }

        public void Respawn() => JustRespawned();

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
        CreatureWaypoint[] path = [.. Enumerable.Range(1, 6).Select(i => new CreatureWaypoint((uint)(i * 10), 10 + (i * 3), 0, Z, 0, 0))];
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

        Assert.Equal(0, ai.IndexOfPointId(10));
        Assert.Equal(2, ai.IndexOfPointId(30));
        Assert.Equal(5, ai.IndexOfPointId(60));
        Assert.Equal(-1, ai.IndexOfPointId(0));
        Assert.Equal(-1, ai.IndexOfPointId(1)); // an index-plus-one is no id here
        Assert.Equal(-1, ai.IndexOfPointId(6));
        Assert.Equal(-1, ai.IndexOfPointId(70));
    }

    [Fact]
    public void SetNextWaypointByPointId_FromOutsideTheCallback_HeadsForThatPoint()
    {
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        Assert.True(ai.Start());

        Assert.True(ai.SetNextWaypointByPointId(40));
        Assert.Equal(3, ai.CurrentWaypointIndex); // point 40 is index 3
        Assert.False(ai.SetNextWaypointByPointId(4)); // the index-plus-one is no id: refused, nothing changes
        Assert.False(ai.SetNextWaypointByPointId(99));
        Assert.Equal(3, ai.CurrentWaypointIndex);

        RunUntil(world, () => ai.Reached.Count > 0);
        Assert.Equal(40u, ai.Reached[0]);
    }

    [Fact]
    public void SetNextWaypointByPointId_InsideTheCallback_MakesThatPointTheNextOne_NotTheOneAfterIt()
    {
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        bool jumped = false;
        ai.OnReached = (escort, point) =>
        {
            if (point == 10 && !jumped)
            {
                jumped = true;
                Assert.True(escort.SetNextWaypointByPointId(50)); // "after point 10, point 50 is next"
            }
        };
        Assert.True(ai.Start());

        RunUntil(world, () => ai.Reached.Count >= 2);
        Assert.Equal([10u, 50u], ai.Reached);
    }

    [Fact]
    public void TheZeroBasedSetter_StaysAnIndex_AndLandsOnePointLateInsideTheCallback()
    {
        // The trap this API exists for: index 4 set inside the callback of point 10 is followed by the usual advance, so index 5 (point 60) is next.
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        bool jumped = false;
        ai.OnReached = (escort, point) =>
        {
            if (point == 10 && !jumped)
            {
                jumped = true;
                escort.SetCurrentWaypoint(4);
            }
        };
        Assert.True(ai.Start());

        RunUntil(world, () => ai.Reached.Count >= 2);
        Assert.Equal([10u, 60u], ai.Reached);
    }

    [Fact]
    public void SetNextWaypointByPointId_OutsideTheCallback_ToANearbyPoint_DoesNotStallTheEscort()
    {
        // Points are 3 yd apart. The escort is walking to point 10 when a script makes point 20 (3 yd from point 10, so inside the 10 yd arrival
        // tolerance but with another id) the next one: the arrival at point 10 must send it on to point 20 instead of leaving it standing.
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        Assert.True(ai.Start());
        RunUntil(world, () => ai.Started.Count > 0);
        Assert.Equal(10u, ai.Started[0]);
        Assert.Empty(ai.Reached);

        Assert.True(ai.SetNextWaypointByPointId(20));

        RunUntil(world, () => ai.Reached.Count > 0);
        Assert.Equal(20u, ai.Reached[0]);
        RunUntil(world, () => ai.Reached.Count >= 3); // and it carries on along the path
        Assert.Equal([20u, 30u, 40u], ai.Reached.Take(3));
    }

    private static bool RedirectPending(JumpEscort ai)
        => (bool)typeof(EscortAI).GetField("_redirectPending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ai)!;

    [Fact]
    public void AStaleRedirect_DoesNotSurviveStopAndANewStart()
    {
        // A redirect chosen outside the callback is pending until the escort arrives. Ending the escort and starting it again must not carry it
        // over: an arrival at a point that is not the destination ("Waypoint out of order") must then leave the walk alone instead of re-arming it.
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        Assert.True(ai.Start());
        Assert.True(ai.SetNextWaypointByPointId(20));
        Assert.True(RedirectPending(ai));
        ai.Stop();

        Assert.True(ai.Start());

        Assert.False(RedirectPending(ai));
        ai.OnMovementInform(MovementGeneratorType.Point, 20); // within tolerance of point 10, but not the destination
        world.RunTick(100);
        Assert.Empty(ai.Started); // still inside the first-waypoint delay: a stale redirect would have cut it to 1 ms
    }

    [Fact]
    public void AStaleRedirect_DoesNotSurviveARespawn()
    {
        (WorldRuntime world, JumpEscort ai) = Setup();
        using WorldRuntime _ = world;
        Assert.True(ai.Start());
        Assert.True(ai.SetNextWaypointByPointId(20));
        Assert.True(RedirectPending(ai));

        ai.Respawn();

        Assert.False(RedirectPending(ai));
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
