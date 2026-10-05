using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class TemporaryEnchantDurationWorldTests
{
    [Theory]
    [InlineData(2000u, 0u, 0u)]
    [InlineData(0u, 49801u, 4u)]
    public async Task MapMaintenance_ExpiresTimedEnchantAndPreservesUntimedChargesAcrossRelog(
        uint duration, uint expectedId, uint expectedCharges)
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 49802, Class = 2, SubClass = 7, InventoryType = 21, Stackable = 1,
        });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, 49802, 1));
        WorldTestHost host;
        using (content.Use())
            host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("ENCHANTTIME", "Enchanttime");
            ulong guid = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Enchanttime")!;
                Item weapon = Assert.Single(player.Inventory.Equipped).Item;
                weapon.SetUInt32(UpdateFields.ItemFieldEnchantment + 3, 49801);
                weapon.SetUInt32(UpdateFields.ItemFieldEnchantment + 4, duration);
                weapon.SetUInt32(UpdateFields.ItemFieldEnchantment + 5, 4);
                player.Map!.Updaters.OfType<ItemMaintenanceUpdater>().Single().Update(player.Map, 2500);
                Assert.Equal(expectedId, weapon.EnchantmentId(1));
                Assert.Equal(0u, weapon.EnchantmentDuration(1));
                Assert.Equal(expectedCharges, weapon.EnchantmentCharges(1));
                return player.Guid.Value;
            });
            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await WorldTestHost.WaitForAsync(() => content.Items.Get((int)guid).Single().Item.Enchantments[3] == expectedId,
                "enchantment state saved");
            Assert.Equal(expectedCharges, content.Items.Get((int)guid).Single().Item.Enchantments[5]);
            await client.LoginAsync(guid);
            Assert.Equal((expectedId, 0u, expectedCharges), await host.PlayerStateAsync("Enchanttime", p =>
            {
                Item item = Assert.Single(p.Inventory.Equipped).Item;
                return (item.EnchantmentId(1), item.EnchantmentDuration(1), item.EnchantmentCharges(1));
            }));
        }
    }
}
