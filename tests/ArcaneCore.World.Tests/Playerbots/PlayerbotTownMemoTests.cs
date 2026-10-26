using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// What the town controller remembers between thinks so that it does not work the same thing out every half second
/// (<see cref="PlayerbotTownGoals"/>): a class trainer that had nothing for the bot, a refused plan to a visible service NPC, and the
/// repair trip's per-think query budget. Time moves only on the manual world clock.
/// </summary>
public sealed class PlayerbotTownMemoTests(ITestOutputHelper output)
{
    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    /// <summary>A town trainer/vendor 25 yards from the bot (visible, out of interaction range), the bot on the manual clock.</summary>
    private static async Task WithTownNpcAsync(ScriptedPathfinder path, Func<WorldTestHost, WorldSession, Player, PlayerbotTownGoals, Task> body)
    {
        var fixture = new TownVendorFixture();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<ISpellContentStore>(fixture);
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                for (int i = 0; i < 100 && !await host.OnWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid)); i++)
                    await host.World.AdvanceClockAsync(100);
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    Assert.Contains(TownVendorFixture.Guid, player.VisibleObjects);
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: path);
                    player.Relocate(player.X - 25, player.Y, player.Z, player.Orientation, host.World.NowMs);
                    return true;
                });
                await body(host, session, session.Player!, goals);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>
    /// A trainer with nothing for the bot is not asked again until its level changes, it has more money than when it was asked, or
    /// <see cref="PlayerbotTownGoals.TrainerQuoteMemoMs"/> have passed.
    /// </summary>
    [Fact]
    public async Task TrainerWithNothing_IsNotAskedAgain_UntilLevelMoneyOrTime()
    {
        await WithTownNpcAsync(new ScriptedPathfinder(), async (host, session, player, goals) =>
        {
            async Task<int> AskedAfter(Action? change, uint advanceMs = 0)
            {
                if (advanceMs > 0) await host.World.AdvanceClockAsync(advanceMs);
                return await host.OnWorldAsync(() =>
                {
                    change?.Invoke();
                    goals.HasCandidate(player);
                    return goals.TrainerQuotesAsked;
                });
            }

            await host.OnWorldAsync(() => { player.Money = 0; return true; });
            Assert.Equal(1, await AskedAfter(null)); // asked: no money, nothing to learn
            Assert.Equal(1, await AskedAfter(null)); // remembered
            Assert.Equal(1, await AskedAfter(null, 1_000));
            Assert.Equal(1, await AskedAfter(() => player.Money = 0));
            Assert.Equal(2, await AskedAfter(() => player.Money = 100)); // more money than when it was asked: asked again (and it has an offer)
        });
    }

    [Fact]
    public async Task TrainerWithNothing_IsAskedAgain_WhenTheLevelChanges()
    {
        await WithTownNpcAsync(new ScriptedPathfinder(), async (host, session, player, goals) =>
        {
            int first = await host.OnWorldAsync(() => { player.Money = 0; goals.HasCandidate(player); return goals.TrainerQuotesAsked; });
            int same = await host.OnWorldAsync(() => { goals.HasCandidate(player); return goals.TrainerQuotesAsked; });
            Assert.Equal(first, same);
            int changed = await host.OnWorldAsync(() => { player.Level = (byte)(player.Level + 1); goals.HasCandidate(player); return goals.TrainerQuotesAsked; });
            Assert.Equal(first + 1, changed);
        });
    }

    [Fact]
    public async Task TrainerWithNothing_IsAskedAgain_WhenTheMemoExpires()
    {
        await WithTownNpcAsync(new ScriptedPathfinder(), async (host, session, player, goals) =>
        {
            int first = await host.OnWorldAsync(() => { player.Money = 0; goals.HasCandidate(player); return goals.TrainerQuotesAsked; });
            await host.World.AdvanceClockAsync(PlayerbotTownGoals.TrainerQuoteMemoMs - 500);
            Assert.Equal(first, await host.OnWorldAsync(() => { goals.HasCandidate(player); return goals.TrainerQuotesAsked; }));
            await host.World.AdvanceClockAsync(1_000);
            Assert.Equal(first + 1, await host.OnWorldAsync(() => { goals.HasCandidate(player); return goals.TrainerQuotesAsked; }));
        });
    }

    /// <summary>
    /// A plan to a visible service NPC that was refused is not made again for <see cref="PlayerbotTownGoals.VisitRefusedMs"/>: the NPC
    /// stays the target, the think still returns false (so the brain goes on to other goals), and the walk is planned again afterwards.
    /// </summary>
    [Fact]
    public async Task RefusedVisit_IsNotPlannedAgain_ForTheMemo_ThenIs()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => false };
        await WithTownNpcAsync(path, async (host, session, player, goals) =>
        {
            async Task<(bool Result, int Queries)> Think(uint advanceMs)
            {
                if (advanceMs > 0) await host.World.AdvanceClockAsync(advanceMs);
                return await host.OnWorldAsync(() =>
                {
                    player.Money = 100;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    bool result = goals.Update(player, 500);
                    Assert.Equal(TownVendorFixture.Entry, goals.TargetEntry); // the NPC stays the target
                    return (result, path.Queries);
                });
            }

            (bool result, int queries) = await Think(0);
            Assert.False(result);
            Assert.True(queries > 0, "the first think planned nothing");
            (bool again, int same) = await Think(500);
            Assert.False(again);
            Assert.Equal(queries, same); // remembered
            (_, int stillSame) = await Think(PlayerbotTownGoals.VisitRefusedMs - 2_000);
            Assert.Equal(queries, stillSame);
            (bool later, int planned) = await Think(2_500);
            Assert.False(later);
            Assert.True(planned > queries, "the walk was not planned again after the memo ran out");
        });
    }

    /// <summary>Repair NPCs along +x of the start, a death hazard across the way to every one of them, and a pathfinder that refuses every detour.</summary>
    private sealed class RepairHazardWorld : IAsyncDisposable
    {
        private readonly WorldTestHost _host;
        private readonly WorldSession _session;
        private readonly PlayerbotTownGoals _goals;
        private readonly PlayerbotThinkProbe _probe;

        private RepairHazardWorld(WorldTestHost host, WorldSession session, HazardPathfinder path, TimeProvider thinkClock)
        {
            _host = host;
            _session = session;
            Path = path;
            _probe = new PlayerbotThinkProbe(thinkClock);
            _goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
        }

        public HazardPathfinder Path { get; }

        /// <summary>The probe of the last think: its query time (<see cref="PlayerbotThinkProbe.QueryTicks"/>) and running time.</summary>
        public PlayerbotThinkProbe Probe => _probe;

        /// <summary>The query time (Stopwatch ticks) a think of this world may spend on repair plans.</summary>
        public long QueryTicksLimit => _goals.RepairQueryTicksLimit;

        /// <param name="hazard">Whether a hazard lies on the way to every repair NPC.</param>
        /// <param name="queryTicksLimit">
        /// The query time a think may spend on repair plans (<see cref="PlayerbotTownGoals.RepairQueryTicksLimit"/>). Unbounded by default,
        /// so a test of the query-count budget does not depend on how fast this machine answers (a loaded one once stopped a think after
        /// 13 queries on the 5 ms budget). Null keeps the production limit (5 ms).
        /// </param>
        /// <param name="frozenThinkClock">
        /// Whether the think probe runs on a manual clock (<see cref="PlayerbotThinkProbeTests.ManualClock"/>) instead of the system clock
        /// (the default: every test of this world, so none depends on how fast this machine thinks).
        /// It moves only when something calls its Advance(), and the only caller is <see cref="HazardPathfinder"/>, by
        /// <paramref name="queryMs"/> inside each query. With <paramref name="queryMs"/> 0 nothing calls it, so the clock never moves and no
        /// budget read off the think's own time can stop a plan: neither the query time (<see cref="PlayerbotTownGoals.RepairQueryTicksLimit"/>)
        /// nor the think's running time (the 20 ms check, live since the probe's RunningMs replaced ElapsedMs, which read 0 during a think).
        /// On the system clock the first think of a fresh process, JIT and all, ran past 20 ms after one plan on Windows, every time.
        /// </param>
        /// <param name="queryMs">
        /// How long each path query takes on the frozen think clock. The pathfinder advances the clock inside the query, so the time is
        /// charged both to the think's query time and to its running time, exactly, whatever this machine's speed.
        /// </param>
        public static async Task<RepairHazardWorld> StartAsync(bool hazard, long? queryTicksLimit = long.MaxValue, bool frozenThinkClock = true, double queryMs = 0)
        {
            var items = new ItemTestContent();
            items.Templates.Templates.Add(new ItemTemplate
            {
                Entry = 9212, Name = "boots", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
                InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = 20, ItemLevel = 10, Quality = 1, Stackable = 1,
                AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
            });
            using IDisposable scope = items.Use();
            ManyRepairServices.Current.Value = new ManyRepairFixture();
            var path = new HazardPathfinder();
            WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            if (queryMs != 0 && !frozenThinkClock) throw new ArgumentException("a query time needs the frozen think clock", nameof(queryMs));
            PlayerbotThinkProbeTests.ManualClock? clock = frozenThinkClock ? new PlayerbotThinkProbeTests.ManualClock() : null;
            if (clock is not null && queryMs > 0) path.OnQuery = () => clock.Advance(queryMs);
            var world = new RepairHazardWorld(host, session, path, clock ?? TimeProvider.System);
            if (queryTicksLimit is { } limit) world._goals.RepairQueryTicksLimit = limit;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: path);
                PlayerbotEquipmentTests.EquipFromBags(player, 9212);
                player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                player.Money = 100;
                path.Origin = new Vector3(player.X, player.Y, player.Z);
                if (hazard)
                {
                    // What the brain's risk keeps for a bot: a place to keep out of, on the way to every repair NPC; the brain guards every route with it.
                    var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
                    brain.Risk.Hazards.Add(player.MapId, path.Origin + new Vector3(60, 0, 0), 25f, host.World.NowMs, 3_600, "death");
                    PlayerbotNavigation.Guard(player, brain.Risk);
                }

                return true;
            });
            return world;
        }

        /// <summary>One think, half a second after the last: (queries made, repair NPCs planned to).</summary>
        public async Task<(int Queries, int Plans)> ThinkAsync(bool withProbe)
        {
            await _host.World.AdvanceClockAsync(500);
            return await _host.OnWorldAsync(() =>
            {
                Player player = _session.Player!;
                Path.Directs.Clear();
                PlayerbotMotion.ElapseForTests(player, 500);
                _session.ManagedBudget = new ManagedActionBudget(1);
                if (withProbe) _probe.Begin();
                _goals.BeginThink();
                _goals.Update(player, 500);
                int queries = 0;
                if (withProbe) { _probe.End(); queries = _probe.Queries; }
                return (queries, Path.Directs.Count);
            });
        }

        public async ValueTask DisposeAsync()
        {
            _session.Kick();
            await _session.ManagedClosed;
            await _host.DisposeAsync();
            ManyRepairServices.Current.Value = null;
        }
    }

    /// <summary>The start answers (the bot is on the mesh, a straight route from where it stands); every query from anywhere else is refused.</summary>
    internal sealed class HazardPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public Vector3 Origin { get; set; }
        public List<Vector3> Directs { get; } = [];

        /// <summary>Runs inside every query (a test advances the think probe's manual clock there: the query's time).</summary>
        public Action? OnQuery { get; set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            OnQuery?.Invoke();
            if (start == end) return PathResult.StraightLine(start, end, PathType.Normal);
            if (Vector3.Distance(start, Origin) > 0.5f) return PathResult.None(start);
            if (Math.Abs(end.Y - Origin.Y) < 0.01f && end.X >= ScriptedPathfinder.SpawnX(0) - 0.01f) Directs.Add(end);
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    /// <summary>
    /// The queries one refused plan makes on <see cref="HazardPathfinder"/>: its direct query, then for each hazard detour offset
    /// (<see cref="PlayerbotNavigation.HazardDetourOffsets"/>, 5, 15 and 28 yards) on either side of the hazard the corner before it,
    /// answered from the start, and the leg past it, refused. Thirteen (nine while the detours had two offsets).
    /// </summary>
    private static int RefusedPlanQueries => 1 + (PlayerbotNavigation.HazardDetourOffsets * 2 * 2);

    /// <summary>
    /// The repair trip stops planning to further NPCs in a think once the think has made <see cref="PlayerbotTownGoals.MaxRepairQueriesPerThink"/>
    /// queries, and the next think goes on. Each refused plan here runs the hazard detours (<see cref="RefusedPlanQueries"/>).
    /// The managed feature runs every think inside a <see cref="PlayerbotThinkProbe"/> (ManagedPlayerbotFeature.Think), the budget's source.
    /// The think's clock is frozen so the count is the only budget that can stop it (the query-time and running-time budgets each have
    /// their own test below).
    /// </summary>
    [Fact]
    public async Task RepairTrip_StopsPlanningInAThink_OnceTheQueryBudgetIsSpent_AndGoesOnNextThink()
    {
        await using RepairHazardWorld world = await RepairHazardWorld.StartAsync(hazard: true, frozenThinkClock: true);
        (int queries, int plans) = await world.ThinkAsync(withProbe: true);
        output.WriteLine($"think 1: {queries} queries, {plans} plans");
        Assert.Equal(2, plans); // 13 queries after the first plan, 26 (over 16) after the second: no third
        Assert.InRange(queries, PlayerbotTownGoals.MaxRepairQueriesPerThink, PlayerbotTownGoals.MaxRepairQueriesPerThink + RefusedPlanQueries);
        (int next, int nextPlans) = await world.ThinkAsync(withProbe: true);
        Assert.True(nextPlans >= 1 && next > 0, "the next think planned nothing");
    }

    /// <summary>
    /// The other budget of the same think: the query TIME. On the frozen think clock each query takes 0.5 ms, so the first plan's 13
    /// queries spend 6.5 ms, over the production limit of 5 ms (what a loaded machine reaches after the first plan). The count
    /// (<see cref="PlayerbotTownGoals.MaxRepairQueriesPerThink"/>) and the 20 ms running time are both unspent, so the query time alone
    /// stops the think after one plan, and the next think goes on. This is the outcome that once made the count test above fail under
    /// load; it is now pinned on purpose.
    /// </summary>
    [Fact]
    public async Task RepairTrip_StopsPlanningInAThink_OnceTheQueryTimeBudgetIsSpent_EvenUnderTheCountBudget()
    {
        await using RepairHazardWorld world = await RepairHazardWorld.StartAsync(hazard: true, queryTicksLimit: null, frozenThinkClock: true, queryMs: 0.5);
        (int queries, int plans) = await world.ThinkAsync(withProbe: true);
        output.WriteLine($"think 1: {queries} queries, {plans} plans, {world.Probe.QueryTicks} of {world.QueryTicksLimit} query ticks, {world.Probe.RunningMs} ms");
        Assert.Equal(1, plans);
        Assert.Equal(RefusedPlanQueries, queries);
        Assert.True(queries < PlayerbotTownGoals.MaxRepairQueriesPerThink, "the count budget must not be what stopped it");
        Assert.True(world.Probe.QueryTicks >= world.QueryTicksLimit, "the query time was not spent");
        Assert.True(world.Probe.RunningMs < 20.0, "the running-time budget must not be what stopped it");
        (int next, int nextPlans) = await world.ThinkAsync(withProbe: true);
        Assert.True(nextPlans >= 1 && next > 0, "the next think planned nothing");
    }

    /// <summary>
    /// The third budget of the same think: its running TIME, the 20 ms that the repair loop in <see cref="PlayerbotTownGoals"/> reads
    /// off <see cref="PlayerbotThinkProbe.RunningMs"/>. On the frozen think clock each query takes 2 ms, so the first plan's 13 queries
    /// run the think to 26 ms. The count (<see cref="PlayerbotTownGoals.MaxRepairQueriesPerThink"/>) and the query time (limited to a
    /// whole second here) are both unspent, so the running time alone cuts the second plan, and the next think goes on. This is the shape
    /// of every first think on Windows on the system clock (JIT and all), pinned on purpose.
    /// </summary>
    [Fact]
    public async Task RepairTrip_StopsPlanningInAThink_OnceTheThinkRunsPast20Ms_EvenUnderTheCountAndQueryTimeBudgets()
    {
        long second = new PlayerbotThinkProbeTests.ManualClock().TicksFor(1_000);
        await using RepairHazardWorld world = await RepairHazardWorld.StartAsync(hazard: true, queryTicksLimit: second, frozenThinkClock: true, queryMs: 2);
        (int queries, int plans) = await world.ThinkAsync(withProbe: true);
        output.WriteLine($"think 1: {queries} queries, {plans} plans, {world.Probe.QueryTicks} of {world.QueryTicksLimit} query ticks, {world.Probe.RunningMs} ms");
        Assert.Equal(1, plans);
        Assert.Equal(RefusedPlanQueries, queries);
        Assert.True(queries < PlayerbotTownGoals.MaxRepairQueriesPerThink, "the count budget must not be what stopped it");
        Assert.True(world.Probe.QueryTicks < world.QueryTicksLimit, "the query-time budget must not be what stopped it");
        Assert.True(world.Probe.RunningMs >= 20.0, "the think did not run past 20 ms");
        (int next, int nextPlans) = await world.ThinkAsync(withProbe: true);
        Assert.True(nextPlans >= 1 && next > 0, "the next think planned nothing");
    }

    /// <summary>Off a think (no probe: a test, a tool) there is nothing to count and the plan bound alone holds.</summary>
    [Fact]
    public async Task RepairTrip_WithoutAProbe_PlansTheFullBound()
    {
        await using RepairHazardWorld world = await RepairHazardWorld.StartAsync(hazard: true);
        (_, int plans) = await world.ThinkAsync(withProbe: false);
        Assert.Equal(PlayerbotTownGoals.MaxRepairPlansPerThink, plans);
    }

    /// <summary>
    /// The live shape (live-w34-r1: 114 path queries per slow town think, refused plans running their fallbacks), on the production
    /// set-up: a guarded bot, a hazard on the way to every repair NPC, every detour refused. Seven minutes of game time: no think
    /// makes more than a plan past the budget (before: all three plans), and the rests between empty rounds grow (at most 50 refused
    /// plans).
    /// </summary>
    [Fact]
    public async Task RepairTrip_RefusedPlansWithHazardDetours_StayWithinTheThinkBudget()
    {
        await using RepairHazardWorld world = await RepairHazardWorld.StartAsync(hazard: true);
        int worst = 0, total = 0, thinks = 840;
        for (int i = 0; i < thinks; i++)
        {
            (int queries, _) = await world.ThinkAsync(withProbe: true);
            worst = Math.Max(worst, queries);
            total += queries;
        }

        output.WriteLine($"BENCH repair-trip hazards: {thinks} thinks, {total} queries ({(double)total / thinks:F2}/think), worst think {worst}");
        Assert.True(worst <= PlayerbotTownGoals.MaxRepairQueriesPerThink + RefusedPlanQueries, $"a think made {worst} queries");
        Assert.True(total <= 50 * RefusedPlanQueries, $"{total} queries in {thinks} thinks");
    }
}
