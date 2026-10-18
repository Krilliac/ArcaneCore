using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// <see cref="PlayerbotVendorSupplyNeeds"/> (the spellbook read once, then a lookup per vendor row) gives
/// <see cref="PlayerbotCrafting.NeedsVendorSupply"/>'s answer (every known spell walked per row) for every item, through a sequence of
/// bot states: reagent counts below, at and above what each wanted recipe uses, tools carried or not, skills learned, a recipe that is
/// not wanted, a recipe that stops being wanted once its product is carried.
/// </summary>
public sealed class PlayerbotVendorSupplyNeedsTests
{
    private const uint WantedPotion = 9601, OtherPotion = 9602, UnknownProduct = 9699;
    private const uint ReagentA = 9611, ReagentB = 9612, ReagentC = 9613, ToolA = 9621, ToolB = 9622, UnwantedTool = 9623;

    private static readonly uint[] Probes =
    [
        0, ReagentA, ReagentB, ReagentC, ToolA, ToolB, UnwantedTool, WantedPotion, OtherPotion, UnknownProduct,
        2901, 5956, 7005, 6219, 2840, 12345,
    ];

    [Fact]
    public async Task NeedsMatchesTheSpellbookWalkForEveryItemAndState()
    {
        var items = new ItemTestContent();
        foreach (uint entry in new[] { WantedPotion, OtherPotion })
            items.Templates.Templates.Add(new ItemTemplate { Entry = entry, Name = $"potion {entry}", Class = (uint)ItemClass.Consumable,
                InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        foreach (uint entry in new[] { ReagentA, ReagentB, ReagentC, ToolA, ToolB, UnwantedTool, 2901u, 5956u })
            items.Templates.Templates.Add(new ItemTemplate { Entry = entry, Name = $"supply {entry}", Class = (uint)ItemClass.TradeGoods,
                InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellStore store = feature.System.Store = new SpellStore([
                    // Wanted (a consumable the bot carries fewer than 5 of): reagent A x2, reagent B x1, tool A.
                    Recipe(996001, WantedPotion, [new SpellReagent(ReagentA, 2), new SpellReagent(ReagentB, 1)], [ToolA, 0]),
                    // Wanted too: reagent A x5 (the larger count decides), tool B in the second slot.
                    Recipe(996002, OtherPotion, [new SpellReagent(ReagentA, 5)], [0, ToolB]),
                    // Never wanted (its product has no template): reagent C, and its tool still counts.
                    Recipe(996003, UnknownProduct, [new SpellReagent(ReagentC, 3)], [UnwantedTool, 0]),
                    new SpellInfo { Id = 996004, Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage }, new(), new()] },
                ], [], []);
                SpellbookCache book = feature.Spellbook;
                var needs = new PlayerbotVendorSupplyNeeds();
                int compared = 0, needed = 0;

                void Compare(string state)
                {
                    needs.Refresh(player, store, book);
                    foreach (uint item in Probes)
                    {
                        bool expected = PlayerbotCrafting.NeedsVendorSupply(player, item, store, book);
                        Assert.True(expected == needs.Needs(player, item), $"{state}: item {item} (the spellbook walk says {expected})");
                        compared++;
                        if (expected) needed++;
                    }
                }

                Compare("no recipes known");
                foreach (uint spell in new uint[] { 996001, 996002, 996003, 996004 }) book.LearnSpell(player, spell);
                Compare("recipes known, nothing carried");
                foreach (uint count in new uint[] { 1, 1, 2, 1 })
                {
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ReagentA, count, out _));
                    Compare($"{player.Inventory.GetItemCount(ReagentA)} of reagent A");
                }
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ReagentB, 1, out _));
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ToolA, 1, out _));
                Compare("reagent B and tool A carried");
                // The common tools' skill rule is one shared function (PlayerbotCrafting.CommonToolNeed); a managed test bot may have no
                // skill state, and then only the carried-tool half of it is compared.
                player.Skills?.Set(SkillIds.Mining, 1, 75, 1);
                Compare("mining learned, no pick");
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(2901, 1, out _));
                Compare("mining pick carried");
                player.Skills?.Set(SkillIds.Blacksmithing, 1, 75, 1);
                Compare("blacksmithing learned, no hammer");
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(5956, 1, out _));
                Compare("blacksmith hammer carried");
                // Five potions carried: the first recipe is no longer wanted, so its reagent B stops being needed.
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(WantedPotion, 5, out _));
                Compare("wanted potion stocked");
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(OtherPotion, 5, out _));
                Compare("both potions stocked");

                // Without a spell store or a spellbook only the common tools count, as before.
                needs.Refresh(player, null, book);
                foreach (uint item in Probes)
                    Assert.True(PlayerbotCrafting.NeedsVendorSupply(player, item, null, book) == needs.Needs(player, item), $"no store: item {item}");
                Assert.True(compared >= 13 * Probes.Length);
                // Both answers occur, so the comparison is not of two constants.
                Assert.InRange(needed, 10, compared - 10);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A cook that knows Basic Campfire (818) and wants a recipe that needs a cooking fire (spell focus 4) needs one Simple Wood (4470),
    /// the campfire's reagent, from a vendor, in both answers; without such a recipe, or without the campfire, it does not; with the
    /// wood carried it does not (bot QA #774: the campfire fallback for an unreachable fire never had wood).
    /// </summary>
    [Fact]
    public async Task ACookWithBasicCampfire_NeedsTheCampfiresWood_OnlyForAFireRecipe()
    {
        const uint Wood = 4470, Meat = 9631, Steak = 9632, FireRecipe = 996011;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Steak, Name = "steak", Class = (uint)ItemClass.Consumable, FoodType = 1,
            InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        foreach (uint entry in new[] { Wood, Meat })
            items.Templates.Templates.Add(new ItemTemplate { Entry = entry, Name = $"supply {entry}", Class = (uint)ItemClass.TradeGoods,
                InventoryType = (uint)InventoryType.NonEquip, Stackable = 20, AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellStore store = feature.System.Store = new SpellStore([
                    new SpellInfo { Id = PlayerbotCrafting.BasicCampfireSpell, Name = "Basic Campfire",
                        Effects = [new SpellEffectInfo { Effect = SpellEffectName.TransDoor }, new(), new()],
                        Reagents = [new SpellReagent(Wood, 1)], Totems = [4471, 0] },
                    Recipe(FireRecipe, Steak, [new SpellReagent(Meat, 1)], [0, 0]) with { RequiresSpellFocus = PlayerbotCrafting.CookingFireFocus },
                ], [], []);
                SpellbookCache book = feature.Spellbook;
                var needs = new PlayerbotVendorSupplyNeeds();
                bool Both()
                {
                    needs.Refresh(player, store, book);
                    bool walk = PlayerbotCrafting.NeedsVendorSupply(player, Wood, store, book);
                    Assert.Equal(walk, needs.Needs(player, Wood));
                    return walk;
                }

                book.LearnSpell(player, PlayerbotCrafting.BasicCampfireSpell);
                Assert.False(Both()); // the campfire alone: no fire recipe to light it for
                book.LearnSpell(player, FireRecipe);
                Assert.True(Both()); // a wanted food recipe at a cooking fire (the bot carries none of the steak)
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(Wood, 1, out _));
                Assert.False(Both());
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static SpellInfo Recipe(uint id, uint product, SpellReagent[] reagents, uint[] tools) => new()
    {
        Id = id,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = product }, new(), new()],
        Reagents = reagents,
        Totems = tools,
    };
}
