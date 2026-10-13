using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
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
        Assert.Equal(6u, destinations.LastUnreachable!.Value.Entry);
        Assert.NotEqual(destinations.LastUnreachable.Value.FromComponent, destinations.LastUnreachable.Value.ToComponent);

        // The same entry met again inside the window is skipped but not reported again; past the window it is.
        uint now = world.Host.World.NowMs;
        Assert.True(await world.Host.OnWorldAsync(() => destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 0, now + 1_000, returning: false, out _)));
        Assert.Equal(1, destinations.UnreachableReports);
        Assert.True(await world.Host.OnWorldAsync(() =>
            destinations.SkipUnreachable(world.Player, 6, 20001, UnreachableSpawn, 0, now + PlayerbotWorldDestinations.UnreachableReportMs + 1, returning: false, out _)));
        Assert.Equal(2, destinations.UnreachableReports);
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

    [Fact]
    public async Task TheQuestFollowsItsLastEntry_OnlyWithoutAnotherObjective_AndNeverCountsTowardsAbandoning()
    {
        await using SyntheticNavWorld world = await StartAsync(ContentOf(UnreachableSpawn));
        PlayerbotWorldDestinations destinations = DestinationsOf(world);
        PlayerbotSuspensions suspensions = destinations.Suspensions!;
        destinations.SetQuestAsideUnreachable(77, 1_000, questHasOtherObjective: true);
        Assert.False(suspensions.IsQuestSuspended(77, 1_001));
        for (int i = 0; i < 2 * PlayerbotSuspensions.MaxReturnSetAsides; i++) destinations.SetQuestAsideUnreachable(77, 1_000, questHasOtherObjective: false);
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

    private sealed class Content(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
