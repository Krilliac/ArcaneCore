using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.ItemMechanics;

public sealed class StartingOutfitInventoryTests
{
    [Fact]
    public void OrderedOutfitInputUsesTheExistingStartingItemPlacementPath()
    {
        var store = new ItemTemplateStore([
            new ItemTemplate { Entry = 25, BuyCount = 2, Stackable = 20 },
            new ItemTemplate { Entry = 38, BuyCount = 1 },
        ]);
        var inventory = new PlayerInventory(ObjectGuid.Player(1), Race.Human, Class.Warrior, 1)
        {
            Templates = store,
            GuidAllocator = new ItemGuidAllocator(),
        };

        inventory.AddStartingItems([
            new StartingItem(1, 1, 25, 2),
            new StartingItem(1, 1, 38, 1),
        ]);

        Assert.Equal([25u, 38u], inventory.AllItems.Select(i => i.Entry).ToArray());
        Assert.Equal(2u, inventory.AllItems.Single(i => i.Entry == 25).Count);
    }
}
