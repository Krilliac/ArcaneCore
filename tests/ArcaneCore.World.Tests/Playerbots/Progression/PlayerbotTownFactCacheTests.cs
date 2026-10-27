using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Bots.Progression;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// Lane L4 (live hot spots 2026-10-27, H1): the town-goal facts a bot recomputed on every think are memoized per bot. Each cache must
/// give the answer a fresh computation gives after every invalidation trigger (an inventory change, an equip, an enchant, a bank move, a
/// level change, a learned spell, a swapped spellbook, a spend limit, the time-to-live, a content refresh), and must not recompute when
/// nothing changed (money, which the vendor cache never reads, is among those).
/// </summary>
public sealed class PlayerbotTownFactCacheTests
{
    private const uint WantedPotion = 9701, OtherPotion = 9702, Bar = 9703, Ore = 9704;
    private const uint ReagentA = 9711, ReagentB = 9712, ReagentE = 9713, ReagentU = 9714, ToolA = 9721, WornBoots = 9731, UpgradeBoots = 9732;
    private const uint PotionRecipe = 997001, OtherRecipe = 997002, SmeltRecipe = 997003, BarRecipe = 997004, EnchantRecipe = 997005, UpgradeRecipe = 997006;

    private static readonly uint[] Probes = [0, ReagentA, ReagentB, ReagentE, ReagentU, ToolA, WantedPotion, OtherPotion, Bar, Ore, 2901, 12345];

    private static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        foreach (uint entry in new[] { WantedPotion, OtherPotion })
            items.Templates.Templates.Add(new ItemTemplate { Entry = entry, Name = $"potion {entry}", Class = (uint)ItemClass.Consumable,
                InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        foreach (uint entry in new[] { ReagentA, ReagentB, ReagentE, ReagentU, ToolA, Bar, Ore })
            items.Templates.Templates.Add(new ItemTemplate { Entry = entry, Name = $"supply {entry}", Class = (uint)ItemClass.TradeGoods,
                InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        // Equal boots: a craftable pair is no upgrade while the worn ones work, and is one once they are broken.
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Template(WornBoots, InventoryType.Feet, 70) with { MaxDurability = 10 });
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Template(UpgradeBoots, InventoryType.Feet, 70));
        return items;
    }

    private static SpellStore Store() => new([
        Recipe(PotionRecipe, WantedPotion, [new SpellReagent(ReagentA, 2), new SpellReagent(ReagentB, 1)], [ToolA, 0]),
        Recipe(OtherRecipe, OtherPotion, [new SpellReagent(ReagentA, 5)], [0, 0]),
        // A bar is wanted only while a known recipe uses it (PlayerbotCrafting.CraftPriority rung 4).
        Recipe(SmeltRecipe, Bar, [new SpellReagent(Ore, 1)], [0, 0]),
        Recipe(BarRecipe, OtherPotion, [new SpellReagent(Bar, 2)], [0, 0]),
        Recipe(UpgradeRecipe, UpgradeBoots, [new SpellReagent(ReagentU, 2)], [0, 0]),
        // Wanted only while a worn item has no permanent enchantment (PlayerbotCrafting.WantsToEnchantSpell).
        new SpellInfo
        {
            Id = EnchantRecipe,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.EnchantItem, MiscValue = 77 }, new(), new()],
            Reagents = [new SpellReagent(ReagentE, 3)],
            Totems = [0, 0],
        },
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
                player.Inventory.CanUseBank = () => true;
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
                Check("money: nothing the spellbook read uses", recomputes: false);
                player.Level = (byte)(player.Level + 1);
                Check("level", recomputes: true);

                // A learned spell whose reagent is relevant: a recipe that uses bars makes bars a need, then smelting them makes ore one.
                Assert.False(cached.Needs(player, Bar));
                book.LearnSpell(player, BarRecipe);
                Check("learned spell: bar recipe", recomputes: true);
                Assert.True(cached.Needs(player, Bar));
                Assert.False(cached.Needs(player, Ore));
                book.LearnSpell(player, SmeltRecipe);
                Check("learned spell: smelting", recomputes: true);
                Assert.True(cached.Needs(player, Ore));

                // An enchant recipe is wanted while a worn item has no enchantment: wearing one, then enchanting it, each change the answer.
                book.LearnSpell(player, EnchantRecipe);
                Check("learned spell: enchanting", recomputes: true);
                Assert.False(cached.Needs(player, ReagentE));
                PlayerbotEquipmentTests.EquipFromBags(player, WornBoots);
                Check("equipped item", recomputes: true);
                Assert.True(cached.Needs(player, ReagentE));
                Item worn = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!;
                Assert.Equal(WornBoots, worn.Entry);
                worn.SetUInt32(UpdateFields.ItemFieldEnchantment, 77);
                Check("enchant applied", recomputes: true);
                Assert.False(cached.Needs(player, ReagentE));
                worn.SetUInt32(UpdateFields.ItemFieldEnchantment, 0);
                Check("enchant removed", recomputes: true);
                Assert.True(cached.Needs(player, ReagentE));

                // A worn item that breaks scores 0, so a craftable replacement becomes an upgrade; repairing it (money only) ends that.
                book.LearnSpell(player, UpgradeRecipe);
                Check("learned spell: boots recipe", recomputes: true);
                Assert.False(cached.Needs(player, ReagentU));
                worn.Durability = 0;
                Check("worn item broken", recomputes: true);
                Assert.True(cached.Needs(player, ReagentU));
                worn.Durability = worn.MaxDurability;
                Check("worn item repaired", recomputes: true);
                Assert.False(cached.Needs(player, ReagentU));

                // Carrying five of a potion ends the want for it and so the need for its reagent; the bank does not count as carried,
                // so a deposit and a withdraw flip the need back and forth.
                Assert.True(cached.Needs(player, ReagentA));
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(OtherPotion, 5, out _));
                Check("inventory: other potion stocked", recomputes: true);
                Assert.False(cached.Needs(player, ReagentA));
                Item stock = player.Inventory.AllItems.Single(item => item.Entry == OtherPotion);
                player.Inventory.AutoBankItem(stock.BagSlot, stock.Slot);
                Assert.True(InventorySlots.IsBankPos(stock.BagSlot, stock.Slot), "the stack did not reach the bank");
                Check("bank deposit", recomputes: true);
                Assert.True(cached.Needs(player, ReagentA));
                player.Inventory.AutoStoreBankItem(stock.BagSlot, stock.Slot);
                Assert.False(InventorySlots.IsBankPos(stock.BagSlot, stock.Slot), "the stack did not leave the bank");
                Check("bank withdraw", recomputes: true);
                Assert.False(cached.Needs(player, ReagentA));

                // The same spells read from a reloaded spell store (a new instance, as after a content reload) is a new read.
                store = feature.System.Store = Store();
                Check("spell store swapped, same spells", recomputes: true);
                Check("nothing changed after the store swap", recomputes: false);

                // A swapped spellbook snapshot (a forgotten spell), then another spellbook (its snapshot is a new list too).
                Assert.True(book.ForgetSpell(player, BarRecipe));
                Check("forgotten spell: new snapshot", recomputes: true);
                Assert.False(cached.Needs(player, Ore));
                SpellbookCache other = new(null, NullLogger<SpellbookCache>.Instance);
                other.LearnSpell(player, PotionRecipe);
                Check("same book again", recomputes: false);
                Assert.True(cached.Needs(player, ReagentE));
                book = other;
                Check("another spellbook", recomputes: true);
                Assert.False(cached.Needs(player, ReagentE));
                Check("nothing changed after the swap", recomputes: false);
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

                // The same spell snapshot against a reloaded spell store: the index was read from the old store, so it is built again.
                store = feature.System.Store = Store();
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
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<ISpellContentStore>(fixture);
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                CreatureContent reloaded = await ((ICreatureDataStore)fixture).LoadAsync(CancellationToken.None);
                Player player = null!;
                PlayerbotTrainerDestinations route = null!;

                void Check(string state, bool requotes)
                {
                    int before = route.QuotesAsked;
                    bool cached = route.HasCandidate(player);
                    Assert.True(route.QuotesAsked == before + (requotes ? 1 : 0),
                        $"{state}: {route.QuotesAsked - before} quotes, expected {(requotes ? 1 : 0)}");
                    bool fresh = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true }) { Budget = route.Budget }.HasCandidate(player);
                    Assert.True(fresh == cached, $"{state}: cached {cached}, fresh {fresh}");
                }

                await host.OnWorldAsync(() =>
                {
                    player = session.Player!;
                    player.Money = 100;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });

                    Check("first ask", requotes: true);
                    Assert.True(route.HasCandidate(player));
                    Check("nothing changed", requotes: false);
                    player.Money = 101;
                    Check("money", requotes: true);
                    player.Level = (byte)(player.Level + 1);
                    Check("level", requotes: true);
                    Check("nothing changed after the level", requotes: false);

                    // A change in what the bot may spend (PlayerbotBudget.Spendable): a reserve ahead of class training eats all the money,
                    // then leaves enough for the one-copper spell.
                    var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
                    route.Budget = budget;
                    Check("budget attached, same limit", requotes: false);
                    budget.Pin(new PlayerbotBudgetLine(PlayerbotSpendKind.Repair, 101));
                    Assert.Equal(0u, budget.Spendable(PlayerbotSpendKind.ClassTraining, player));
                    Check("spend limit 0", requotes: true);
                    Assert.False(route.HasCandidate(player));
                    budget.Pin(new PlayerbotBudgetLine(PlayerbotSpendKind.Repair, 50));
                    Assert.Equal(51u, budget.Spendable(PlayerbotSpendKind.ClassTraining, player));
                    Check("spend limit 51", requotes: true);
                    Assert.True(route.HasCandidate(player));
                    Check("nothing changed after the limit", requotes: false);
                    return true;
                });

                // The yes-memo (kept 5 s) holds up to its last millisecond and is gone at the 5 s mark.
                await host.World.AdvanceClockAsync(4_999);
                await host.OnWorldAsync(() => { Check("inside the memo", requotes: false); return true; });
                await host.World.AdvanceClockAsync(1);
                await host.OnWorldAsync(() =>
                {
                    Check("memo expired", requotes: true);
                    Assert.True(route.HasCandidate(player));
                    Check("nothing changed after the expiry", requotes: false);

                    // A content refresh (the definitions swapped) clears the memos with the destinations.
                    session.Services.GetRequiredService<CreatureWorldFeature>().Content.SwapDefinitions(reloaded);
                    Check("content refreshed", requotes: true);
                    Check("nothing changed after the refresh", requotes: false);

                    session.Services.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(player, 944011);
                    Check("learned spell", requotes: true);
                    player.Money = 0;
                    Check("money spent", requotes: true);
                    Assert.False(route.HasCandidate(player));
                    Check("nothing changed after the money was spent", requotes: false);
                    return true;
                });

                // The no-memo expires the same way.
                await host.World.AdvanceClockAsync(4_999);
                await host.OnWorldAsync(() => { Check("inside the no-memo", requotes: false); return true; });
                await host.World.AdvanceClockAsync(1);
                await host.OnWorldAsync(() => { Check("no-memo expired", requotes: true); return true; });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private static SpellInfo Recipe(uint id, uint product, SpellReagent[] reagents, uint[] tools) => new()
    {
        Id = id,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = product }, new(), new()],
        Reagents = reagents,
        Totems = tools,
    };
}
