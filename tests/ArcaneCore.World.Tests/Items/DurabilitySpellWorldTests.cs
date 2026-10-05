using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class DurabilitySpellWorldTests
{
    private const uint SpellId = 993411;
    private const uint SwordEntry = 25;

    [Theory]
    [InlineData(SpellEffectName.DurabilityDamage, 5)]
    [InlineData(SpellEffectName.DurabilityDamagePct, 25)]
    public async Task KnownDurabilitySpell_ClientCastChangesOwnedItemAndSurvivesLogoutRelog(SpellEffectName effect, int value)
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = SwordEntry, Name = "Durability test sword", Class = 2, SubClass = 7, InventoryType = 21,
            DisplayId = 1542, MaxDurability = 20, Delay = 1800, Damages = [new ItemDamage(5, 7, 0)],
        });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, SwordEntry, 1));
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("DURABLE", "Durable");
            ulong playerGuid = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Durable")!;
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
                {
                    Id = SpellId, Name = "Test durability damage", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                    Range = new SpellRange(0, 0),
                    Effects = [new SpellEffectInfo { Effect = effect, BasePoints = value - 1, BaseDice = 1, DieSides = 1,
                        MiscValue = InventorySlots.MainHand, TargetA = SpellImplicitTarget.UnitCaster }],
                }], [], []);
                feature.Spellbook.LearnSpell(player, SpellId);
                Assert.Equal(20u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability);
                return player.Guid.Value;
            });
            await client.CollectAsync();
            var writer = new PacketWriter();
            writer.WriteUInt32(SpellId);
            SpellCastTargets.ForSelf().Write(writer);

            await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
            await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Durable")!;
                Item sword = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;
                Assert.Equal(15u, sword.Durability);
                Assert.Equal(15u, sword.GetUInt32(UpdateFields.ItemFieldDurability));
                Assert.Equal(15u, Assert.Single(player.CreateSnapshot(0).Inventory!.Items).Item.Durability);
            });
            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await WorldTestHost.WaitForAsync(() => content.Items.Get((int)playerGuid).Single().Item.Durability == 15, "durability saved");
            Assert.Equal(15u, Assert.Single(content.Items.Get((int)playerGuid)).Item.Durability);
            await client.LoginAsync(playerGuid);
            Assert.Equal(15u, await host.PlayerStateAsync("Durable", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability));
        }
    }
}
