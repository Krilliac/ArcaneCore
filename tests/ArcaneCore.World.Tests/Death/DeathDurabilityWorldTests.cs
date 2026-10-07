using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

public sealed class DeathDurabilityWorldTests
{
    private const uint Entry = 963050;
    private const uint Spell = 963051;
    private const string Account = "DEATHWEAR";
    private const string Name = "Deathwear";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreatureCombatDeath_NotifiesTheVictim_AndPersistsEquipmentOnly(bool enabled)
    {
        ItemTestContent content = Items();
        await using WorldTestHost host = Start(content);
        byte[] key = await host.AddAccountAsync(Account);
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.CreateCharacterAsync(Name);
            await client.LoginAsync(1);
            await client.CollectAsync();
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer(Name)!;
                player.Inventory.Options.DurabilityLossEnable = enabled;
                Creature creature = Creature(host);
                player.Map!.Combat.DealDamage(creature, player, player.Health);
                Assert.False(player.IsAlive);
                Assert.Equal(enabled ? 18u : 20u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability);
                Assert.Equal(20u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.Durability);
            });

            List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectAsync();
            Assert.Empty(Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath).Payload);
        }

        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "dead player disconnected");
        await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "death inventory saved");
        IReadOnlyList<InventoryItemData> saved = content.Items.Get(1);
        Assert.Equal(enabled ? 18u : 20u, Assert.Single(saved, r => r.Slot == InventorySlots.MainHand).Item.Durability);
        Assert.Equal(20u, Assert.Single(saved, r => r.Slot == InventorySlots.ItemStart).Item.Durability);

        await using WorldTestClient relog = await host.ConnectAsync();
        await relog.AuthenticateAsync(Account, key);
        await relog.LoginAsync(1);
        Assert.Equal(enabled ? 18u : 20u, await host.PlayerStateAsync(Name,
            p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatureDamageSpell_RespectsNoDurabilityLossAttribute(bool exempt)
    {
        await using WorldTestHost host = Start(Items());
        await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(Name)!;
            SpellSystem system = host.WorldServices.GetRequiredService<SpellFeature>().System;
            var spell = new SpellInfo { Id = Spell, Name = "Death Wear Spell", AttributesEx3 = exempt ? 0x20u : 0 };
            uint before = player.Health;
            Assert.Equal(before, system.Damage.DealSpellDamage(Creature(host), player, spell, before, false));
            Assert.Equal(DeathState.JustDied, player.Combat.DeathState);
            Assert.Equal(exempt ? 20u : 18u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability);
        });
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectAsync();
        Assert.Equal(exempt ? 0 : 1, packets.Count(p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath));
    }

    [Fact]
    public async Task CreatureInstakillEffect_DoesNotApplyOrdinaryDeathWear()
    {
        await using WorldTestHost host = Start(Items());
        await using WorldTestClient client = await host.EnterWorldAsync(Account, Name);
        await client.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(Name)!;
            SpellSystem system = host.WorldServices.GetRequiredService<SpellFeature>().System;
            var spell = new SpellInfo
            {
                Id = Spell, Name = "Death Wear Instakill", RangeIndex = 4, Range = new SpellRange(0, 30),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.Instakill, TargetA = SpellImplicitTarget.Unit }],
            };
            system.Store = new SpellStore([.. system.Store.All, spell], [], []);
            Assert.Equal(SpellCastResult.CastOk, system.CastSpell(Creature(host), spell.Id, SpellCastTargets.ForUnit(player.Guid), true));
            Assert.Equal(0u, player.Health);
            Assert.Equal(DeathState.JustDied, player.Combat.DeathState);
            Assert.Equal(20u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability);
        });
        Assert.DoesNotContain(await client.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    private static Creature Creature(WorldTestHost host)
        => Assert.Single(host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures);

    private static ItemTestContent Items()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542,
            InventoryType = 21, MaxDurability = 20, Stackable = 1,
        });
        // Two copies: the first equips and the second stays in the backpack.
        content.Templates.StartingItems.Add(new StartingItem(1, 1, 25, 2));
        return content;
    }

    private static WorldTestHost Start(ItemTestContent items)
    {
        var template = new CreatureTemplate
        {
            Entry = Entry, Name = "Death Wear Creature", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = 100, MaxLevelHealth = 100, Faction = 35,
        };
        var spawn = new CreatureSpawn { Guid = Entry, Entry = Entry, MapId = 0, X = -8940, Y = -132, Z = 83.5f };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [spawn], [], [], []));
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start();
            }
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }
}
