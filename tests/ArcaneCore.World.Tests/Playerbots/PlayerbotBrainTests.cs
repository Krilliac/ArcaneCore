using ArcaneCore.Protocol;
using ArcaneCore.Game.Items;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotBrainTests
{
    [Fact]
    public void ManagedActionPayloadsUseVanillaOpcodeShapes()
    {
        byte[] guid = PlayerbotNavigation.GuidPayload(0x0102030405060708UL);
        Assert.Equal(8, guid.Length);
        Assert.Equal((byte)0x08, guid[0]);

        byte[] food = PlayerbotNavigation.UseItemPayload(InventorySlots.Bag0, InventorySlots.ItemStart);
        Assert.Equal(5, food.Length);
        Assert.Equal((byte)InventorySlots.Bag0, food[0]);
        Assert.Equal((byte)InventorySlots.ItemStart, food[1]);
        Assert.Equal(0, food[2]);
    }
}
