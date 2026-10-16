using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Game.Maps;
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
            return Task.FromResult(new GameObjectContent([fire], [], [], [], []));
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

    /// <summary>Spell.dbc row 818 of build 5875: effect 1 TRANS_DOOR (50) of gameobject 29784, tool (totem) Flint and Tinder 4471, reagent Simple Wood 4470.</summary>
    private static SpellInfo BasicCampfire(uint manaCost = 0) => new()
    {
        Id = Campfire,
        Name = "Basic Campfire",
        ManaCost = manaCost,
        Totems = [FlintAndTinder],
        Reagents = [new SpellReagent(Wood, 1)],
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.TransDoor, MiscValue = (int)FireEntry, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    private static async Task RunAsync(bool knowsCampfire, uint woodCount, Func<WorldTestHost, WorldSession, PlayerbotCrafting, Player, Task> body, bool flint = true, uint campfireMana = 0, bool manualClock = false)
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
                spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => s.Id is not Recipe and not Campfire), Cook(), BasicCampfire(campfireMana)], [], []);
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

    private static bool Step(WorldSession session, PlayerbotCrafting crafting, Player player)
    {
        session.ManagedBudget = new ManagedActionBudget(5);
        return crafting.Update(player, 500);
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
        await RunAsync(knowsCampfire: false, woodCount: 0, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.NotNull(Objs(host, player).Summon(FireEntry, player.X + 2f, player.Y, player.Z, 0));

            Assert.True(Step(session, crafting, player));
            Assert.Equal(2u, player.Inventory.GetItemCount(Reagent));
            Assert.Equal(1u, player.Inventory.GetItemCount(Product));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            return true;
        }));
    }

    [Fact]
    public async Task BotWithWoodAndTheCampfireSpell_LightsACampfire_ThenCooks()
    {
        await RunAsync(knowsCampfire: true, woodCount: 2, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            Assert.True(Step(session, crafting, player));
            Assert.Equal(1u, player.Inventory.GetItemCount(Wood));
            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));

            Assert.True(Step(session, crafting, player));
            Assert.Equal(2u, player.Inventory.GetItemCount(Reagent));
            Assert.Equal(1u, player.Inventory.GetItemCount(Product));
            Assert.Equal(1u, player.Inventory.GetItemCount(Wood));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            return true;
        }));
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
            await host.World.AdvanceClockAsync(200);
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
        await RunAsync(knowsCampfire: true, woodCount: 2, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.True(Step(session, crafting, player));
            Assert.Equal(2u, player.Inventory.GetItemCount(Wood));

            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.False(crafting.Update(player, 500));
            Assert.Equal(5, session.ManagedBudget.Remaining);
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            return true;
        }), campfireMana: 100_000);
    }

    [Fact]
    public async Task OneWoodPerFire_TheBotCooksSeveralTimesAtTheFireItLit_AndDoesNotRelight()
    {
        await RunAsync(knowsCampfire: true, woodCount: 3, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            Assert.True(Step(session, crafting, player));
            Assert.Equal(2u, player.Inventory.GetItemCount(Wood));
            for (int i = 0; i < 3; i++)
                Assert.True(Step(session, crafting, player));
            Assert.Equal(0u, player.Inventory.GetItemCount(Reagent));
            Assert.Equal(3u, player.Inventory.GetItemCount(Product));
            Assert.Equal(2u, player.Inventory.GetItemCount(Wood));
            Assert.Single(Objs(host, player).GameObjects, g => g.Template.Entry == FireEntry);
            return true;
        }));
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

        await RunAsync(knowsCampfire: false, woodCount: 0, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotCrafting.ProvidesSpellFocus(player, real, 4));
            Assert.False(PlayerbotCrafting.ProvidesSpellFocus(player, real, 3));
            return true;
        }));
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
public static class PlayerbotCraftingCollection
{
    public const string Name = "Playerbot crafting";
}