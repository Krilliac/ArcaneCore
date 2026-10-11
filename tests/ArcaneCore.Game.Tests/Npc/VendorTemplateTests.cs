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

    // --- load-time validation (vmangos ObjectMgr::IsVendorItemValid 11262-11330, LoadVendorTemplates 10836-10862) ---

    private static NpcContent WithTemplate(uint templateId, params VendorItem[] rows) => NpcContent.Empty with
    {
        VendorTemplateIds = new Dictionary<uint, uint> { [NpcServiceKit.Entry] = templateId },
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

        Assert.Equal([1001u, 1004u], store.VendorItems(NpcServiceKit.Entry).Select(item => item.Item));
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("maxcount (5) for item 1002", StringComparison.Ordinal) && d.Contains("incrtime=0", StringComparison.Ordinal));
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("maxcount=0 for item 1003", StringComparison.Ordinal));
        Assert.Equal(2, store.VendorDiagnostics.Count);
    }

    [Fact]
    public void ATemplateStopsAtTheItemCap_LikeVmangosUint8Max()
    {
        VendorItem[] rows = [.. Enumerable.Range(0, 256).Select(i => new VendorItem { Entry = Template, Item = 5000u + (uint)i, Slot = (uint)i })];
        NpcStore store = new(WithTemplate(Template, rows));

        Assert.Equal(255, store.VendorItems(NpcServiceKit.Entry).Count);
        Assert.Equal(5254u, store.VendorItems(NpcServiceKit.Entry)[^1].Item);
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("too many items", StringComparison.Ordinal) && d.Contains("5255", StringComparison.Ordinal));
    }

    [Fact]
    public void ADuplicateItemInATemplate_KeepsTheFirstBySlot()
    {
        NpcStore store = new(WithTemplate(Template,
            new VendorItem { Entry = Template, Item = 1001, MaxCount = 3, IncrTime = 60, Slot = 5 },
            new VendorItem { Entry = Template, Item = 1001, Slot = 2 }));

        VendorItem kept = Assert.Single(store.VendorItems(NpcServiceKit.Entry));
        Assert.Equal((0u, 0u), (kept.MaxCount, kept.IncrTime)); // slot 2 came first
        Assert.Contains(store.VendorDiagnostics, d => d.Contains("duplicate item 1001", StringComparison.Ordinal));
    }

    [Fact]
    public void ACreatureLinkedToATemplateWithNoRows_AndTemplateRowsNoCreatureUses_AreReported()
    {
        NpcStore store = new(NpcContent.Empty with
        {
            VendorTemplateIds = new Dictionary<uint, uint> { [NpcServiceKit.Entry] = 777 },
            VendorTemplateItems = [new VendorItem { Entry = 888, Item = 1001 }],
        });

        Assert.Empty(store.VendorItems(NpcServiceKit.Entry));
        Assert.Contains($"Creature (Entry: {NpcServiceKit.Entry}) has vendor_id = 777 for nonexistent vendor template", store.VendorDiagnostics);
        Assert.Contains("npc_vendor_template has vendor template 888 not used by any vendors", store.VendorDiagnostics);
    }

    [Fact]
    public void CleanTemplateContent_ReportsNothing()
        => Assert.Empty(new NpcStore(Content()).VendorDiagnostics);
}
