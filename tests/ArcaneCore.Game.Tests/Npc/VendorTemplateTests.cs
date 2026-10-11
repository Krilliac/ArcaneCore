using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>Shared vendor rows: vmangos ObjectMgr::LoadVendorTemplates and ItemHandler::SendListInventory.</summary>
public sealed class VendorTemplateTests
{
    private const uint Template = 133;

    private static NpcContent Content(params VendorItem[] direct) => NpcContent.Empty with
    {
        VendorItems = direct,
        VendorTemplateIds = new Dictionary<uint, uint> { [NpcServiceKit.Entry] = Template },
        VendorTemplateItems =
        [
            new VendorItem { Entry = Template, Item = NpcServiceKit.Lantern, MaxCount = 2, IncrTime = 60, Slot = 1 },
            new VendorItem { Entry = Template, Item = NpcServiceKit.Sword, Slot = 2 },
        ],
    };

    [Fact]
    public void TemplateOnlyVendorListsSellsAndRestocksSharedItems()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Content());
        kit.Player.Money = 10_000;
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] packet = kit.Single(WorldOpcode.SmsgListInventory);
        Assert.Equal(2, packet[8]);
        Assert.Equal(NpcServiceKit.Lantern, BitConverter.ToUInt32(packet, 13));
        Assert.Equal(NpcServiceKit.Sword, BitConverter.ToUInt32(packet, 41));

        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceKit.Lantern, 1);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(NpcServiceKit.Lantern));
        Assert.Contains(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgBuyItem);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceKit.Lantern, 1);
        kit.Drain();
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        Assert.Equal(0u, BitConverter.ToUInt32(kit.Single(WorldOpcode.SmsgListInventory), 21));
        kit.Now += 60;
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        Assert.Equal(1u, BitConverter.ToUInt32(kit.Single(WorldOpcode.SmsgListInventory), 21));
    }

    [Fact]
    public void OwnItemsPrecedeTemplateItemsEvenWhenTemplateSlotIsLower()
    {
        using var kit = new NpcServiceKit(NpcFlags.Vendor, Content(new VendorItem
        {
            Entry = NpcServiceKit.Entry, Item = NpcServiceKit.Bread, Slot = 99,
        }));
        kit.Player.Money = 10_000;
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] packet = kit.Single(WorldOpcode.SmsgListInventory);
        Assert.Equal(3, packet[8]);
        Assert.Equal(NpcServiceKit.Bread, BitConverter.ToUInt32(packet, 13));
        Assert.Equal(NpcServiceKit.Lantern, BitConverter.ToUInt32(packet, 41));
        Assert.Equal(NpcServiceKit.Sword, BitConverter.ToUInt32(packet, 69));
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceKit.Bread, 1);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceKit.Sword, 1);
        Assert.Equal(5u, kit.Player.Inventory.GetItemCount(NpcServiceKit.Bread));
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(NpcServiceKit.Sword));
    }

    [Fact]
    public void DuplicateDirectItemUsesTheTemplateStockAsVmangosValidates()
    {
        NpcStore store = new(Content(new VendorItem
        {
            Entry = NpcServiceKit.Entry, Item = NpcServiceKit.Lantern, MaxCount = 9, IncrTime = 600,
        }));
        VendorItem row = Assert.Single(store.VendorItems(NpcServiceKit.Entry), item => item.Item == NpcServiceKit.Lantern);
        Assert.Equal(2u, row.MaxCount);
        Assert.Equal(60u, row.IncrTime);
    }
}
