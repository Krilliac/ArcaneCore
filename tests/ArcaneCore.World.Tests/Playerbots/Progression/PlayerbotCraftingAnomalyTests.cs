using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// H1 hardening: <c>bot-progression/craft-failed</c> from <see cref="PlayerbotCrafting.Update"/>: a recipe whose spell focus is missing and a
/// cast the server rejected although the reagents are in the bags. The recipe is a synthetic consumable the bot wants (a consumable it carries
/// fewer than five of), made from one reagent.
/// </summary>
[Collection(PlayerbotCraftingCollection.Name)]
public sealed class PlayerbotCraftingAnomalyTests
{
    private const uint Reagent = 99501;
    private const uint Product = 99502;
    private const uint Recipe = 99510;

    private static readonly string CraftFailed = PlayerbotAnomalies.Key("progression", "craft-failed");

    private static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = Reagent, Name = "Anomaly Reagent", Class = 7, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = Product, Name = "Anomaly Elixir", Class = 0, Quality = 1, Stackable = 20 },
        ]);
        return items;
    }

    private static SpellInfo CraftSpell(uint manaCost = 0, uint focus = 0, int castTimeMs = 0) => new()
    {
        Id = Recipe,
        Name = "Anomaly Elixir Recipe",
        RequiresSpellFocus = focus,
        ManaCost = manaCost,
        CastTime = castTimeMs == 0 ? default : new SpellCastTime(castTimeMs, 0, castTimeMs),
        Reagents = [new SpellReagent(Reagent, 1)],
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = Product, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    private static async Task RunAsync(SpellInfo recipe, Func<WorldTestHost, WorldSession, PlayerbotCrafting, Player, Task> body)
    {
        using IDisposable scope = Items().Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var crafting = new PlayerbotCrafting(session, new PlayerbotOptions { Enabled = true });
            Player? player = null;
            await host.OnWorldAsync(() =>
            {
                player = session.Player!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => s.Id != Recipe), recipe], [], []);
                Assert.True(spells.Spellbook.LearnSpell(player, Recipe));
                Assert.True(player.Inventory.AddItem(Reagent, 3, out _) == InventoryResult.Ok);
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

    private static bool Step(WorldSession session, PlayerbotCrafting crafting, Player player)
    {
        session.ManagedBudget = new ManagedActionBudget(5);
        return crafting.Update(player, 500);
    }

    [Fact]
    public async Task CraftFailed_Silent_WhenTheRecipeNeedsASpellFocusThatIsNotAround()
    {
        // No forge or fire around is the ordinary state of a bot in the field, not an anomaly (see PlayerbotCraftingSpellFocusTests).
        await RunAsync(CraftSpell(focus: 777), (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            Assert.False(Step(session, crafting, player));
            Assert.False(Step(session, crafting, player));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));
            return true;
        }));
    }

    [Fact]
    public async Task CraftFailed_Fires_WhenTheServerRejectsTheCastWithTheReagentsPresent()
    {
        // The bot does not check mana before casting: the server's own check refuses a recipe that costs more mana than the bot has.
        await RunAsync(CraftSpell(manaCost: 100_000), (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            Assert.True(Step(session, crafting, player));
            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));
            Assert.Equal(0u, player.Inventory.GetItemCount(Product));
            Assert.Equal(1, capture.Count(CraftFailed));
            Assert.Contains($"craft spell {Recipe} rejected with reagents present", capture.Notices.Single(n => n.Key == CraftFailed).Detail, StringComparison.Ordinal);

            // Backed off for a minute: no second packet, no second report.
            int before = session.ManagedBudget!.Remaining;
            Assert.False(Step(session, crafting, player));
            Assert.Equal(1, capture.Count(CraftFailed));
            return true;
        }));
    }

    [Fact]
    public async Task CraftFailed_Silent_WhenTheCastLandsAtOnce_AndWhenItIsUnderway()
    {
        // An instant recipe is over before the bot looks: the reagent is gone and the product is in the bags, not a rejected cast.
        await RunAsync(CraftSpell(), async (host, session, crafting, player) =>
        {
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
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

        // A cast with a cast bar is in progress when the bot looks: still not a rejection.
        await RunAsync(CraftSpell(castTimeMs: 2000), (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.True(Step(session, crafting, player));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            return true;
        }));
    }

    [Fact]
    public async Task CraftFailed_Silent_WhenBotIsSittingOrInDisallowedShapeshiftForm()
    {
        await RunAsync(CraftSpell(), async (host, session, crafting, player) =>
        {
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                player.StandState = StandState.Sit;
                player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)ShapeshiftForm.Cat);

                Assert.True(Step(session, crafting, player));
                return true;
            });
            await SpellBatchGoneAsync(host, player);
            await host.OnWorldAsync(() =>
            {
                Assert.Equal(StandState.Stand, player.StandState);
                Assert.Equal(2u, player.Inventory.GetItemCount(Reagent));
                Assert.Equal(1u, player.Inventory.GetItemCount(Product));
                Assert.False(capture.Any(CraftFailed), capture.Describe());
                return true;
            });
        });
    }

    [Fact]
    public async Task CraftFailed_Silent_WhenSpellOnCooldown_OrInStealthWithStanceRestriction_OrMissingEquippedTool()
    {
        SpellInfo stealthBlockedRecipe = new()
        {
            Id = Recipe,
            Name = "Stealth-Blocked Elixir Recipe",
            StancesNot = 1u << ((int)ShapeshiftForm.Stealth - 1),
            RecoveryTime = 5_000,
            Reagents = [new SpellReagent(Reagent, 1)],
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = Product, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
        };

        await RunAsync(stealthBlockedRecipe, async (host, session, crafting, player) =>
        {
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                // 1. When the bot is in Stealth and the recipe forbids Stealth via StancesNot, EnsureReadyForCraftCast clears the form
                // (using real SpellShapeshiftForm flags so GetErrorAtShapeshiftedCast detects the restriction) and crafts cleanly.
                player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)ShapeshiftForm.Stealth);
                Assert.True(Step(session, crafting, player));
                return true;
            });
            await SpellBatchGoneAsync(host, player);
            await host.OnWorldAsync(() =>
            {
                SpellSystem spellSystem = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.Equal(0, player.GetByte(UpdateFields.UnitFieldBytes1, 2));
                Assert.Equal(2u, player.Inventory.GetItemCount(Reagent));
                Assert.Equal(1u, player.Inventory.GetItemCount(Product));
                Assert.False(capture.Any(CraftFailed), capture.Describe());

                // 2. While the recipe's 5s cooldown is active after the first craft, TryCraft skips it without casting or reporting craft-failed.
                Assert.False(spellSystem.IsSpellReady(player, stealthBlockedRecipe));
                Assert.False(Step(session, crafting, player));
                Assert.Equal(2u, player.Inventory.GetItemCount(Reagent));
                Assert.Equal(1u, player.Inventory.GetItemCount(Product));
                Assert.False(capture.Any(CraftFailed), capture.Describe());
                return true;
            });
        });

        SpellInfo equippedWeaponRecipe = new()
        {
            Id = Recipe,
            Name = "Fishing Pole Recipe",
            EquippedItemClass = 2,
            EquippedItemSubClassMask = 1 << 20,
            Reagents = [new SpellReagent(Reagent, 1)],
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.CreateItem, ItemType = Product, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
        };

        await RunAsync(equippedWeaponRecipe, (host, session, crafting, player) => host.OnWorldAsync(() =>
        {
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.False(Step(session, crafting, player));
            Assert.Equal(3u, player.Inventory.GetItemCount(Reagent));
            Assert.False(capture.Any(CraftFailed), capture.Describe());
            return true;
        }));
    }
}
