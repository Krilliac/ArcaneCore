using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// ItemMaintenance keeps a per-thread scratch list of the timed items it is ticking. It must be empty once a pass ends, or the
/// last player's items stay strongly referenced (through them the whole Item graph) until the next pass on that thread.
/// </summary>
public sealed class ItemMaintenanceScratchListTests
{
    private const uint TimedStone = 94101; // Duration 1800

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = TimedStone, Class = 12, Name = "Timed Stone", DisplayId = 1, Duration = 1800 },
        ], []);

    // The list is private and [ThreadStatic]; xunit runs a synchronous test on one thread, so this reads the list the Tick calls used.
    private static int ScratchCount()
    {
        FieldInfo field = typeof(ItemMaintenance).GetField("t_timed", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ItemMaintenance.t_timed not found");
        return ((List<Item>?)field.GetValue(null))?.Count ?? 0;
    }

    private static Player Make(uint guid)
    {
        (Player player, _) = CreatePlayer(guid);
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        return player;
    }

    [Fact]
    public void FirstPass_EarlyReturnWithTimedItem_LeavesScratchListEmpty()
    {
        Player player = Make(1);
        Give(player.Inventory, TimedStone);

        ItemMaintenance.Tick(player, 1000); // baselines LastTick and returns early

        Assert.Equal(0, ScratchCount());
    }

    [Fact]
    public void SameSecondPass_EarlyReturnWithTimedItem_LeavesScratchListEmpty()
    {
        Player player = Make(2);
        Give(player.Inventory, TimedStone);
        ItemMaintenance.Tick(player, 1000);

        ItemMaintenance.Tick(player, 1000); // nowSeconds <= LastTick: early return

        Assert.Equal(0, ScratchCount());
    }

    [Fact]
    public void FullTickingPass_LeavesScratchListEmpty()
    {
        Player player = Make(3);
        Item stone = Give(player.Inventory, TimedStone);
        ItemMaintenance.Tick(player, 1000);

        ItemMaintenance.Tick(player, 1010);

        Assert.Equal(1790u, stone.Duration);
        Assert.Equal(0, ScratchCount());
    }

    [Fact]
    public void ExpiryPass_LeavesScratchListEmpty()
    {
        Player player = Make(4);
        Item stone = Give(player.Inventory, TimedStone);
        ItemMaintenance.Tick(player, 1000);

        ItemMaintenance.Tick(player, 1000 + 1800); // destroys the item

        Assert.Null(player.Inventory.GetItemByGuid(stone.Guid));
        Assert.Equal(0, ScratchCount());
    }
}
