using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Bots.Progression;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// Lane L4 (live hot spots 2026-10-27, H1): the town-goal facts a bot recomputed on every think are memoized per bot. Each cache must
/// give the answer a fresh computation gives after every invalidation trigger (an inventory change, a money change, a level change, a
/// learned spell, the time-to-live), and must not recompute when nothing changed.
/// </summary>
public sealed class PlayerbotTownFactCacheTests
{
    private const uint WantedPotion = 9701, OtherPotion = 9702, Bar = 9703, Ore = 9704;
    private const uint ReagentA = 9711, ReagentB = 9712, ToolA = 9721;
    private const uint PotionRecipe = 997001, OtherRecipe = 997002, SmeltRecipe = 997003, BarRecipe = 997004;

    private static readonly uint[] Probes = [0, ReagentA, ReagentB, ToolA, WantedPotion, OtherPotion, Bar, Ore, 2901, 12345];

    private static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        foreach (uint entry in new[] { WantedPotion, OtherPotion })
            items.Templates.Templates.Add(new ItemTemplate { Entry = entry, Name = $"potion {entry}", Class = (uint)ItemClass.Consumable,
                InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        foreach (uint entry in new[] { ReagentA, ReagentB, ToolA, Bar, Ore })
            items.Templates.Templates.Add(new ItemTemplate { Entry = entry, Name = $"supply {entry}", Class = (uint)ItemClass.TradeGoods,
                InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        return items;
    }

    private static SpellStore Store() => new([
        Recipe(PotionRecipe, WantedPotion, [new SpellReagent(ReagentA, 2), new SpellReagent(ReagentB, 1)], [ToolA, 0]),
        Recipe(OtherRecipe, OtherPotion, [new SpellReagent(ReagentA, 5)], [0, 0]),
        // A bar is wanted only while a known recipe uses it (PlayerbotCrafting.CraftPriority rung 4).
        Recipe(SmeltRecipe, Bar, [new SpellReagent(Ore, 1)], [0, 0]),
        Recipe(BarRecipe, OtherPotion, [new SpellReagent(Bar, 2)], [0, 0]),
    ], [], []);

    [Fact]
    public async Task VendorSupplyNeeds_SecondReadWithNothingChanged_DoesNotRecompute_AndEqualsAFreshReadAfterEveryTrigger()
    {
        using IDisposable scope = Items().Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellStore store = feature.System.Store = Store();
                SpellbookCache book = feature.Spellbook;
                foreach (uint spell in new[] { PotionRecipe, OtherRecipe }) book.LearnSpell(player, spell);
                var cached = new PlayerbotVendorSupplyNeeds();
                uint now = 1_000;

                void Check(string state, bool recomputes)
                {
                    int before = cached.Refreshes;
                    cached.RefreshIfChanged(player, store, book, now);
                    Assert.True(cached.Refreshes == before + (recomputes ? 1 : 0),
                        $"{state}: {cached.Refreshes - before} spellbook reads, expected {(recomputes ? 1 : 0)}");
                    var fresh = new PlayerbotVendorSupplyNeeds();
                    fresh.Refresh(player, store, book);
                    foreach (uint item in Probes)
                        Assert.True(fresh.Needs(player, item) == cached.Needs(player, item), $"{state}: item {item}");
                    Assert.Equal(fresh.Shortfalls(player, 16), cached.Shortfalls(player, 16));
                }

                Check("first read", recomputes: true);
                Check("nothing changed", recomputes: false);
                Check("nothing changed, again", recomputes: false);
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ReagentA, 2, out _));
                Check("inventory: reagent A bought", recomputes: true);
                Check("nothing changed after the buy", recomputes: false);
                // Stocking the potion stops its recipe being wanted: reagent B leaves the needs. Only a re-read sees that.
                Assert.True(cached.Needs(player, ReagentB));
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(WantedPotion, 5, out _));
                Check("inventory: wanted potion stocked", recomputes: true);
                Assert.False(cached.Needs(player, ReagentB));
                player.Money += 100;
                Check("money", recomputes: true);
                player.Level = (byte)(player.Level + 1);
                Check("level", recomputes: true);
                book.LearnSpell(player, SmeltRecipe);
                Check("learned spell", recomputes: true);
                now += PlayerbotVendorSupplyNeeds.CacheTtlMs - 1;
                Check("inside the time-to-live", recomputes: false);
                now += 1;
                Check("time-to-live reached", recomputes: true);
                Check("nothing changed after the time-to-live", recomputes: false);
                cached.RefreshIfChanged(player, null, book, now);
                foreach (uint item in Probes)
                    Assert.True(PlayerbotCrafting.NeedsVendorSupply(player, item, null, book) == cached.Needs(player, item), $"no store: {item}");
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task CraftPriority_ReagentIndexIsBuiltOncePerSpellbook_AndFollowsALearnedRecipe()
    {
        using IDisposable scope = Items().Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellStore store = feature.System.Store = Store();
                SpellbookCache book = feature.Spellbook;
                PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
                SpellInfo smelt = store.Get(SmeltRecipe)!;
                book.LearnSpell(player, SmeltRecipe);

                int before = PlayerbotCrafting.ReagentIndexBuilds;
                Assert.False(PlayerbotCrafting.WantsToCraftSpell(player, smelt, weights, store, book)); // no recipe uses bars yet
                Assert.False(PlayerbotCrafting.WantsToCraftSpell(player, smelt, weights, store, book));
                Assert.False(PlayerbotCrafting.WantsToCraftSpell(player, smelt, weights, store, book));
                Assert.Equal(1, PlayerbotCrafting.ReagentIndexBuilds - before);

                book.LearnSpell(player, BarRecipe); // learned spell: a known recipe now uses bars
                before = PlayerbotCrafting.ReagentIndexBuilds;
                Assert.True(PlayerbotCrafting.WantsToCraftSpell(player, smelt, weights, store, book));
                Assert.True(PlayerbotCrafting.WantsToCraftSpell(player, smelt, weights, store, book));
                Assert.Equal(1, PlayerbotCrafting.ReagentIndexBuilds - before);


                // Inventory still read live: 20 bars carried ends the want (rung 4's own count), with no rebuild.
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(Bar, 20, out _));
                before = PlayerbotCrafting.ReagentIndexBuilds;
                Assert.False(PlayerbotCrafting.WantsToCraftSpell(player, smelt, weights, store, book));
                Assert.Equal(0, PlayerbotCrafting.ReagentIndexBuilds - before);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task TrainerAffordableSpell_SecondAskWithNothingChanged_DoesNotRequote_AndEqualsAFreshAnswerAfterEveryTrigger()
    {
        var fixture = new FarTrainerFixture();
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    var route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });

                    void Check(string state, bool requotes)
                    {
                        int before = route.QuotesAsked;
                        bool cached = route.HasCandidate(player);
                        Assert.True(route.QuotesAsked == before + (requotes ? 1 : 0),
                            $"{state}: {route.QuotesAsked - before} quotes, expected {(requotes ? 1 : 0)}");
                        bool fresh = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true }).HasCandidate(player);
                        Assert.True(fresh == cached, $"{state}: cached {cached}, fresh {fresh}");
                    }

                    Check("first ask", requotes: true);
                    Assert.True(route.HasCandidate(player));
                    Check("nothing changed", requotes: false);
                    player.Money = 101;
                    Check("money", requotes: true);
                    player.Level = (byte)(player.Level + 1);
                    Check("level", requotes: true);
                    Check("nothing changed after the level", requotes: false);
                    session.Services.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(player, 944011);
                    Check("learned spell", requotes: true);
                    player.Money = 0;
                    Check("money spent", requotes: true);
                    Assert.False(route.HasCandidate(player));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    private static SpellInfo Recipe(uint id, uint product, SpellReagent[] reagents, uint[] tools) => new()
    {
        Id = id,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = product }, new(), new()],
        Reagents = reagents,
        Totems = tools,
    };
}
