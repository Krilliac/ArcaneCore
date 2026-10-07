using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// The random property feature in the daemon: ItemRandomProperties.dbc and the item_enchantment_template dump are read from the configured
/// paths, the roll reaches every inventory at login, and a new item of a template with random_property gets its property and enchantments.
/// </summary>
public sealed class ItemRandomPropertyWorldTests
{
    private const uint Sword = 93_500;

    private static byte[] Dbc(uint id, uint enchant)
    {
        const int fields = 16;
        byte[] strings = Encoding.UTF8.GetBytes("\0of the Test\0");
        byte[] image = new byte[20 + (fields * 4) + strings.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), id);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), enchant);
        strings.CopyTo(image, 20 + (fields * 4));
        return image;
    }

    private static WorldTestHost Start(ItemTestContent content, Dictionary<string, string?> config)
    {
        content.Templates.Templates.Add(new ItemTemplate { Entry = Sword, Class = 2, SubClass = 7, Name = "Random Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, RandomProperty = 6 });
        using (content.Use())
        {
            return WorldTestHost.Start(configureServices: services =>
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build()));
        }
    }

    [Fact]
    public async Task ConfiguredContent_GivesNewItemsTheirProperty()
    {
        string dbc = Path.Combine(Path.GetTempPath(), "arcane-irp-" + Guid.NewGuid().ToString("N") + ".dbc");
        string dump = Path.Combine(Path.GetTempPath(), "arcane-iet-" + Guid.NewGuid().ToString("N") + ".sql");
        await File.WriteAllBytesAsync(dbc, Dbc(1001, 74));
        await File.WriteAllTextAsync(dump, """
            CREATE TABLE `item_enchantment_template` (`entry` int, `ench` int, `chance` float);
            INSERT INTO `item_enchantment_template` VALUES (6,1001,100);
            """);
        try
        {
            await using WorldTestHost host = Start(new ItemTestContent(), new()
            {
                ["ItemRandomProperties:DbcPath"] = dbc,
                ["ItemRandomProperties:EnchantmentTemplateDumpPath"] = dump,
            });
            ItemRandomPropertyFeature feature = host.WorldServices.GetRequiredService<ItemRandomPropertyFeature>();
            Assert.Equal((1, 1), (feature.Properties!.Catalog.PropertyCount, feature.Properties.Catalog.GroupCount));
            await using WorldTestClient client = await host.EnterWorldAsync("RANDPROP", "Lucky");
            (int property, uint enchant) = await host.PlayerStateAsync("Lucky", p =>
            {
                Assert.Equal(InventoryResult.Ok, p.Inventory.AddItem(Sword, 1, out Item? item));
                return (item!.RandomPropertyId, item.EnchantmentId(3));
            });
            Assert.Equal((1001, 74u), (property, enchant));
        }
        finally
        {
            File.Delete(dbc);
            File.Delete(dump);
        }
    }

    [Fact]
    public async Task WithoutContent_NewItemsHaveNoProperty()
    {
        await using WorldTestHost host = Start(new ItemTestContent(), []);
        Assert.Null(host.WorldServices.GetRequiredService<ItemRandomPropertyFeature>().Properties);
        await using WorldTestClient client = await host.EnterWorldAsync("RANDPROP2", "Unlucky");
        Assert.Equal(0, await host.PlayerStateAsync("Unlucky", p =>
        {
            Assert.Equal(InventoryResult.Ok, p.Inventory.AddItem(Sword, 1, out Item? item));
            return item!.RandomPropertyId;
        }));
    }
}
