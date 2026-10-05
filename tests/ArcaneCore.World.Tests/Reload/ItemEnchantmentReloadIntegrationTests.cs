using System.Buffers.Binary;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

public sealed class ItemEnchantmentReloadIntegrationTests
{
    [Fact]
    public async Task AlreadyOnlinePlayer_KeepsOldAppliedStatsAndUsesReloadedDefinitionOnNextApply()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spell-enchant-online-{Guid.NewGuid():N}.dbc");
        try
        {
            File.WriteAllBytes(path, Image(9010, 10));
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["World:SpellItemEnchantmentDbcPath"] = path }).Build();
            var items = new ItemTestContent();
            items.Templates.Templates.Add(new ItemTemplate { Entry = 9011, Class = 2, SubClass = 7, InventoryType = 21 });
            items.Templates.StartingItems.Add(new StartingItem(1, 1, 9011, 1));
            WorldTestHost host;
            using (items.Use()) host = WorldTestHost.Start(configureServices: services => services.AddSingleton(configuration));
            await using (host)
            await using (WorldTestClient client = await host.EnterWorldAsync("RELOADENCH", "Reloadench"))
            {
                int before = await host.OnWorldAsync(() =>
                {
                    Player player = host.World.FindOnlinePlayer("Reloadench")!;
                    var item = Assert.Single(player.Inventory.Equipped).Item;
                    int baseline = player.GetInt32(UpdateFields.UnitFieldStat0);
                    item.SetUInt32(UpdateFields.ItemFieldEnchantment, 9010);
                    player.Inventory.EnchantmentSink!.ApplyEnchantment(player, item, 0, true);
                    Assert.Equal(baseline + 10, player.GetInt32(UpdateFields.UnitFieldStat0));
                    return baseline;
                });
                File.WriteAllBytes(path, Image(9010, 20));
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                using ServiceProvider services = new ServiceCollection().AddSingleton(feature).AddSingleton(configuration).BuildServiceProvider();
                var coordinator = new ReloadCoordinator(NullLogger.Instance);
                coordinator.Attach(host.World);
                coordinator.Register(new ItemEnchantmentContentReloadable(services));
                Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("spell_item_enchantment")).Status);
                await host.OnWorldAsync(() =>
                {
                    Player player = host.World.FindOnlinePlayer("Reloadench")!;
                    var item = Assert.Single(player.Inventory.Equipped).Item;
                    Assert.Equal(before + 10, player.GetInt32(UpdateFields.UnitFieldStat0));
                    player.Inventory.EnchantmentSink!.ApplyEnchantment(player, item, 0, false);
                    Assert.Equal(before, player.GetInt32(UpdateFields.UnitFieldStat0));
                    player.Inventory.EnchantmentSink.ApplyEnchantment(player, item, 0, true);
                    Assert.Equal(before + 20, player.GetInt32(UpdateFields.UnitFieldStat0));
                });
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task ValidConfiguredDbc_ReloadsTheImmutableCatalog_AndCorruptReplacementRetainsIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"spell-enchant-{Guid.NewGuid():N}.dbc");
        try
        {
            File.WriteAllBytes(path, Image(9010, 10));
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["World:SpellItemEnchantmentDbcPath"] = path }).Build();
            await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton(configuration));
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            var services = new ServiceCollection().AddSingleton(feature).AddSingleton(configuration).BuildServiceProvider();
            var coordinator = new ReloadCoordinator(NullLogger.Instance);
            coordinator.Attach(host.World);
            coordinator.Register(new ItemEnchantmentContentReloadable(services));

            ReloadResult first = await coordinator.ReloadAsync("spell_item_enchantment");
            Assert.Equal(ReloadStatus.Applied, first.Status);
            IItemEnchantmentCatalog published = feature.EnchantmentCatalogProvider!.Current;
            Assert.Equal(10, published.Find(9010)!.Effects[0].Amount);

            File.WriteAllBytes(path, [1, 2, 3]);
            ReloadResult corrupt = await coordinator.ReloadAsync("spell_item_enchantment");
            Assert.Equal(ReloadStatus.Failed, corrupt.Status);
            Assert.Same(published, feature.EnchantmentCatalogProvider.Current);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] Image(uint entry, int amount)
    {
        byte[] bytes = new byte[20 + 24 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), entry);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 5);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), amount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 4); // type-5 strength selector, field 10
        return bytes;
    }
}
