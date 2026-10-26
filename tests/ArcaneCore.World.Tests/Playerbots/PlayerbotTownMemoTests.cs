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
        // A frozen clock: the repair plans also stop at 20 ms of wall time into the think (PlayerbotTownGoals, RunningMs), which a loaded
        // machine reaches after the first plan (CI: 13 queries, 1 plan). The count and the query-time budgets are what these tests pin;
        // the query time then stays 0 ticks, which a limit of 0 (the time-budget test) still spends.
        private readonly PlayerbotThinkProbe _probe = new(new PlayerbotThinkProbeTests.ManualClock());

        private RepairHazardWorld(WorldTestHost host, WorldSession session, HazardPathfinder path)
        {
            _host = host;
            _session = session;
            Path = path;
            _goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
        }

        public HazardPathfinder Path { get; }

        /// <param name="hazard">Whether a hazard lies on the way to every repair NPC.</param>
        /// <param name="queryTicksLimit">
        /// The query time a think may spend on repair plans (<see cref="PlayerbotTownGoals.RepairQueryTicksLimit"/>). Unbounded by default,
        /// so a test of the query-count budget does not depend on how fast this machine answers (a loaded one once stopped a think after
        /// 13 queries on the 5 ms budget).
        /// </param>
        public static async Task<RepairHazardWorld> StartAsync(bool hazard, long queryTicksLimit = long.MaxValue)
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
            var world = new RepairHazardWorld(host, session, path);
            world._goals.RepairQueryTicksLimit = queryTicksLimit;
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

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
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
    /// </summary>
    [Fact]
    public async Task RepairTrip_StopsPlanningInAThink_OnceTheQueryBudgetIsSpent_AndGoesOnNextThink()
    {
        await using RepairHazardWorld world = await RepairHazardWorld.StartAsync(hazard: true);
        (int queries, int plans) = await world.ThinkAsync(withProbe: true);
        output.WriteLine($"think 1: {queries} queries, {plans} plans");
        Assert.Equal(2, plans); // 13 queries after the first plan, 26 (over 16) after the second: no third
        Assert.InRange(queries, PlayerbotTownGoals.MaxRepairQueriesPerThink, PlayerbotTownGoals.MaxRepairQueriesPerThink + RefusedPlanQueries);
        (int next, int nextPlans) = await world.ThinkAsync(withProbe: true);
        Assert.True(nextPlans >= 1 && next > 0, "the next think planned nothing");
    }

    /// <summary>
    /// The other budget of the same think: the query TIME. With no time to spend (a limit of 0 ticks, what a loaded machine reaches
    /// after the first plan), the think stops after one plan although it made far fewer queries than the count budget allows
    /// (<see cref="PlayerbotTownGoals.MaxRepairQueriesPerThink"/>), and the next think goes on. This is the outcome that made the
    /// count test above fail once under load; it is now pinned on purpose.
    /// </summary>
    [Fact]
    public async Task RepairTrip_StopsPlanningInAThink_OnceTheQueryTimeBudgetIsSpent_EvenUnderTheCountBudget()
    {
        await using RepairHazardWorld world = await RepairHazardWorld.StartAsync(hazard: true, queryTicksLimit: 0);
        (int queries, int plans) = await world.ThinkAsync(withProbe: true);
        Assert.Equal(1, plans);
        Assert.Equal(RefusedPlanQueries, queries);
        Assert.True(queries < PlayerbotTownGoals.MaxRepairQueriesPerThink, "the count budget must not be what stopped it");
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
