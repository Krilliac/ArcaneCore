using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class EnchantSpellWorldTests
{
    [Theory]
    [InlineData(53u, 0, 0u)]
    [InlineData(54u, 1, 2u)]
    public async Task SocketEnchant_AppliesOwnedStatsAndPersistsAcrossRelog(uint effect, int slot, uint charges)
    {
        const uint spellId = 50201;
        const uint enchantId = 50202;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 50203, Class = 2, SubClass = 7, InventoryType = 21, Stackable = 1 });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, 50203, 1));
        SpellContent content = SpellTestServices.Content();
        content = content with { Spells = [.. content.Spells, new SpellTemplateRow
        {
            Id = spellId, SpellName = "Synthetic owned enchant", RangeIndex = 1, Effect1 = effect, EquippedItemClass = -1,
            EffectBasePoints1 = 9, EffectBaseDice1 = 1, EffectDieSides1 = 1,
            EffectMiscValue1 = (int)enchantId, EffectImplicitTargetA1 = 1,
        }] };
        WorldTestHost host;
        using (items.Use())
            host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player,
                configureServices: services =>
                {
                    services.AddSingleton<ISpellContentStore>(new Store(content));
                    // The enchanting catalog (SpellItemEnchantment.dbc rows): one Stat effect, +5 strength (ITEM_MOD_STRENGTH = 4).
                    services.AddSingleton(new ArcaneCore.Kernel.Crafting.EnchantCatalog([
                        new ArcaneCore.Kernel.Crafting.SpellItemEnchantment(enchantId, [5, 0, 0], [5, 0, 0], [4, 0, 0], "Strength", 0, 0)]));
                    services.AddSingleton<ISpellEnchantChargesStore>(new ChargesStore());
                });
        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("ENCHANTSPELL", "Enchantspell");
            (ulong playerGuid, ObjectGuid itemGuid, uint strength) = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Enchantspell")!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                Assert.NotNull(player.Enchantments);
                Assert.Equal(2u, spells.System.SpellEnchantCharges.Find(spellId));
                Assert.Null(spells.System.SpellEnchantCharges.Find(50299));
                spells.Spellbook.LearnSpell(player, spellId);
                return (player.Guid.Value, Assert.Single(player.Inventory.Equipped).Item.Guid,
                    player.GetUInt32(UpdateFields.UnitFieldStat0));
            });
            var writer = new PacketWriter();
            writer.WriteUInt32(spellId);
            new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = itemGuid }.Write(writer);
            await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
            Assert.Equal(SpellPackets.BuildCastResult(spellId, SpellCastResult.CastOk),
                await client.ReadUntilAsync(WorldOpcode.SmsgCastResult));
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Enchantspell")!;
                Item item = player.Inventory.GetItemByGuid(itemGuid)!;
                Assert.Equal(enchantId, item.EnchantmentId(slot));
                Assert.Equal(charges, item.EnchantmentCharges(slot));
                Assert.Equal(strength + 5, player.GetUInt32(UpdateFields.UnitFieldStat0));
            });
            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await WorldTestHost.WaitForAsync(() => items.Items.Get((int)playerGuid).Single().Item.Enchantments[slot * 3] == enchantId,
                "owned enchant saved");
            await client.LoginAsync(playerGuid);
            Assert.Equal(strength + 5, await host.PlayerStateAsync("Enchantspell", p => p.GetUInt32(UpdateFields.UnitFieldStat0)));
            if (slot == 1)
                await host.OnWorldAsync(() =>
                {
                    Player player = host.World.FindOnlinePlayer("Enchantspell")!;
                    player.Enchantments!.Update(15000); // UpdateEnchantTime: the 10 s temporary enchantment expires
                    Assert.Equal(strength, player.GetUInt32(UpdateFields.UnitFieldStat0));
                    Assert.Equal(0u, player.Inventory.GetItemByGuid(itemGuid)!.EnchantmentId(slot));
                });
        }
    }

    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ChargesStore : ISpellEnchantChargesStore
    {
        public Task<IReadOnlyList<SpellEnchantCharges>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpellEnchantCharges>>([new(50201, 2), new(50299, 7)]);
    }
}
