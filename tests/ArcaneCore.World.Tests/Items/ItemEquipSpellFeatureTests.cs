using System.Buffers.Binary;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>The "ItemSets" configuration section and the feature that loads ItemSet.dbc and binds the equip hook at login.</summary>
public sealed class ItemEquipSpellFeatureTests
{
    private const uint StackingBand = 97100;
    private const string RelogAccount = "EQUIPRELOG";
    private const string RelogName = "Equiprelog";
    private static IConfiguration Config(string? path) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ItemSets:DbcPath"] = path,
    }).Build();

    private static byte[] OneSetImage()
    {
        const int fields = 45;
        byte[] image = new byte[20 + (fields * 4) + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 55);              // id
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (27 * 4)), 7000); // first set spell
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (35 * 4)), 2);    // its threshold
        return image;
    }

    [Fact]
    public void WithoutConfiguration_NoDbcIsConfigured()
        => Assert.Null(ItemEquipSpellFeature.Bind(null).DbcPath);

    [Fact]
    public void TheSectionSetsTheDbcPath()
        => Assert.Equal("/data/ItemSet.dbc", ItemEquipSpellFeature.Bind(Config("/data/ItemSet.dbc")).DbcPath);

    [Fact]
    public async Task WithoutAFile_TheFeatureRunsWithAnEmptyCatalog_AndTheEquipHookIsBound()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        ItemEquipSpellFeature feature = host.WorldServices.GetRequiredService<ItemEquipSpellFeature>();

        Assert.Equal(0, feature.Catalog.Count);
        Assert.NotNull(feature.Spells);
    }

    [Fact]
    public async Task AConfiguredFile_IsLoadedIntoTheCatalog()
    {
        string path = Path.Combine(Path.GetTempPath(), $"itemset-{Guid.NewGuid():N}.dbc");
        File.WriteAllBytes(path, OneSetImage());
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton(Config(path)));

            ItemEquipSpellFeature feature = host.WorldServices.GetRequiredService<ItemEquipSpellFeature>();

            Assert.Equal(path, feature.Options.DbcPath);
            Assert.Equal(7000u, feature.Catalog.Find(55)!.SpellIds[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AConfiguredFileThatIsMissing_RefusesStartup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"itemset-missing-{Guid.NewGuid():N}.dbc");

        Assert.ThrowsAny<Exception>(() => WorldTestHost.Start(configureServices: s => s.AddSingleton(Config(path))));
    }

    /// <summary>
    /// The real login order through the feature host: <see cref="SpellFeature"/> restores the saved auras in its PlayerLoggedIn handler, and
    /// the equip replay must run after that restore. Features attach in full-name order (Items before Spells), so a plain PlayerLoggedIn
    /// handler on the items feature would replay first and the restored copy of the equip aura would stack on the item-bound one.
    /// A saved buff and a saved non-passive Equip: aura both come back exactly once, the equip aura still bound to its item.
    /// </summary>
    [Fact]
    public async Task Relog_RestoresASavedAuraOnce_AndReappliesTheEquipSpellOnce_BoundToItsItem()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = StackingBand, Class = 4, SubClass = 0, Name = "Stacking Band", DisplayId = 1, Quality = 2, InventoryType = 11,
            Spells = [new ItemSpell(SpellTestServices.StackingEquipSpell, ItemSpellTriggers.OnEquip, 0, 0, -1, 0, -1)],
        });
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            byte[] key = await host.AddAccountAsync(RelogAccount);
            SpellFeature spells;
            InMemoryCharacterSpellStateStore store;
            CharacterRecord character;
            await using (WorldTestClient first = await host.ConnectAsync())
            {
                await first.AuthenticateAsync(RelogAccount, key);
                await first.CreateCharacterAsync(RelogName);
                Account account = (await host.Accounts.FindByUsernameAsync(RelogAccount))!;
                character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
                await first.LoginAsync((ulong)character.Id);

                Player player = await host.PlayerAsync(RelogName);
                spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
                store = (InMemoryCharacterSpellStateStore)((WorldSession)player.Session).Services.GetRequiredService<ICharacterSpellStateStore>();
                await host.OnWorldAsync(() =>
                {
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(StackingBand, 1, out Item? band));
                    Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquipItem(InventorySlots.NullSlot, out _, band!.Template, band, swap: true));
                    player.Inventory.AutoEquipItem(band.BagSlot, band.Slot);
                    Assert.Equal((InventorySlots.Bag0, InventorySlots.Finger1), (band.BagSlot, band.Slot));
                    Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, SpellTestServices.Renew, SpellCastTargets.ForSelf(), triggered: true));
                    SpellAuraHolder equip = Assert.Single(spells.System.GetAuras(player), h => h.Spell.Id == SpellTestServices.StackingEquipSpell);
                    Assert.Equal(band.Guid, equip.CastItemGuid);
                });
            }

            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session leaves the world");
            await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the inventory save");
            await spells.State.FlushAsync();
            // The equip aura is not passive, so the logout save keeps it next to the buff: the login restore brings both back.
            CharacterSpellState saved = await store.LoadAsync(character.Id);
            Assert.Equal([SpellTestServices.Renew, SpellTestServices.StackingEquipSpell], saved.Auras.Select(a => a.Spell).Order());

            await using WorldTestClient second = await host.ConnectAsync();
            await second.AuthenticateAsync(RelogAccount, key);
            await second.LoginAsync((ulong)character.Id);
            Player again = await host.PlayerAsync(RelogName);
            await host.OnWorldAsync(() =>
            {
                Item band = Assert.Single(again.Inventory.Equipped, e => e.Item.Template.Entry == StackingBand).Item;
                IReadOnlyList<SpellAuraHolder> holders = [.. spells.System.GetAuras(again).Where(h => !h.IsRemoved)];
                Assert.Single(holders, h => h.Spell.Id == SpellTestServices.Renew);
                SpellAuraHolder equip = Assert.Single(holders, h => h.Spell.Id == SpellTestServices.StackingEquipSpell);
                Assert.Equal((byte)1, equip.StackAmount);
                Assert.Equal(band.Guid, equip.CastItemGuid);

                // Still bound to the item: taking it off removes the equip aura and nothing else.
                again.Inventory.RemoveItem(band.BagSlot, band.Slot);
                Assert.DoesNotContain(spells.System.GetAuras(again), h => h.Spell.Id == SpellTestServices.StackingEquipSpell && !h.IsRemoved);
                Assert.Single(spells.System.GetAuras(again), h => h.Spell.Id == SpellTestServices.Renew && !h.IsRemoved);
            });
        }
    }
}
