using System.Numerics;
using System.Reflection;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// A cooking recipe needs a cooking fire (spell focus 4) within the fire's reach (vmangos Spell::CheckItems, Spell.cpp:7233-7246, with
/// GameObjectFocusCheck, GridNotifiers.h:586-606). The bot casts only with one in reach, lights a Basic Campfire (spell 818, reagent Simple
/// Wood 4470) when it can, and otherwise sets the recipe aside for ten minutes without an anomaly per think.
/// </summary>
[Collection(PlayerbotCraftingCollection.Name)]
public sealed class PlayerbotCraftingSpellFocusTests
{
    private const uint Reagent = 99601;
    private const uint Product = 99602;
    private const uint Recipe = 99610;
    private const uint Wood = 4470;
    private const uint Campfire = 818;
    private const uint FireEntry = 29784;

    /// <summary>A fishing hole whose data0 is 4 as well: the same number in another kind of object is not a cooking fire.</summary>
    private const uint FishingHoleEntry = 29785;
    private const uint FlintAndTinder = 4471;

    private static readonly string CraftFailed = PlayerbotAnomalies.Key("progression", "craft-failed");

    private sealed class Objects : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            var data = new uint[GameObjectTemplate.DataCount];
            data[0] = 4;
            data[1] = 10;
            var fire = new GameObjectTemplate { Entry = FireEntry, Type = (uint)GameObjectType.SpellFocus, DisplayId = 1, Name = "Basic Campfire", Data = data };
            var holeData = new uint[GameObjectTemplate.DataCount];
            holeData[0] = 4;
            holeData[1] = 10;
            var hole = new GameObjectTemplate { Entry = FishingHoleEntry, Type = (uint)GameObjectType.FishingHole, DisplayId = 1, Name = "Test Fishing Hole", Data = holeData };
            return Task.FromResult(new GameObjectContent([fire, hole], [], [], [], []));
        }
    }

    private static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = Reagent, Name = "Raw Test Meat", Class = 7, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = Product, Name = "Charred Test Meat", Class = 0, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = Wood, Name = "Simple Wood", Class = 7, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = FlintAndTinder, Name = "Flint and Tinder", Class = 7, Quality = 1, Stackable = 20 },
        ]);
        return items;
    }

    private static SpellInfo Cook() => new()
    {
        Id = Recipe,
        Name = "Test Cooking",
        RequiresSpellFocus = 4,
        Reagents = [new SpellReagent(Reagent, 1)],
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = Product, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    /// <summary>The ten second cast of Basic Campfire (SpellCastTimes.dbc index 7); a test advances the world clock past it.</summary>
    private const uint CampfireCastMs = 10_000;

    /// <summary>
    /// Spell.dbc row 818 of build 5875 as the reviewer dumped it: effect 1 TRANS_DOOR (50) of gameobject 29784, tool (totem) Flint and Tinder
    /// 4471, reagent Simple Wood 4470, cast time index 7 (10 s), duration index 6 (10 min), range index 1 (self), implicit target A 32
    /// (LocationUnitMinionPosition), radius index 15 (3 yd). The DBC-gated test checks these fields against the real file.
    /// </summary>
    private static SpellInfo BasicCampfire(uint manaCost = 0) => new()
    {
        Id = Campfire,
        Name = "Basic Campfire",
        ManaCost = manaCost,
        CastTime = new SpellCastTime((int)CampfireCastMs, 0, (int)CampfireCastMs),
        Duration = new SpellDuration(600_000, 0, 600_000),
        RangeIndex = 1,
        Totems = [FlintAndTinder],
        Reagents = [new SpellReagent(Wood, 1)],
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.TransDoor, MiscValue = (int)FireEntry, TargetA = SpellImplicitTarget.LocationUnitMinionPosition, Radius = 3f },
            new(),
            new(),
        ],
    };

    private static SpellInfo Summoning(SpellEffectName effect, uint entry) => new()
    {
        Id = Campfire,
        Name = "Test Summoner",
        Effects = [new SpellEffectInfo { Effect = effect, MiscValue = (int)entry }, new(), new()],
    };

    private static async Task RunAsync(bool knowsCampfire, uint woodCount, Func<WorldTestHost, WorldSession, PlayerbotCrafting, Player, Task> body, bool flint = true, uint campfireMana = 0, bool manualClock = false, SpellInfo? campfire = null)
    {
        using IDisposable scope = Items().Use();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s =>
        {
            s.AddScoped<IGameObjectDataStore>(_ => new Objects());
            if (manualClock)
                s.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var crafting = new PlayerbotCrafting(session, new PlayerbotOptions { Enabled = true });
            Player? player = null;
            await host.OnWorldAsync(() =>
            {
                player = session.Player!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => s.Id is not Recipe and not Campfire), Cook(), campfire ?? BasicCampfire(campfireMana)], [], []);
                Assert.True(spells.Spellbook.LearnSpell(player, Recipe));
                if (knowsCampfire)
                    Assert.True(spells.Spellbook.LearnSpell(player, Campfire));
                Assert.True(player.Inventory.AddItem(Reagent, 3, out _) == InventoryResult.Ok);
                if (woodCount > 0)
                    Assert.True(player.Inventory.AddItem(Wood, woodCount, out _) == InventoryResult.Ok);
                if (flint)
                    Assert.True(player.Inventory.AddItem(FlintAndTinder, 1, out _) == InventoryResult.Ok);
                return true;
            });
            await body(host, session, crafting, player!);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// With spell batching on (ARCANECORE_TEST_SPELL_BATCH_MS), a craft that went off waits in the batch for its go: the world runs on to the
    /// batch boundary before the bags are read. Without batching nothing is held and this returns at once.
    /// </summary>
    private static Task SpellBatchGoneAsync(WorldTestHost host, Player player)
        => host.WaitForWorldAsync(() => !host.WorldServices.GetRequiredService<SpellFeature>().System.HasPendingGo(player), "the batched craft went off");

    /// <summary>The spell batch window of the world's spell system (0 without batching).</summary>
    private static uint SpellBatchWindowMs(WorldTestHost host) => host.WorldServices.GetRequiredService<SpellFeature>().System.SpellBatchWindowMs;

    private static bool Step(WorldSession session, PlayerbotCrafting crafting, Player player)
    {
        session.ManagedBudget = new ManagedActionBudget(5);
        return crafting.Update(player, 500);
    }

    private static Task<bool> StepAsync(WorldTestHost host, WorldSession session, PlayerbotCrafting crafting, Player player)
        => host.OnWorldAsync(() => Step(session, crafting, player));

    private static Task<int> FiresAsync(WorldTestHost host, Player player)
        => host.OnWorldAsync(() => Objs(host, player).GameObjects.Count(g => g.Template.Entry == FireEntry));

    private static Task<uint> CountAsync(WorldTestHost host, Player player, uint item)
        => host.OnWorldAsync(() => player.Inventory.GetItemCount(item));

    /// <summary>Casts the campfire (the first think) and lets the world run past its ten second cast, as the server would.</summary>
    private static async Task LightAndWaitAsync(WorldTestHost host, WorldSession session, PlayerbotCrafting crafting, Player player)
    {
        Assert.True(await StepAsync(host, session, crafting, player));
        await host.World.AdvanceClockAsync(CampfireCastMs + 500);
    }

    /// <summary>Puts a cooking fire <paramref name="dx"/> yards east and <paramref name="dz"/> up from the bot and runs the world until the bot sees it.</summary>
    private static async Task SummonVisibleAsync(WorldTestHost host, Player player, float dx, float dz)
    {
        ObjectGuid guid = await host.OnWorldAsync(() => Objs(host, player).Summon(FireEntry, player.X + dx, player.Y, player.Z + dz, 0)!.Guid);
        Assert.True(await host.World.AdvanceClockUntilAsync(10_000, () => player.VisibleObjects.Contains(guid)), "the fire never became visible");
    }

    private static GameObjectMapSystem Objs(WorldTestHost host, Player player)
        => host.WorldServices.GetRequiredService<GameObjectLootFeature>().FindSystem(player.Map!)!;

    [Fact]
    public async Task NoFireNearby_AndNoWood_TheBotDoesNotCast_AndDoesNotSpamTheAnomaly()
    {
        await RunAsync(knowsCampfire: true, woodCount: 0, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            for (int i = 0; i < 5; i++)
                Assert.False(Step(session, crafting, player));

            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));
            Assert.Equal(0u, player.Inventory.GetItemCount(Product));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            return true;
        }));
    }

    [Fact]
    public async Task NoFireNearby_TheBotDoesNotRetryTheRecipeEveryThink_EvenWhenAFireAppearsDuringTheBackoff()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            Assert.False(Step(session, crafting, player));
            GameObject? fire = Objs(host, player).Summon(FireEntry, player.X, player.Y, player.Z, 0);
            Assert.NotNull(fire);

            // Set aside for ten minutes: the fire that lit up a moment later is not looked at until the backoff has run out.
            Assert.False(Step(session, crafting, player));
            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));
            return true;
        }));
    }

    [Fact]
    public async Task BotNextToACookingFire_Cooks()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                Assert.NotNull(Objs(host, player).Summon(FireEntry, player.X + 2f, player.Y, player.Z, 0));

                Assert.True(Step(session, crafting, player));
                return true;
            });
            await SpellBatchGoneAsync(host, player);
            await host.OnWorldAsync(() =>
            {
                Assert.Equal(2u, player.Inventory.GetItemCount(Reagent));
                Assert.Equal(1u, player.Inventory.GetItemCount(Product));
                Assert.False(capture.Any(CraftFailed), capture.Describe());
                return true;
            });
        });
    }

    [Fact]
    public async Task BotWithWoodAndTheCampfireSpell_LightsACampfire_ThenCooks()
    {
        await RunAsync(knowsCampfire: true, woodCount: 2, async (host, session, crafting, player) =>
        {
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));

            // The cast takes ten seconds: until then no wood is spent and no fire stands.
            Assert.True(await StepAsync(host, session, crafting, player));
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            Assert.Equal(0, await FiresAsync(host, player));

            await host.World.AdvanceClockAsync(CampfireCastMs / 2);
            Assert.True(await StepAsync(host, session, crafting, player));
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            Assert.Equal(0, await FiresAsync(host, player));

            await host.World.AdvanceClockAsync(CampfireCastMs / 2 + 500);
            Assert.Equal(1u, await CountAsync(host, player, Wood));
            Assert.Equal(1, await FiresAsync(host, player));
            Assert.Equal(3u, await CountAsync(host, player, Reagent));

            Assert.True(await StepAsync(host, session, crafting, player));
            await host.World.AdvanceClockAsync(500);
            Assert.Equal(2u, await CountAsync(host, player, Reagent));
            Assert.Equal(1u, await CountAsync(host, player, Product));
            Assert.Equal(1u, await CountAsync(host, player, Wood));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
        }, manualClock: true);
    }

    [Fact]
    public async Task BotWithoutTheCampfireSpell_DoesNotBurnItsWood()
    {
        await RunAsync(knowsCampfire: false, woodCount: 2, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            Assert.False(Step(session, crafting, player));
            Assert.Equal(2u, player.Inventory.GetItemCount(Wood));
            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));
            return true;
        }));
    }

    [Fact]
    public async Task BackoffEnds_AfterTenMinutes_AndTheBotThenCooksAtTheFireThatAppearedMeanwhile()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            Assert.False(await host.OnWorldAsync(() => Step(session, crafting, player)));
            await host.OnWorldAsync(() => Objs(host, player).Summon(FireEntry, player.X + 2f, player.Y, player.Z, 0));
            await host.World.AdvanceClockAsync(60_000);
            Assert.False(await host.OnWorldAsync(() => Step(session, crafting, player)));
            await host.World.AdvanceClockAsync(560_000);
            Assert.True(await host.OnWorldAsync(() => Step(session, crafting, player)));
            await host.World.AdvanceClockAsync(200 + SpellBatchWindowMs(host)); // past the go, which spell batching holds to the boundary
            Assert.Equal(1u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(Product)));
        }, manualClock: true);
    }

    [Fact]
    public async Task BotWithoutFlintAndTinder_DoesNotLightACampfire()
    {
        await RunAsync(knowsCampfire: true, woodCount: 2, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.False(Step(session, crafting, player));
            Assert.Equal(2u, player.Inventory.GetItemCount(Wood));
            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            return true;
        }), flint: false);
    }

    [Fact]
    public async Task ACampfireTheServerRefuses_IsSetAsideInSilence_AndNotCastAgain()
    {
        // 100000 mana: the server's own check refuses the cast although the bot holds the tool and the wood.
        await RunAsync(knowsCampfire: true, woodCount: 2, async (host, session, crafting, player) =>
        {
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await LightAndWaitAsync(host, session, crafting, player);
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            Assert.Equal(0, await FiresAsync(host, player));

            // Set aside: the next think neither casts nor takes any managed action, and a second ten seconds change nothing.
            Assert.False(await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(5);
                bool acted = crafting.Update(player, 500);
                Assert.Equal(5, session.ManagedBudget.Remaining);
                return acted;
            }));
            await host.World.AdvanceClockAsync(CampfireCastMs + 500);
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            Assert.Equal(0, await FiresAsync(host, player));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
        }, campfireMana: 100_000, manualClock: true);
    }

    [Fact]
    public async Task OneWoodPerFire_TheBotCooksSeveralTimesAtTheFireItLit_AndDoesNotRelight()
    {
        await RunAsync(knowsCampfire: true, woodCount: 3, async (host, session, crafting, player) =>
        {
            await LightAndWaitAsync(host, session, crafting, player);
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            for (int i = 0; i < 3; i++)
            {
                Assert.True(await StepAsync(host, session, crafting, player));
                await host.World.AdvanceClockAsync(500);
            }

            Assert.Equal(0u, await CountAsync(host, player, Reagent));
            Assert.Equal(3u, await CountAsync(host, player, Product));
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            Assert.Equal(1, await FiresAsync(host, player));
        }, manualClock: true);
    }

    [Fact]
    public async Task AnUnreachableFire_IsReportedOnce_AndRetriedOnlyAfterTenMinutes()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            // The test host has no navigation data, so the planner refuses every walk: a fire beyond its reach is an unreachable one.
            await SummonVisibleAsync(host, player, 20f, 0f);
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            for (int i = 0; i < 4; i++)
            {
                await host.OnWorldAsync(() => Step(session, crafting, player));
                await host.World.AdvanceClockAsync(500);
            }

            Assert.Equal(1, capture.Count(CraftFailed));
            Assert.Contains("unreachable", capture.Notices.Single(n => n.Key == CraftFailed).Detail, StringComparison.Ordinal);

            await host.World.AdvanceClockAsync(600_000);
            await host.OnWorldAsync(() => Step(session, crafting, player));
            Assert.Equal(2, capture.Count(CraftFailed));
        }, manualClock: true);
    }

    /// <summary>
    /// On the live server 181 bots retried the same unreachable fire every ten minutes for days (14k craft-failed reports): each failure in a
    /// row now doubles the wait (10, 20, 40 minutes ...), so a fire the planner never reaches is not walked to and reported again and again.
    /// </summary>
    [Fact]
    public async Task AnUnreachableFire_BacksOffLongerEachTimeItFailsInARow()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            await SummonVisibleAsync(host, player, 20f, 0f);
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));

            await host.OnWorldAsync(() => Step(session, crafting, player));
            Assert.Equal(1, capture.Count(CraftFailed));

            await host.World.AdvanceClockAsync(600_000); // first backoff: ten minutes
            await host.OnWorldAsync(() => Step(session, crafting, player));
            Assert.Equal(2, capture.Count(CraftFailed));

            await host.World.AdvanceClockAsync(600_000); // second backoff: twenty minutes, only ten have passed
            await host.OnWorldAsync(() => Step(session, crafting, player));
            Assert.Equal(2, capture.Count(CraftFailed));

            await host.World.AdvanceClockAsync(600_500);
            await host.OnWorldAsync(() => Step(session, crafting, player));
            Assert.Equal(3, capture.Count(CraftFailed));

            await host.World.AdvanceClockAsync(1_800_000); // third backoff: forty minutes, only thirty have passed
            await host.OnWorldAsync(() => Step(session, crafting, player));
            Assert.Equal(3, capture.Count(CraftFailed));
        }, manualClock: true);
    }

    private sealed class UnusedPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }

    /// <summary>
    /// Simulates a cooking fire inside a firepit or tent whose center lies 7.5 yards past the walkable mesh edge
    /// (<paramref name="meshEdgeX"/>): a query to the center itself is outside <c>NearExtents</c> (3 yd) and returns
    /// <see cref="PathType.NoPath"/>, while a query to the focus-reach ring within 3 yards of the mesh edge returns an
    /// <see cref="PathType.Incomplete"/> path ending at <paramref name="meshEdgeX"/> (7.5 yards from the fire's center,
    /// inside its 10-yard <c>data1</c> spell-focus reach).
    /// </summary>
    private sealed class OffMeshFireCenterPathfinder(float meshEdgeX) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (end.X > meshEdgeX + 3f)
                return PathResult.StraightLine(start, end, PathType.NoPath);

            Vector3 clamped = new(MathF.Min(end.X, meshEdgeX), end.Y, end.Z);
            PathType type = end.X > meshEdgeX ? PathType.Incomplete : PathType.Normal;
            return PathResult.StraightLine(start, clamped, type);
        }
    }

    [Fact]
    public async Task AFireWhoseCenterIsOffMesh_ButWhoseTenYardFocusRadiusIsReachable_IsWalkedToAndCookedAt()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: new OffMeshFireCenterPathfinder(player.X + 4.5f));
                return true;
            });
            await SummonVisibleAsync(host, player, 12f, 0f);
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));

            for (int i = 0; i < 6 && await CountAsync(host, player, Product) == 0; i++)
            {
                Assert.True(await StepAsync(host, session, crafting, player));
                await host.World.AdvanceClockAsync(500);
            }

            Assert.Equal(2u, await CountAsync(host, player, Reagent));
            Assert.Equal(1u, await CountAsync(host, player, Product));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
        }, manualClock: true);
    }

    /// <summary>Hands the crafting goal a route it is "already walking" to the fire, the way the brain tests inject a <c>PlayerbotRoute</c>.</summary>
    private static void InjectRoute(PlayerbotCrafting crafting, PlayerbotRoute route, ObjectGuid target)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PlayerbotCrafting).GetField("_route", flags)!.SetValue(crafting, route);
        typeof(PlayerbotCrafting).GetField("_routeTarget", flags)!.SetValue(crafting, target);
    }

    [Fact]
    public async Task ARouteThatRanToItsEnd_ButLeavesTheBotOutsideTheFocusRange_IsReportedAsWalkedButOutOfRange()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            // The test host has no navmesh, so no real route can be planned: the route the bot "walked" is handed in, finished (the next
            // point is past the last), while the bot stands 20 yards from a fire whose reach is 10.
            ObjectGuid fire = await host.OnWorldAsync(() => Objs(host, player).Summon(FireEntry, player.X + 20f, player.Y, player.Z, 0)!.Guid);
            Assert.True(await host.World.AdvanceClockUntilAsync(10_000, () => player.VisibleObjects.Contains(fire)), "the fire never became visible");
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                var here = new Vector3(player.X, player.Y, player.Z);
                var route = new PlayerbotRoute([here, here + new Vector3(1f, 0f, 0f)], 1f) { NextPoint = 2 };
                Assert.True(route.Complete);
                InjectRoute(crafting, route, fire);
                return true;
            });

            Assert.False(await StepAsync(host, session, crafting, player));

            Assert.Equal(1, capture.Count(CraftFailed));
            string detail = capture.Notices.Single(n => n.Key == CraftFailed).Detail;
            Assert.Contains("walked to spell focus 4", detail, StringComparison.Ordinal);
            Assert.Contains("still out of its range", detail, StringComparison.Ordinal);

            // And it is set aside: the next think does not report it again.
            await StepAsync(host, session, crafting, player);
            Assert.Equal(1, capture.Count(CraftFailed));
        }, manualClock: true);
    }

    [Fact]
    public async Task ARouteThatEndedEarly_AStaleOne_IsSetAsideWithoutTheWalkedButOutOfRangeAnomaly()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            ObjectGuid fire = await host.OnWorldAsync(() => Objs(host, player).Summon(FireEntry, player.X + 20f, player.Y, player.Z, 0)!.Guid);
            Assert.True(await host.World.AdvanceClockUntilAsync(10_000, () => player.VisibleObjects.Contains(fire)), "the fire never became visible");
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                // Planned on a pathfinder the map no longer uses: TryAdvance drops it with the route unfinished.
                var here = new Vector3(player.X, player.Y, player.Z);
                var route = new PlayerbotRoute([here, here + new Vector3(15f, 0f, 0f)], 15f) { Pathfinder = new UnusedPathfinder() };
                Assert.False(route.Complete);
                InjectRoute(crafting, route, fire);
                return true;
            });

            Assert.False(await StepAsync(host, session, crafting, player));

            Assert.False(capture.Any(CraftFailed), capture.Describe());
            Assert.Equal(3u, await CountAsync(host, player, Reagent));
        }, manualClock: true);
    }

    [Fact]
    public async Task ProvidesSpellFocus_NeedsASpellFocusObject_OfTheAskedFocusId()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotCrafting.ProvidesSpellFocus(player, BasicCampfire(), 4));
            Assert.True(PlayerbotCrafting.ProvidesSpellFocus(player, Summoning(SpellEffectName.SummonObjectWild, FireEntry), 4));
            Assert.False(PlayerbotCrafting.ProvidesSpellFocus(player, BasicCampfire(), 3));

            // A fishing hole carries data0 = 4 as well, but it is no spell focus: it must not pass for a cooking fire.
            Assert.False(PlayerbotCrafting.ProvidesSpellFocus(player, Summoning(SpellEffectName.TransDoor, FishingHoleEntry), 4));
            Assert.False(PlayerbotCrafting.ProvidesSpellFocus(player, Summoning(SpellEffectName.SummonObjectWild, FishingHoleEntry), 4));

            // An unknown template, no template id, and an effect that summons nothing.
            Assert.False(PlayerbotCrafting.ProvidesSpellFocus(player, Summoning(SpellEffectName.TransDoor, 99999), 4));
            Assert.False(PlayerbotCrafting.ProvidesSpellFocus(player, Summoning(SpellEffectName.TransDoor, 0), 4));
            Assert.False(PlayerbotCrafting.ProvidesSpellFocus(player, Cook(), 4));
            return true;
        }));
    }

    [OptionalDbcFact]
    public async Task TheRealSpellDbcRecordOfBasicCampfire_IsOneTheBotRecognisesAsLightingACookingFire()
    {
        string dir = Environment.GetEnvironmentVariable(OptionalDbcFactAttribute.Variable)!;
        SpellDbcContent dbc = SpellDbcImporter.ReadDirectory(dir);
        SpellTemplateRow row = dbc.Spells.Single(s => s.Id == Campfire);
        SpellInfo real = SpellStoreFactory.ToSpellInfo(
            row,
            dbc.CastTimes.ToDictionary(r => r.Id), dbc.Durations.ToDictionary(r => r.Id),
            dbc.Ranges.ToDictionary(r => r.Id), dbc.Radii.ToDictionary(r => r.Id));
        Assert.Equal(SpellEffectName.TransDoor, real.Effects[0].Effect);
        Assert.Equal((int)FireEntry, real.Effects[0].MiscValue);
        Assert.Equal(FlintAndTinder, real.Totems[0]);
        Assert.Equal(Wood, real.Reagents[0].ItemId);

        // The shared fixture models this same record: cast time, duration, range, target and radius.
        SpellInfo fixture = BasicCampfire();
        Assert.Equal(fixture.CastTime, real.CastTime);
        Assert.Equal((int)CampfireCastMs, real.GetCastTime(1));
        Assert.Equal(fixture.Duration, real.Duration);
        Assert.Equal(fixture.RangeIndex, real.RangeIndex);
        Assert.Equal(fixture.Effects[0].TargetA, real.Effects[0].TargetA);
        Assert.Equal(fixture.Effects[0].Radius, real.Effects[0].Radius);

        // The real record, cast end to end: the bot lights the fire from it, waits out the 10 s cast and cooks at the fire it made.
        await RunAsync(knowsCampfire: true, woodCount: 2, async (host, session, crafting, player) =>
        {
            Assert.True(await host.OnWorldAsync(() => PlayerbotCrafting.ProvidesSpellFocus(player, real, 4)));
            Assert.False(await host.OnWorldAsync(() => PlayerbotCrafting.ProvidesSpellFocus(player, real, 3)));
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));

            Assert.True(await StepAsync(host, session, crafting, player));
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            await host.World.AdvanceClockAsync(CampfireCastMs - 1_000);
            Assert.Equal(2u, await CountAsync(host, player, Wood));
            await host.World.AdvanceClockAsync(1_500);
            Assert.Equal(1u, await CountAsync(host, player, Wood));
            Assert.Equal(1, await FiresAsync(host, player));

            Assert.True(await StepAsync(host, session, crafting, player));
            await host.World.AdvanceClockAsync(500);
            Assert.Equal(2u, await CountAsync(host, player, Reagent));
            Assert.Equal(1u, await CountAsync(host, player, Product));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
        }, manualClock: true, campfire: real);
    }

    [Fact]
    public async Task AFireReachedAfterSteepEscapeLeg_ContinuesWalkingOnwardLegUntilInReach_WithoutFalseOutOfRangeAnomaly()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: new OffMeshFireCenterPathfinder(player.X + 4.5f));
                return true;
            });
            ObjectGuid fire = await host.OnWorldAsync(() => Objs(host, player).Summon(FireEntry, player.X + 12f, player.Y, player.Z, 0)!.Guid);
            Assert.True(await host.World.AdvanceClockUntilAsync(10_000, () => player.VisibleObjects.Contains(fire)), "the fire never became visible");
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                // The first leg was a 4-yard steep-escape route (TryLeaveSteepGround) that ran to completion,
                // leaving the bot at player.X (still 12 yards from the fire, outside its 11.39-yard focus reach).
                var here = new Vector3(player.X, player.Y, player.Z);
                var start = here - new Vector3(4f, 0f, 0f);
                var escapeLeg = new PlayerbotRoute([start, here], 4f, navigated: true) { NextPoint = 2 };
                Assert.True(escapeLeg.Complete);
                InjectRoute(crafting, escapeLeg, fire);
                return true;
            });

            for (int i = 0; i < 6 && await CountAsync(host, player, Product) == 0; i++)
            {
                Assert.True(await StepAsync(host, session, crafting, player));
                await host.World.AdvanceClockAsync(500);
            }

            Assert.Equal(2u, await CountAsync(host, player, Reagent));
            Assert.Equal(1u, await CountAsync(host, player, Product));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
        }, manualClock: true);
    }

    /// <summary>
    /// Simulates a fire whose exact center query returns an <see cref="PathType.Incomplete"/> path stopping 11.7 yards short
    /// (inside <c>PathEndShortYards = 12</c>, so <c>PlanExact</c> accepts it, but outside the fire's ~11.39-yard focus reach),
    /// while a ring point 7 yards from the fire's center is reachable by a normal path.
    /// </summary>
    private sealed class ShortIncompleteCenterButReachableRingPathfinder(float meshEdgeX) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (end.X > meshEdgeX + 3f)
            {
                Vector3 shortStop = new(end.X - 11.7f, end.Y, end.Z);
                return PathResult.StraightLine(start, shortStop, PathType.Incomplete);
            }

            Vector3 clamped = new(MathF.Min(end.X, meshEdgeX), end.Y, end.Z);
            PathType type = end.X > meshEdgeX ? PathType.Incomplete : PathType.Normal;
            return PathResult.StraightLine(start, clamped, type);
        }
    }

    [Fact]
    public async Task AnIncompleteExactRouteEndingOutsideFocusReach_FallsBackToRingWithinReach_WithoutWalkedButOutOfRangeAnomaly()
    {
        await RunAsync(knowsCampfire: false, woodCount: 0, async (host, session, crafting, player) =>
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: new ShortIncompleteCenterButReachableRingPathfinder(player.X + 4.5f));
                return true;
            });
            await SummonVisibleAsync(host, player, 12f, 0f);
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));

            for (int i = 0; i < 6 && await CountAsync(host, player, Product) == 0; i++)
            {
                Assert.True(await StepAsync(host, session, crafting, player));
                await host.World.AdvanceClockAsync(500);
            }

            Assert.Equal(2u, await CountAsync(host, player, Reagent));
            Assert.Equal(1u, await CountAsync(host, player, Product));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
        }, manualClock: true);
    }

    private sealed class OptionalDbcFactAttribute : FactAttribute
    {
        public const string Variable = "ARCANECORE_TEST_DBC_DIR";

        public OptionalDbcFactAttribute()
        {
            string? dir = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "Spell.dbc")))
                Skip = $"Set {Variable} to the build-5875 DBC directory to read Basic Campfire from the real Spell.dbc.";
        }
    }
}

/// <summary>The crafting test classes share the bot name "Controlone" in the anomaly ledger, so they run one after the other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlayerbotCraftingCollection
{
    public const string Name = "Playerbot crafting";
}