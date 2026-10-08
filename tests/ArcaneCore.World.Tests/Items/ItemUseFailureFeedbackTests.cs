using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

/// <summary>Draft-only feedback assertions for build-5875 item-use eligibility and malformed wire input.</summary>
public sealed class ItemUseFailureFeedbackTests
{
    [Fact]
    public async Task Short_and_trailing_5875_use_item_payloads_are_silent_and_do_not_enter_cast_planning()
    {
        FailureItemContent content = new();
        await using WorldTestHost host = FailureItemContent.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMWIRE", "Itemwire");
        await client.CollectAsync();

        foreach (int length in Enumerable.Range(0, 5))
            await client.SendAsync(WorldOpcode.CmsgUseItem, new byte[length]);
        await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0, 0xFF]);
        Assert.DoesNotContain(await client.CollectAsync(),
            p => p.Opcode is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo or WorldOpcode.SmsgInventoryChangeFailure);
    }

    [Fact]
    public async Task Shapeshifted_non_equipped_item_returns_the_spell_failure_and_does_not_consume()
    {
        FailureItemContent content = new();
        await using WorldTestHost host = FailureItemContent.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMSHIFT", "Itemshift");
        await client.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Itemshift")!.SetByte(UpdateFields.UnitFieldBytes1, 2, 1));

        ulong guid = await host.PlayerStateAsync("Itemshift", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.Guid.Value);
        await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0]);
        byte[] inventoryFailure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);
        Assert.Equal((byte)InventoryResult.None, inventoryFailure[0]);
        Assert.Equal(18, inventoryFailure.Length);
        Assert.Equal(guid, BinaryPrimitives.ReadUInt64LittleEndian(inventoryFailure.AsSpan(1)));
        Assert.Equal(SpellPackets.BuildCastResult(Heal, SpellCastResult.NoItemsWhileShapeshifted),
            await client.ReadUntilAsync(WorldOpcode.SmsgCastResult));
        Assert.Equal(1, await host.PlayerStateAsync("Itemshift", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges)));
        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public async Task Peaceful_item_in_combat_returns_not_in_combat_feedback_without_payment()
    {
        FailureItemContent content = new(includePeacefulItem: true);
        await using WorldTestHost host = FailureItemContent.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("ITEMPEACE", "Itempeace");
        await client.CollectAsync();
        var item = await FailureItemContent.PositionAsync(host, "Itempeace", 99025);
        await host.OnWorldAsync(() =>
        {
            var player = host.World.FindOnlinePlayer("Itempeace")!;
            player.Map!.Combat.SetInCombatState(player, 60_000);
        });

        await client.SendAsync(WorldOpcode.CmsgUseItem, [item.Bag, item.Slot, 0, 0, 0]);
        byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);
        Assert.Equal((byte)InventoryResult.NotInCombat, failure[0]);
        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
        Assert.Equal(1, await host.PlayerStateAsync("Itempeace", p => p.Inventory.GetItemByGuid(new ObjectGuid(item.Guid))!.GetInt32(UpdateFields.ItemFieldSpellCharges)));
    }
}
