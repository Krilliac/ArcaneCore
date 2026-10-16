using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
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
public sealed class PlayerbotCraftingSpellFocusTests
{
    private const uint Reagent = 99601;
    private const uint Product = 99602;
    private const uint Recipe = 99610;
    private const uint Wood = 4470;
    private const uint Campfire = 818;
    private const uint FireEntry = 99620;

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

    private static SpellInfo BasicCampfire() => new()
    {
        Id = Campfire,
        Name = "Basic Campfire",
        Reagents = [new SpellReagent(Wood, 1)],
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.SummonObjectWild, MiscValue = (int)FireEntry, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    private static async Task RunAsync(bool knowsCampfire, uint woodCount, Func<WorldTestHost, WorldSession, PlayerbotCrafting, Player, Task> body)
    {
        using IDisposable scope = Items().Use();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddScoped<IGameObjectDataStore>(_ => new Objects()));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var crafting = new PlayerbotCrafting(session, new PlayerbotOptions { Enabled = true });
            Player? player = null;
            await host.OnWorldAsync(() =>
            {
                player = session.Player!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => s.Id is not Recipe and not Campfire), Cook(), BasicCampfire()], [], []);
                Assert.True(spells.Spellbook.LearnSpell(player, Recipe));
                if (knowsCampfire)
                    Assert.True(spells.Spellbook.LearnSpell(player, Campfire));
                Assert.True(player.Inventory.AddItem(Reagent, 3, out _) == InventoryResult.Ok);
                if (woodCount > 0)
                    Assert.True(player.Inventory.AddItem(Wood, woodCount, out _) == InventoryResult.Ok);
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
}
