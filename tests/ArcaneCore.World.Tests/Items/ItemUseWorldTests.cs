using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

/// <summary>Real loopback coverage for the build-5875 CMSG_USE_ITEM request and cast-item identity.</summary>
public sealed class ItemUseWorldTests
{
    private const string Account = "ITEMUSE";
    private const string Name = "ItemUser";
    private const uint ItemEntry = 99021;

    [Fact]
    public async Task UseItem_UsesTheVanillaThreeBytePrefix_AndReturnsTheCastItemGuid()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = ItemEntry,
            Class = 0,
            Stackable = 1,
            Spells = [new ItemSpell(Heal, 0, 2, 0, 0, 0, 0)],
        });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, ItemEntry, 1));
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync(Account, Name))
        {
            await host.PlayerStateAsync(Name, p => p.Health = Math.Max(1u, p.MaxHealth / 2));
            PlayerState state = await host.PlayerStateAsync(Name, p => new PlayerState(
                p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.Guid.Value,
                p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges)));
            await client.CollectAsync();

            await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0]);
            byte[] start = await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
            ulong casterGuid = await host.PlayerStateAsync(Name, p => p.Guid.Value);
            var reader = new PacketReader(start);
            Assert.Equal(state.Guid, reader.ReadPackedGuid());
            Assert.Equal(casterGuid, reader.ReadPackedGuid());
            Assert.Equal(Heal, reader.ReadUInt32());
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges) == 1, "item charge");

            // Short prefixes are rejected before item lookup, cast creation,
            // cooldown, or charge mutation.
            foreach (int length in Enumerable.Range(0, 5))
            {
                await client.SendAsync(WorldOpcode.CmsgUseItem, new byte[length]);
            }
            Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
            Assert.Equal(1, await host.PlayerStateAsync(Name, p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges)));

            // The TBC cast-count/item-GUID trailer is rejected as a malformed 5875 packet.
            await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0, 0xFF]);
            Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op == WorldOpcode.SmsgSpellGo);
        }
    }

    private readonly record struct PlayerState(ulong Guid, int Charges);
}
