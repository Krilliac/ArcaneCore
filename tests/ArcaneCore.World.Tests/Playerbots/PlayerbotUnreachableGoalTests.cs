using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA cluster C3 on the synthetic mesh, through the code the bots run: the world destinations skip a spawn no walk reaches (no path search,
/// a reachable sibling chosen, the entry set aside when it was the last, the quest only without another objective and never counted towards
/// abandoning it, a completed quest's return never skipped, one report per window), and a bot on a fragment is moved to its bind point when its
/// goal is given up.
/// </summary>
public sealed class PlayerbotUnreachableGoalTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 Start = new(5, 5, 0), UnreachableSpawn = new(75, 5, 0), ReachableSpawn = new(20, 115, 0);

    // Tile A (the bot's, X 0-40 Y 0-40) joins C, E, F to the north; tile B (X 40-80), holding UnreachableSpawn, has a hole in its west and joins nothing.
    private static readonly (int X, int Y, CellTile Tile)[] Unjoined =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (12, 10, new CellTile(2, 0, 0, 80, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (13, 10, new CellTile(3, 0, 0, 120, 8, 8, Size: 5) { LinkedSides = [4] }),
    ];

    private static CreatureContent ContentOf(params Vector3[] spawns)
        => new([new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [.. spawns.Select((at, index) => new CreatureSpawn { Guid = (uint)(20001 + index), Entry = 6, MapId = 0, X = at.X, Y = at.Y, Z = at.Z })], [], [], []);

    private Task<SyntheticNavWorld> StartAsync(CreatureContent content)
        => SyntheticNavWorld.StartAsync(_fixture, Unjoined, new FloorLineOfSight(),
            configureServices: services => services.AddSingleton<ICreatureDataStore>(new Content(content)));

    /// <summary>Asked like a think until the graph and the tiles are ready: the control that the skip's answer is true here.</summary>
    private static async Task WaitUntilUnreachableAsync(SyntheticNavWorld world)
    {
        for (int think = 0; think < 80; think++)
        {
            if (await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsUnreachableOnFoot(world.Player, UnreachableSpawn, out _, out _))) return;
            await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.Fail("the spawn is never told unreachable");
    }

    // The content's creatures really spawn in the synthetic world and stand in sight; none counts as the bot's target, so the walk is what is decided.
    private static PlayerbotWorldDestinations DestinationsOf(SyntheticNavWorld world)
        => new(world.Session, new PlayerbotOptions { Enabled = true }) { Suspensions = new PlayerbotSuspensions(), Untakeable = (_, _) => true };

    [Fact]
    public async Task AnUnreachableSpawn_IsSkippedWithNoSearch_AndAReachableSiblingIsChosen()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn, ReachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        PlayerbotWorldDestinations destinations = DestinationsOf(world);
        float before = world.Player.Y;
        bool moved = await world.Host.OnWorldAsync(() =>
        {
            world.Session.ManagedBudget = new ManagedActionBudget(1);
            bool first = destinations.Update(world.Player, 6, 500);
            PlayerbotMotion.ElapseForTests(world.Player, 500);
            world.Session.ManagedBudget = new ManagedActionBudget(1);
            return first && destinations.Update(world.Player, 6, 500);
        });
        Assert.True(moved, $"T={destinations.TargetEntry} R={destinations.UnreachableReports} W={destinations.WaitingForTiles} Y={world.Player.Y}");
        Assert.True(world.Player.Y > before + 1f, "the bot walks north to the reachable sibling");
        Assert.True(world.Player.X < 40f, "and not east to the unreachable spawn");
        Assert.Equal(1, destinations.UnreachableReports);
        Assert.False(destinations.Suspensions!.IsEntrySuspended(6, world.Host.World.NowMs), "the entry has a reachable spawn left");
    }

    [Fact]
    public async Task WhenEverySpawnIsUnreachable_TheEntryIsSetAside_WithNoPathSearch_AndTheSkipIsReportedOnce()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        PlayerbotWorldDestinations destinations = DestinationsOf(world);
        (bool updated, long searches) = await world.Host.OnWorldAsync(() =>
        {
            long searchesBefore = PathSearchCounters.Current.Searches;
            world.Session.ManagedBudget = new ManagedActionBudget(1);
            bool result = destinations.Update(world.Player, 6, 500);
            return (result, PathSearchCounters.Current.Searches - searchesBefore);
        });
        Assert.False(updated);
        Assert.Equal(0, searches);
        Assert.True(destinations.Suspensions!.IsEntrySuspended(6, world.Host.World.NowMs));
        Assert.Equal(1, destinations.UnreachableReports);
        Assert.True(destinations.TryTakeUnreachable(out PlayerbotWorldDestinations.UnreachableSkip skip));
        Assert.Equal(6u, skip.Entry);
        Assert.NotEqual(skip.FromComponent, skip.ToComponent);
        Assert.False(destinations.HasUnreachableReports);

        // The same entry met again inside the window is skipped but not reported again; past the window it is.
        uint now = world.Host.World.NowMs;
        Assert.True(await world.Host.OnWorldAsync(() => destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 0, now + 1_000, returning: false, out _)));
        Assert.Equal(1, destinations.UnreachableReports);
        Assert.True(await world.Host.OnWorldAsync(() =>
            destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 0, now + PlayerbotWorldDestinations.UnreachableReportMs + 1, returning: false, out _)));
        Assert.Equal(2, destinations.UnreachableReports);
    }

    /// <summary>One think can skip several entries: each is queued with the goal held at that moment and none is lost to a later one; a full queue marks nothing reported.</summary>
    [Fact]
    public async Task SeveralEntriesSkippedInOneThink_AreAllReported_WithTheGoalHeldThen()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        PlayerbotWorldDestinations destinations = DestinationsOf(world);
        PlayerbotGoalKind goal = PlayerbotGoalKind.Grind;
        destinations.GoalOf = () => goal;
        await world.Host.OnWorldAsync(() =>
        {
            Assert.True(destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 0, 1_000, returning: false, out _));
            goal = PlayerbotGoalKind.Quest; // the goal changes before the lines are written
            Assert.True(destinations.SkipUnreachable(world.Player, 7, 20002, UnreachableSpawn, 55, 1_000, returning: false, out _));
            return true;
        });
        Assert.Equal(2, destinations.UnreachableReports);
        Assert.True(destinations.TryTakeUnreachable(out PlayerbotWorldDestinations.UnreachableSkip first));
        Assert.True(destinations.TryTakeUnreachable(out PlayerbotWorldDestinations.UnreachableSkip second));
        Assert.False(destinations.TryTakeUnreachable(out _));
        Assert.Equal((6u, PlayerbotGoalKind.Grind), (first.Entry, first.Goal));
        Assert.Equal((7u, PlayerbotGoalKind.Quest, 55u), (second.Entry, second.Goal, second.Quest));

        // More entries than the queue holds: the extra ones are not marked reported and are reported once the queue is drained.
        await world.Host.OnWorldAsync(() =>
        {
            for (uint entry = 100; entry < 100 + PlayerbotWorldDestinations.MaxPendingReports + 2; entry++)
                Assert.True(destinations.SkipUnreachable(world.Player, entry, 30000 + entry, UnreachableSpawn, 0, 2_000, returning: false, out _));
            return true;
        });
        Assert.Equal(2 + PlayerbotWorldDestinations.MaxPendingReports, destinations.UnreachableReports);
        while (destinations.TryTakeUnreachable(out _)) { }
        await world.Host.OnWorldAsync(() =>
        {
            for (uint entry = 100; entry < 100 + PlayerbotWorldDestinations.MaxPendingReports + 2; entry++)
                destinations.SkipUnreachable(world.Player, entry, 30000 + entry, UnreachableSpawn, 0, 2_500, returning: false, out _);
            return true;
        });
        Assert.Equal(2 + PlayerbotWorldDestinations.MaxPendingReports + 2, destinations.UnreachableReports);
    }

    /// <summary>
    /// Bot QA #807: a goal on no walking region (here 300 yards above the bot's own tile, as a retreat point in a hillside or in the air) is
    /// no skip, and the bot's own component is still given: the <c>bot-nav/goal-unreachable</c> line read "bot -1, goal -1" in every such
    /// live case although the bot stood on the continent's component.
    /// </summary>
    [Fact]
    public async Task AGoalOnNoWalkingRegion_IsNoSkip_AndTheBotsOwnComponentIsStillGiven()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        Vector3 offMesh = new(5, 25, 300);
        (bool unreachable, int from, int to, int own) = await world.Host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotNavigation.IsUnreachableOnFoot(world.Player, UnreachableSpawn, out int ownComponent, out _), "the control");
            bool answer = PlayerbotNavigation.IsUnreachableOnFoot(world.Player, offMesh, out int fromComponent, out int toComponent);
            return (answer, fromComponent, toComponent, ownComponent);
        });
        Assert.False(unreachable, "a place the graph cannot place is never called unreachable");
        Assert.True(own >= 0, $"the bot's component {own}");
        Assert.Equal(own, from);
        Assert.Equal(-1, to);

        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < PlayerbotNavigationAnomalies.RepeatedRefusals; i++)
                PlayerbotNavigationAnomalies.ObservePlan(world.Player, offMesh, PlayerbotMotion.CurrentPosition(world.Player), routed: false, inconclusive: false);
            return true;
        });
        string key = PlayerbotAnomalies.Key("nav", "goal-unreachable");
        Assert.Equal(1, cap.Count(key));
        Assert.Contains($"(region components bot {own}, goal -1)", cap.Notices.Single(n => n.Key == key).Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #1497: a goal 6.5 yards above the floor, within three yards of it flat, is found only by the far box and snaps to the floor
    /// beside the bot. It is far, has no component of its own (no verdict), and five refused plans toward it report no
    /// <c>bot-nav/no-path-connected</c>.
    /// </summary>
    [Fact]
    public async Task AGoalAboveTheFloor_FoundOnlyByTheFarBox_HasNoComponent_AndIsNotReportedNoPathConnected()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        Vector3 floor = new(20, 20, 0), inTheAir = new(20, 20, 6.5f);
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        (int floorRegion, bool floorFar, int airRegion, bool airFar, int airPlain, int own, int to) = await world.Host.OnWorldAsync(() =>
        {
            var nav = (NavMeshPathfinder)world.Player.Map!.Collision.Pathfinder!;
            NavRegionGraph graph = nav.RegionGraph(world.Player.Map!.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!;
            int onFloor = nav.RegionAt(graph, floor, locate, out bool onFloorFar);
            int inAir = nav.RegionAt(graph, inTheAir, locate, out bool inAirFar);
            PlayerbotNavigation.IsUnreachableOnFoot(world.Player, inTheAir, out int fromComponent, out int toComponent);
            return (onFloor, onFloorFar, inAir, inAirFar, nav.RegionAt(graph, inTheAir, locate), fromComponent, toComponent);
        });
        Assert.True(floorRegion >= 0, "the control: the floor is on a region");
        Assert.False(floorFar, "the control: a point on the floor is not far");
        Assert.True(airRegion >= 0, "the far box snaps the goal to the floor polygon");
        Assert.True(airFar);
        Assert.Equal(airRegion, airPlain);
        Assert.True(own >= 0, $"the bot's component {own}");
        Assert.Equal(-1, to);

        using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < PlayerbotNavigationAnomalies.RepeatedRefusals; i++)
                PlayerbotNavigationAnomalies.ObservePlan(world.Player, inTheAir, PlayerbotMotion.CurrentPosition(world.Player), routed: false, inconclusive: false);
            return true;
        });
        Assert.Equal(1, cap.Count(PlayerbotAnomalies.Key("nav", "goal-unreachable")));
        Assert.Equal(0, cap.Count(PlayerbotAnomalies.Key("nav", "no-path-connected")));
    }

    /// <summary>
    /// Issue #1497 review: a goal inside the near box but past the edge of the walkable cells of the unjoined tile is still told unreachable, and its
    /// component is the tile's own (on the real mesh, eroded by the agent radius, spawns lie just past the walkable cells; this synthetic mesh is not eroded).
    /// </summary>
    [Fact]
    public async Task AGoalJustOutsideAWalkableCellOfTheUnjoinedTile_IsStillToldUnreachable_WithItsOwnComponent()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        Vector3 besideTheHole = new(69.5f, 5, 0);
        (bool unreachable, int from, int to, int spawnComponent) = await world.Host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotNavigation.IsUnreachableOnFoot(world.Player, UnreachableSpawn, out _, out int spawn), "the control");
            bool answer = PlayerbotNavigation.IsUnreachableOnFoot(world.Player, besideTheHole, out int fromComponent, out int toComponent);
            return (answer, fromComponent, toComponent, spawn);
        });
        Assert.True(unreachable, "a goal 0.5 yd past the walkable cells is still on the unjoined tile");
        Assert.True(from >= 0 && from != to, $"bot {from}, goal {to}");
        Assert.Equal(spawnComponent, to);
    }

    /// <summary>
    /// Issue #1497: a far goal (6.5 yd above the walkable cells) that snaps into ANOTHER component than the bot's keeps its verdict: it is
    /// told unreachable, and its component is the unjoined tile's, not -1.
    /// </summary>
    [Fact]
    public async Task AFarGoalSnappingIntoAnotherComponent_IsStillToldUnreachable_WithThatComponent()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        Vector3 aboveTheTile = new(75, 5, 6.5f);
        (bool unreachable, int from, int to, int spawnComponent) = await world.Host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotNavigation.IsUnreachableOnFoot(world.Player, UnreachableSpawn, out _, out int spawn), "the control");
            bool answer = PlayerbotNavigation.IsUnreachableOnFoot(world.Player, aboveTheTile, out int fromComponent, out int toComponent);
            return (answer, fromComponent, toComponent, spawn);
        });
        Assert.True(unreachable, "a far goal over the unjoined tile is unreachable from the bot's component");
        Assert.True(from >= 0 && from != to, $"bot {from}, goal {to}");
        Assert.Equal(spawnComponent, to);
    }

    [Fact]
    public async Task ACompletedQuestsReturn_IsNeverSkipped()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        PlayerbotWorldDestinations destinations = DestinationsOf(world);
        bool skipped = await world.Host.OnWorldAsync(() => destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 77, 1_000, returning: true, out _));
        Assert.False(skipped);
        Assert.Equal(0, destinations.UnreachableReports);
        Assert.False(destinations.Suspensions!.IsEntrySuspended(6, 1_000));
        Assert.True(await world.Host.OnWorldAsync(() => destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 77, 1_000, returning: false, out _)),
            "the control: the same call outside a return is a skip");
    }

    private static Quest QuestOf(int creature1, uint count1, int creature2, uint count2, uint item, uint itemCount)
        => new(new QuestTemplate
        {
            Entry = 77, Title = "t", ReqCreatureOrGOId1 = creature1, ReqCreatureOrGOCount1 = count1, ReqCreatureOrGOId2 = creature2, ReqCreatureOrGOCount2 = count2,
            ReqItemId1 = item, ReqItemCount1 = itemCount,
        });

    private static bool Other(Quest quest, QuestStatusData status, uint skipped, Func<uint, bool>? suspended = null, uint held = 0, uint[]? droppers = null)
        => PlayerbotQuestObjectives.HasOtherOpenObjective(quest, status, skipped, suspended ?? (_ => false), _ => held, _ => droppers);

    /// <summary>The quest's own open objectives decide (creature, game object and item), whether the bot hunts or travels.</summary>
    [Fact]
    public void TheOtherOpenObjectives_OfAQuest_AreReadFromTheQuestAndTheBotsProgress()
    {
        QuestStatusData status = new() { Status = QuestStatus.Incomplete };
        Quest two = QuestOf(100, 2, 200, 1, 0, 0);
        Assert.True(Other(two, status, 100), "creature 200 is still to kill");
        Assert.False(Other(two, status, 100, suspended: e => e == 200), "creature 200 is set aside too");
        status.CreatureOrGOCount[1] = 1;
        Assert.False(Other(two, status, 100), "creature 200 is done: 100 was the last open objective");
        Assert.True(Other(two, status, 200), "100 is still open");

        Assert.True(Other(QuestOf(100, 2, -5001, 1, 0, 0), new QuestStatusData(), 100), "an open game object objective");

        Quest item = QuestOf(100, 1, 0, 0, 5000, 3);
        status = new QuestStatusData { Status = QuestStatus.Incomplete };
        status.CreatureOrGOCount[0] = 1;
        Assert.True(Other(item, status, 100, held: 0, droppers: null), "an item with no known creature source is still open");
        Assert.True(Other(item, status, 100, held: 0, droppers: [100, 300]), "another creature drops the item");
        Assert.False(Other(item, status, 100, suspended: e => e == 300, held: 0, droppers: [100, 300]), "the other dropper is set aside");
        Assert.False(Other(item, status, 100, held: 3, droppers: [100, 300]), "the item is in the bag");

        Assert.True(PlayerbotQuestObjectives.Serves(item, new QuestStatusData(), 100, _ => 0, _ => null));
        Assert.True(PlayerbotQuestObjectives.Serves(item, status, 300, _ => 0, _ => (uint[])[100, 300]));
        Assert.False(PlayerbotQuestObjectives.Serves(item, status, 300, _ => 3, _ => (uint[])[100, 300]), "the item is in the bag");
        Assert.False(PlayerbotQuestObjectives.Serves(two, status, 999, _ => 0, _ => null));
    }

    [Fact]
    public async Task TheQuestFollowsItsLastEntry_OnlyWithoutAnotherObjective_AndNeverCountsTowardsAbandoning()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        PlayerbotWorldDestinations destinations = DestinationsOf(world);
        PlayerbotSuspensions suspensions = destinations.Suspensions!;
        QuestStatusData status = new() { Status = QuestStatus.Incomplete };
        Quest quest = QuestOf(100, 2, 200, 1, 0, 0);
        destinations.SetQuestAsideUnreachable(77, 1_000, Other(quest, status, 100));
        Assert.False(suspensions.IsQuestSuspended(77, 1_001), "creature 200 is still to kill");
        status.CreatureOrGOCount[1] = 1;
        for (int i = 0; i < 2 * PlayerbotSuspensions.MaxReturnSetAsides; i++) destinations.SetQuestAsideUnreachable(77, 1_000, Other(quest, status, 100));
        Assert.True(suspensions.IsQuestSuspended(77, 1_001));
        Assert.Equal(0, suspensions.QuestSetAsideCount(77));
    }

    // --- the stall path ------------------------------------------------------------------------------------------------------

    private static (int X, int Y, CellTile Tile)[] Pocket(int firstLargeCell) =>
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2], Walkable = (i, j) => i <= 1 && j <= 5 || i >= firstLargeCell }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6] }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] }),
        (11, 11, new CellTile(1, 1, 40, 40, 8, 8, Size: 5) { LinkedSides = [4, 6] }),
    ];

    private async Task<(float X, float Y)> GiveUpAsync(int firstLargeCell, Vector3 at, Vector3 bind, bool expectFragment)
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Pocket(firstLargeCell), new FloorLineOfSight());
        await world.PlaceAsync(at);
        // The control for the pocket: the bot is told it stands on a fragment once the graph and tiles are ready; on the large part it never is.
        for (int think = 0; think < 80 && expectFragment && !await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshFragment(world.Player)); think++)
            await world.Host.World.AdvanceClockAsync(50);
        Assert.Equal(expectFragment, await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshFragment(world.Player)));
        var brain = new PlayerbotBrain(world.Session, new PlayerbotOptions { Enabled = true });
        await world.Host.OnWorldAsync(() =>
        {
            world.Player.Home = new HomeBind(0, 12, bind.X, bind.Y, bind.Z);
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(world.Player, shareable: false);
            return true;
        });
        for (int tick = 0; tick < 100; tick++)
        {
            await world.Host.OnWorldAsync(() => { PlayerbotMovementControl.Update(world.Session, world.Player); return true; });
            await world.Host.World.AdvanceClockAsync(50);
        }

        return (world.Player.X, world.Player.Y);
    }

    [Fact]
    public async Task AStalledFragmentBot_IsMovedToItsBindPoint()
    {
        (float x, _) = await GiveUpAsync(7, new Vector3(5, 5, 0), new Vector3(37, 5, 0), expectFragment: true);
        Assert.True(x > 30f, $"the bot is at x {x}, not at the bind point beside x 37");
    }

    [Fact]
    public async Task AStalledBotOnTheLargePart_IsNotMoved()
    {
        (float x, float y) = await GiveUpAsync(4, new Vector3(30, 5, 0), new Vector3(5, 5, 0), expectFragment: false);
        Assert.True(Vector2.Distance(new(x, y), new(30, 5)) < 1f, $"the bot moved to {x},{y}");
    }

    [Fact]
    public async Task ARepeatedlyStalledBotInTheSameDeadEndPinCell_EscalatesSuspension_RemembersPinCell_AndTeleportsToBindPoint()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Pocket(4), new FloorLineOfSight());
        var pinPos = new Vector3(30, 5, 0);
        var bindPos = new Vector3(5, 5, 0);
        await world.PlaceAsync(pinPos);
        var options = new PlayerbotOptions { Enabled = true };
        var brain = new PlayerbotBrain(world.Session, options);
        await world.Host.OnWorldAsync(() =>
        {
            world.Player.Home = new HomeBind(0, 12, bindPos.X, bindPos.Y, bindPos.Z);
            PlayerbotNavigation.SetStallWatch(world.Player, brain.StallWatch);

            // First stall in cell (3, 0): stays in place, ordinary 10-minute entry suspension (strike 1), not yet a dead-end pin.
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.Equal(1, brain.StallWatch.PinStallCount(0, pinPos.X, pinPos.Y));
            Assert.False(brain.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, world.Host.World.NowMs));
            Assert.Equal(1, brain.Suspensions.EntryStrikeCount(266));
            Assert.False(brain.Suspensions.IsQuestUnreachable(65, world.Host.World.NowMs));

            // Second stall in the same 10-yard cell: marks cell as a dead-end pin, escalates suspension (strike 2 = 2x duration),
            // marks repeated quest unreachable, and triggers Hearthstone/bind recovery off the dead-end pin.
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.Equal(2, brain.StallWatch.PinStallCount(0, pinPos.X, pinPos.Y));
            Assert.True(brain.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, world.Host.World.NowMs));
            Assert.True(PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos));
            Assert.Equal(2, brain.Suspensions.EntryStrikeCount(266));
            Assert.True(brain.Suspensions.IsEntrySuspended(266, world.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1_000));
            Assert.True(brain.Suspensions.IsQuestUnreachable(65, world.Host.World.NowMs));
            return true;
        });

        for (int tick = 0; tick < 100; tick++)
        {
            await world.Host.OnWorldAsync(() => { PlayerbotMovementControl.Update(world.Session, world.Player); return true; });
            await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(Vector2.Distance(new(world.Player.X, world.Player.Y), new(bindPos.X, bindPos.Y)) < 1f,
            $"expected bot at bind point {bindPos}, actual ({world.Player.X},{world.Player.Y})");

        // Route planning back into the remembered dead-end pin cell is refused.
        bool refused = await world.Host.OnWorldAsync(() =>
        {
            TileWait wait = default;
            return PlayerbotNavigation.PlanToward(world.Player, pinPos, options, ref wait, world.Host.World.NowMs, out _) == PlanOutcome.Refused;
        });
        Assert.True(refused, "navigation into a remembered dead-end pin cell must be refused");

        // The pin is remembered for ten minutes, not for good: real thinks over the world clock, and the cell is open again.
        for (int minute = 1; minute <= 9; minute++)
        {
            await world.Host.World.AdvanceClockAsync(60_000);
            await world.Host.OnWorldAsync(() => { world.Session.ManagedBudget = new ManagedActionBudget(1); brain.Update(500); return true; });
        }

        Assert.True(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos)), "about nine minutes on");
        for (int minute = 1; minute <= 2; minute++)
        {
            await world.Host.World.AdvanceClockAsync(60_000);
            await world.Host.OnWorldAsync(() => { world.Session.ManagedBudget = new ManagedActionBudget(1); brain.Update(500); return true; });
        }

        Assert.False(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos)), "past the ten minutes");
    }

    [Fact]
    public async Task RegionLeg_RefusesUnreachableGoal_WhenNoCloserPortalExists()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        // Stand at the northernmost tile (X=20, Y=115) or eastern edge of tile A (X=37, Y=5) closest to UnreachableSpawn (X=75, Y=5):
        // no portal in component A is closer to UnreachableSpawn than (37, 5, 0) already is.
        var nearCliffEdge = new Vector3(37, 5, 0);
        await world.PlaceAsync(nearCliffEdge);
        await WaitUntilUnreachableAsync(world);
        var options = new PlayerbotOptions { Enabled = true, MaxPathPoints = 20 };
        PlanOutcome outcome = await world.Host.OnWorldAsync(() =>
        {
            TileWait wait = default;
            return PlayerbotNavigation.PlanToward(world.Player, UnreachableSpawn, options, ref wait, world.Host.World.NowMs,
                out _, completedQuestReturn: true);
        });
        Assert.Equal(PlanOutcome.Refused, outcome);
    }

    [Fact]
    public async Task SkipUnreachable_EscalatesEntrySuspensionOnSubsequentStrikes()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        await world.PlaceAsync(Start);
        await WaitUntilUnreachableAsync(world);
        PlayerbotWorldDestinations destinations = DestinationsOf(world);
        PlayerbotSuspensions suspensions = destinations.Suspensions!;
        await world.Host.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            Assert.False(destinations.Update(world.Player, 6, 500, preferredQuestId: 788));
            Assert.Equal(1, suspensions.EntryStrikeCount(6));
            Assert.True(suspensions.IsEntrySuspended(6, now + PlayerbotSuspensions.SuspendMs - 1));
            Assert.True(destinations.TryTakeUnreachable(out PlayerbotWorldDestinations.UnreachableSkip firstSkip));
            Assert.Equal(788u, firstSkip.Quest);

            // Second unreachable check after the first 10-minute suspension expires escalates to strike 2 (20 minutes).
            uint afterFirst = now + PlayerbotSuspensions.SuspendMs + 1;
            Assert.True(destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 788, afterFirst, returning: false, out _));
            Assert.Equal(2, suspensions.EntryStrikeCount(6));
            Assert.True(suspensions.IsEntrySuspended(6, afterFirst + PlayerbotSuspensions.SuspendMs + 1_000));
            return true;
        });
    }

    private sealed class Content(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
