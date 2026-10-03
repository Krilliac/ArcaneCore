using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>The item maintenance feature is attached to the maps a player stands in, and the options reach the inventory.</summary>
public sealed class ItemMaintenanceWorldTests
{
    private const string Account = "ITEMSMAINT";
    private const string Name = "Timekeeper";

    [Fact]
    public async Task PlayerMap_HasTheMaintenanceUpdater_AndTheInventoryHasRetailOptions()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, InventoryType = 21 });
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);
            Assert.Equal(1, await host.PlayerStateAsync(Name, p => p.Map!.Updaters.OfType<ItemMaintenanceUpdater>().Count()));
            Assert.True(await host.PlayerStateAsync(Name, p => p.Inventory.Options.DurabilityLossEnable));
            Assert.Equal(1000, await host.PlayerStateAsync(Name, p => p.Inventory.Options.ZoneLimitCheckMs));
        }
    }
}
