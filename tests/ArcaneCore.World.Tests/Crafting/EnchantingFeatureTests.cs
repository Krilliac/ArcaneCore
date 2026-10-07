using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.World.Crafting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Crafting;

/// <summary>Crafting lane: the enchanting feature loads its catalog, attaches the engine to a loading player and honours its switches.</summary>
public sealed class EnchantingFeatureTests
{
    private static EnchantCatalog Catalog() => new([new SpellItemEnchantment(7, [5, 0, 0], [3, 0, 0], [4, 0, 0], "Test", 0, 0)]);

    private static Action<IServiceCollection> Services(EnchantCatalog? catalog, string? enabled = null) => services =>
    {
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { ["Enchanting:Enabled"] = enabled })
            .Build());
        if (catalog is not null)
        {
            services.AddSingleton(catalog);
        }
    };

    [Fact]
    public async Task TheFeatureIsDiscovered_WithACatalog_ItIsActive_AndALoadingPlayerGetsTheEngine()
    {
        Assert.Contains(typeof(EnchantingFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Services(Catalog()));
        EnchantingFeature feature = host.WorldServices.GetRequiredService<EnchantingFeature>();

        await using WorldTestClient client = await host.EnterWorldAsync("ENCHANT1", "Disenchanter");

        Assert.True(feature.IsActive);
        Assert.Equal(1, feature.Catalog.Count);
        Assert.True(await host.PlayerStateAsync("Disenchanter", p => p.Enchantments is not null));
        Assert.True(await host.OnWorldAsync(() =>
        {
            var system = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System;
            return system.CastChecks.OfType<ItemTargetFitCheck>().Count() == 1
                && system.HasEffectHandler(ArcaneCore.Game.Spells.SpellEffectName.EnchantItem)
                && system.HasEffectHandler(ArcaneCore.Game.Spells.SpellEffectName.EnchantItemTemporary)
                && system.HasEffectHandler(ArcaneCore.Game.Spells.SpellEffectName.EnchantHeldItem);
        }));
    }

    [Fact]
    public async Task ALoadedPlayer_HasTheItemHookInstalled_SoAnEnchantmentFollowsTheEquipState()
    {
        var content = new ArcaneCore.World.Tests.Items.ItemTestContent();
        content.Templates.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ArcaneCore.Kernel.Items.ItemDamage(1, 3, 0)] });
        content.Templates.StartingItems.Add(new ArcaneCore.Kernel.Items.StartingItem(1, 1, 25, 1));
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start(configureServices: Services(Catalog()));
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("ENCHANT3", "Sharpener");

            (uint worn, uint unworn) = await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Sharpener")!;
                var sword = player.Inventory.GetItem(ArcaneCore.Game.Items.InventorySlots.Bag0, ArcaneCore.Game.Items.InventorySlots.MainHand)!;
                uint before = player.GetUInt32(ArcaneCore.Game.UpdateFields.UnitFieldStat0);
                ItemEnchantments.Set(sword, EnchantSlots.Permanent, 7, 0, 0);
                player.Enchantments!.Apply(sword, EnchantSlots.Permanent, apply: true);
                uint with = player.GetUInt32(ArcaneCore.Game.UpdateFields.UnitFieldStat0) - before;
                player.Inventory.SwapItem(sword.BagSlot, sword.Slot, ArcaneCore.Game.Items.InventorySlots.Bag0, ArcaneCore.Game.Items.InventorySlots.ItemStart + 10);
                return (with, player.GetUInt32(ArcaneCore.Game.UpdateFields.UnitFieldStat0) - before);
            });

            Assert.Equal(3u, worn);
            Assert.Equal(0u, unworn);
        }
    }

    [Fact]
    public async Task WithoutACatalog_TheFeatureIsInactive_AndNoPlayerGetsAnEngine()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Services(null));

        await using WorldTestClient client = await host.EnterWorldAsync("ENCHANT2", "Plainwalker");

        Assert.False(host.WorldServices.GetRequiredService<EnchantingFeature>().IsActive);
        Assert.True(await host.PlayerStateAsync("Plainwalker", p => p.Enchantments is null));
    }

    [Fact]
    public async Task Disabled_TheFeatureLoadsNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Services(Catalog(), enabled: "false"));

        Assert.False(host.WorldServices.GetRequiredService<EnchantingFeature>().IsActive);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CraftingDisabled_StillGuardsTheEnchantEffects_SoACastKeepsItsReagents()
    {
        const uint spellId = 50211;
        const uint sword = 50212;
        const uint dust = 50213;
        var items = new ArcaneCore.World.Tests.Items.ItemTestContent();
        items.Templates.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = sword, Class = 2, SubClass = 7, InventoryType = 21, Stackable = 1 });
        items.Templates.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = dust, Class = 7, SubClass = 0, Stackable = 20 });
        items.Templates.StartingItems.Add(new ArcaneCore.Kernel.Items.StartingItem(1, 1, sword, 1));
        items.Templates.StartingItems.Add(new ArcaneCore.Kernel.Items.StartingItem(1, 1, dust, 1));
        ArcaneCore.Data.Content.Spells.SpellContent content = ArcaneCore.World.Tests.Spells.SpellTestServices.Content();
        content = content with { Spells = [.. content.Spells, new ArcaneCore.Data.Content.Spells.SpellTemplateRow
        {
            Id = spellId, SpellName = "Synthetic enchant", RangeIndex = 1, Targets = 0x10, Effect1 = (uint)ArcaneCore.Game.Spells.SpellEffectName.EnchantItem,
            EquippedItemClass = -1, EffectMiscValue1 = 7, Reagent1 = (int)dust, ReagentCount1 = 1,
        }] };
        WorldTestHost host;
        using (items.Use())
        {
            host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { [CraftingFeature.EnabledKey] = "false" })
                    .Build());
                services.AddSingleton(Catalog());
                services.AddSingleton<ArcaneCore.Data.Content.Spells.ISpellContentStore>(new ArcaneCore.World.Tests.Spells.InMemorySpellContentStore(content));
            });
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("ENCHANT4", "Nocrafter");

            (ArcaneCore.Game.Spells.SpellCastResult result, uint dustLeft) = await host.OnWorldAsync(() =>
            {
                var player = host.World.FindOnlinePlayer("Nocrafter")!;
                var spells = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>();
                spells.Spellbook.LearnSpell(player, spellId);
                var target = Assert.Single(player.Inventory.Equipped).Item;
                var cast = spells.System.HandleCastRequest(player, spellId,
                    new ArcaneCore.Game.Spells.SpellCastTargets { Mask = ArcaneCore.Game.Spells.SpellCastTargetFlags.Item, Item = target.Guid });
                return (cast, player.Inventory.GetItemCount(dust));
            });

            // Crafting:Enabled=false leaves the enchant effects unhandled, as a missing DBC does: refuse before the reagents go.
            Assert.False(host.WorldServices.GetRequiredService<EnchantingFeature>().IsActive);
            Assert.Equal(ArcaneCore.Game.Spells.SpellCastResult.Unknown, result);
            Assert.Equal(1u, dustLeft);
        }
    }

    [Fact]
    public void AConfiguredButMissingDbc_RefusesStartup()
    {
        Action<IServiceCollection> services = s => s.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Enchanting:SpellItemEnchantmentDbcPath"] = Path.Combine(Path.GetTempPath(), "missing-enchant.dbc") })
            .Build());

        Assert.ThrowsAny<Exception>(() => WorldTestHost.Start(configureServices: services));
    }
}
