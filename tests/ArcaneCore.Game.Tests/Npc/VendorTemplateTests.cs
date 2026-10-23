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
        VendorTemplateIds = new Dictionary<uint, uint> { [NpcServiceTestKit.Entry] = Template },
        VendorTemplateItems =
        [
            new VendorItem { Entry = Template, Item = NpcServiceTestKit.Lantern, MaxCount = 2, IncrTime = 60, Slot = 1 },
            new VendorItem { Entry = Template, Item = NpcServiceTestKit.Sword, Slot = 2 },
        ],
    };

    [Fact]
    public void TemplateOnlyVendorListsSellsAndRestocksSharedItems()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Vendor, Content());
        kit.Player.Money = 10_000;
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] packet = kit.Single(WorldOpcode.SmsgListInventory);
        Assert.Equal(2, packet[8]);
        Assert.Equal(NpcServiceTestKit.Lantern, BitConverter.ToUInt32(packet, 13));
        Assert.Equal(NpcServiceTestKit.Sword, BitConverter.ToUInt32(packet, 41));

        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceTestKit.Lantern, 1);
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(NpcServiceTestKit.Lantern));
        Assert.Contains(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgBuyItem);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceTestKit.Lantern, 1);
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
        using var kit = new NpcServiceTestKit(NpcFlags.Vendor, Content(new VendorItem
        {
            Entry = NpcServiceTestKit.Entry, Item = NpcServiceTestKit.Bread, Slot = 99,
        }));
        kit.Player.Money = 10_000;
        kit.Services.ListInventory(kit.Player, kit.Npc.Guid);
        byte[] packet = kit.Single(WorldOpcode.SmsgListInventory);
        Assert.Equal(3, packet[8]);
        Assert.Equal(NpcServiceTestKit.Bread, BitConverter.ToUInt32(packet, 13));
        Assert.Equal(NpcServiceTestKit.Lantern, BitConverter.ToUInt32(packet, 41));
        Assert.Equal(NpcServiceTestKit.Sword, BitConverter.ToUInt32(packet, 69));
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceTestKit.Bread, 1);
        kit.Services.BuyItem(kit.Player, kit.Npc.Guid, NpcServiceTestKit.Sword, 1);
        Assert.Equal(5u, kit.Player.Inventory.GetItemCount(NpcServiceTestKit.Bread));
        Assert.Equal(1u, kit.Player.Inventory.GetItemCount(NpcServiceTestKit.Sword));
    }

    [Fact]
    public void DuplicateDirectItemUsesTheTemplateStockAsVmangosValidates()
    {
        NpcStore store = new(Content(new VendorItem
        {
            Entry = NpcServiceTestKit.Entry, Item = NpcServiceTestKit.Lantern, MaxCount = 9, IncrTime = 600,
        }));
        VendorItem row = Assert.Single(store.VendorItems(NpcServiceTestKit.Entry), item => item.Item == NpcServiceTestKit.Lantern);
        Assert.Equal(2u, row.MaxCount);
        Assert.Equal(60u, row.IncrTime);
    }

    // --- load-time validation (vmangos ObjectMgr::IsVendorItemValid 11262-11330, LoadVendorTemplates 10836-10862) ---

    private static NpcContent WithTemplate(uint templateId, params VendorItem[] rows) => NpcContent.Empty with
    {
        VendorTemplateIds = new Dictionary<uint, uint> { [NpcServiceTestKit.Entry] = templateId },
        VendorTemplateItems = rows,
    };

    [Fact]
    public void TemplateRowsWithMismatchedMaxCountAndIncrTime_AreSkipped_AndReported()
    {
        NpcStore store = new(WithTemplate(Template,
            new VendorItem { Entry = Template, Item = 1001, Slot = 1 },
            new VendorItem { Entry = Template, Item = 1002, MaxCount = 5, IncrTime = 0, Slot = 2 },
            new VendorItem { Entry = Template, Item = 1003, MaxCount = 0, IncrTime = 30, Slot = 3 },
            new VendorItem { Entry = Template, Item = 1004, MaxCount = 5, IncrTime = 30, Slot = 4 }));

        Assert.Equal([1001u, 1004u], store.VendorItems(NpcServiceTestKit.Entry).Select(item => item.Item));
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("maxcount (5) for item 1002", StringComparison.Ordinal) && d.Contains("incrtime=0", StringComparison.Ordinal));
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("maxcount=0 for item 1003", StringComparison.Ordinal));
        Assert.Equal(2, store.VendorDiagnostics.Count);
    }

    [Fact]
    public void ATemplateStopsAtTheItemCap_LikeVmangosUint8Max()
    {
        VendorItem[] rows = [.. Enumerable.Range(0, 256).Select(i => new VendorItem { Entry = Template, Item = 5000u + (uint)i, Slot = (uint)i })];
        NpcStore store = new(WithTemplate(Template, rows));

        Assert.Equal(255, store.VendorItems(NpcServiceTestKit.Entry).Count);
        Assert.Equal(5254u, store.VendorItems(NpcServiceTestKit.Entry)[^1].Item);
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("too many items", StringComparison.Ordinal) && d.Contains("5255", StringComparison.Ordinal));
    }

    [Fact]
    public void ADuplicateItemInATemplate_KeepsTheFirstBySlot()
    {
        NpcStore store = new(WithTemplate(Template,
            new VendorItem { Entry = Template, Item = 1001, MaxCount = 3, IncrTime = 60, Slot = 5 },
            new VendorItem { Entry = Template, Item = 1001, Slot = 2 }));

        VendorItem kept = Assert.Single(store.VendorItems(NpcServiceTestKit.Entry));
        Assert.Equal((0u, 0u), (kept.MaxCount, kept.IncrTime)); // slot 2 came first
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("duplicate item 1001", StringComparison.Ordinal));
    }

    [Fact]
    public void ACreatureLinkedToATemplateWithNoRows_AndTemplateRowsNoCreatureUses_AreReported()
    {
        NpcStore store = new(NpcContent.Empty with
        {
            VendorTemplateIds = new Dictionary<uint, uint> { [NpcServiceTestKit.Entry] = 777 },
            VendorTemplateItems = [new VendorItem { Entry = 888, Item = 1001 }],
        });

        Assert.Empty(store.VendorItems(NpcServiceTestKit.Entry));
        Assert.Contains($"Creature (Entry: {NpcServiceTestKit.Entry}) has vendor_id = 777 for nonexistent vendor template", store.VendorDiagnostics);
        Assert.Contains("npc_vendor_template has vendor template 888 not used by any vendors", store.VendorDiagnostics);
    }

    [Fact]
    public void CleanTemplateContent_ReportsNothing()
        => Assert.Empty(new NpcStore(Content()).VendorDiagnostics);
}
