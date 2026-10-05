using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

public sealed class ItemEquipSpellWorldTests
{
    private const uint ItemEntry = 4948;
    private const uint EquipSpell = 49210;

    [Fact]
    public async Task EquippedItemSpell_AppliesRemovesReappliesAndRelogKeepsOneOwner()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = ItemEntry, Class = 4, InventoryType = 12, Stackable = 1,
            Spells = [new ItemSpell(EquipSpell, 1, 0, 0, 0, 0, 0)] });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, ItemEntry, 1));
        SpellContent baseContent = SpellTestServices.Content();
        var spell = new SpellTemplateRow { Id = EquipSpell, SpellName = "Equip Aura", RangeIndex = 1, Effect1 = 6,
            EffectBasePoints1 = 0, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1,
            EffectApplyAuraName1 = (uint)AuraType.Dummy, StartRecoveryCategory = 133, StartRecoveryTime = 1500 };
        WorldTestHost host;
        using (items.Use())
        {
            host = WorldTestHost.Start(configure: o => o.LogoutDelayMs = 20,
                configureServices: services => services.AddSingleton<ISpellContentStore>(new Store(baseContent with { Spells = [.. baseContent.Spells, spell] })));
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("EQUIPSPELL", "Equipspell"))
        {
            ulong guid = await host.PlayerStateAsync("Equipspell", p => p.Inventory.Equipped.Single().Item.Guid.Value);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Equipspell")!.Inventory.Equipped.Count() == 1, "equipped");
            Assert.Equal(guid, await host.PlayerStateAsync("Equipspell", p => p.Inventory.Equipped.Single().Item.Guid.Value));
            Assert.Equal(1, await host.PlayerStateAsync("Equipspell", p => host.WorldServices.GetRequiredService<SpellFeature>().System.GetAuras(p).Count(h => h.Spell.Id == EquipSpell && h.ItemGuid.Value == guid)));

            await client.SendAsync(WorldOpcode.CmsgSwapInvItem, [InventorySlots.Trinket1, InventorySlots.ItemStart]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Equipspell")!.Inventory.Equipped.Count() == 0, "unequipped");
            await client.SendAsync(WorldOpcode.CmsgAutoequipItem, [InventorySlots.Bag0, InventorySlots.ItemStart]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Equipspell")!.Inventory.Equipped.Count() == 1, "reequipped");

            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "logout");
            Account account = (await host.Accounts.FindByUsernameAsync("EQUIPSPELL"))!;
            CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            await client.LoginAsync((ulong)character.Id);
            Assert.Equal(1, await host.PlayerStateAsync("Equipspell", p => host.WorldServices.GetRequiredService<SpellFeature>().System.GetAuras(p).Count(h => h.Spell.Id == EquipSpell && h.ItemGuid.Value == guid)));
        }
    }

    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
