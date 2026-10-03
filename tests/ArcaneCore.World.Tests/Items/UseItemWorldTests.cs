using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Items;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// Crafting lane, slice use-item over loopback: CMSG_USE_ITEM (u8 bag, u8 slot, u8 spell index, SpellCastTargets; gtker cmsg_use_item.wowm 1.12)
/// reaches the item use service, heals with the item's ON_USE spell and consumes one potion. The wire layout is verified against the references
/// only (not against a real 1.12.1 client).
/// </summary>
public sealed class UseItemWorldTests
{
    private const string Account = "USEITEM1";
    private const string Name = "Quaffer";
    private const uint Potion = 90500;

    private static ItemTestContent Content()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate
            {
                Entry = Potion, Class = 0, Name = "Test Healing Potion", DisplayId = 1, Quality = 1, Stackable = 5,
                Spells = [new ItemSpell(SpellTestServices.Heal, ItemSpellTriggers.OnUse, -1, 0, -1, 0, -1)],
            }.Normalized(),
        ]);
        content.Templates.StartingItems.AddRange([new StartingItem(1, 1, 25, 1), new StartingItem(1, 1, Potion, 3)]);
        return content;
    }

    private static byte[] UseSelf(Item potion, byte spellIndex = 0) => [potion.BagSlot, potion.Slot, spellIndex, 0, 0];

    [Fact]
    public async Task UsingAPotion_HealsThePlayer_AndConsumesOneOfTheStack()
    {
        Assert.Contains(typeof(UseItemFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
        ItemTestContent content = Content();
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);
            Item potion = await host.PlayerStateAsync(Name, p => p.Inventory.GetItemByGuid(p.Inventory.AllItems.Single(i => i.Entry == Potion).Guid)!);
            uint max = await host.PlayerStateAsync(Name, p => p.MaxHealth);
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Health = 5);

            await client.SendAsync(WorldOpcode.CmsgUseItem, [potion.BagSlot, potion.Slot]);   // short payload: ignored
            await client.SendAsync(WorldOpcode.CmsgUseItem, UseSelf(potion));

            await WorldTestHost.WaitForAsync(
                () => host.PlayerStateAsync(Name, p => p.Inventory.GetItemCount(Potion)).GetAwaiter().GetResult() == 2, "one potion consumed");
            uint health = await host.PlayerStateAsync(Name, p => p.Health);
            Assert.Equal(Math.Min(max, 25u), health);   // 5 + the heal of 20
        }
    }

    [Fact]
    public async Task UsingAPotion_AtFullHealth_IsRefused_AndKeepsTheStack()
    {
        ItemTestContent content = Content();
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);
            Item potion = await host.PlayerStateAsync(Name, p => p.Inventory.AllItems.Single(i => i.Entry == Potion));

            await client.SendAsync(WorldOpcode.CmsgUseItem, UseSelf(potion));
            byte[] reply = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);

            Assert.Equal((byte)ArcaneCore.Game.Spells.SpellCastResult.AlreadyAtFullHealth, reply[5]);
            Assert.Equal(3u, await host.PlayerStateAsync(Name, p => p.Inventory.GetItemCount(Potion)));
        }
    }
}
