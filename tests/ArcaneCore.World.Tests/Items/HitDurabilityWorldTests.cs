using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Tests.Pets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class HitDurabilityWorldTests
{
    [Fact]
    public async Task WorldPlayerDamage_UsesCombatProducerAndUpdatesDurabilitySnapshot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("HITWEAR", "Hitwear");
        var state = await host.PlayerStateAsync("Hitwear", player =>
        {
            var template = new ItemTemplate { Entry = 980901, Name = "World hit item", Class = 2, SubClass = 7, InventoryType = 21, MaxDurability = 10 };
            player.Inventory.Templates = new ItemTemplateStore([template]);
            player.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand,
                new ItemInstanceData { Guid = 980901, Entry = template.Entry, Durability = 10 })]);
            player.Inventory.Options.DurabilityLossChanceDamage = 100;
            host.World.GetMap(0).Combat.Random = new FixedCombatRandom(InventorySlots.MainHand);
            host.World.GetMap(0).Combat.DealDamage(player, player, 1, direct: false, meleeDamage: false, durabilityLoss: false);
            return player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability;
        });
        Assert.Equal(8u, state); // self damage performs independent hit-taken and hit-done rolls
    }

    private sealed class FixedCombatRandom(int slot) : ArcaneCore.Game.Combat.ICombatRandom
    {
        public int Next(int minInclusive, int maxInclusive) => Math.Clamp(slot, minInclusive, maxInclusive);
        public float NextFloat(float min, float max) => min;
    }
}
