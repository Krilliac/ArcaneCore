using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Items;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload item_template</c> (vmangos ServerCommands.cpp HandleReloadItemTemplate,
/// ServerCommands.cpp:1744-1749, Chat.cpp:855 → ObjectMgr::LoadItemPrototypes, ObjectMgr.cpp:3814): lookups by entry see the new content at once,
/// for the feature and for every inventory already online; an item a player already holds keeps the
/// record it was created with until the character logs in again (a named deviation: vmangos
/// resolves <c>Item::GetProto</c> by entry on every call, Item.cpp:567-570).
/// </summary>
public sealed class ItemReloadTests
{
    private const uint Jerky = 117;
    private const uint Added = 99001;

    private static ItemTestContent Content()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = Jerky, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20, SellPrice = 1 },
            new ItemTemplate { Entry = 6948, Class = 15, SubClass = 0, Name = "Hearthstone", DisplayId = 6418, Quality = 1, Bonding = 1 },
        ]);
        content.Templates.StartingItems.AddRange([new StartingItem(1, 1, 25, 1), new StartingItem(1, 1, Jerky, 4), new StartingItem(1, 1, 6948, 1)]);
        return content;
    }

    private static WorldTestHost Start(ItemTestContent content)
    {
        using (content.Use())
        {
            return WorldTestHost.Start();
        }
    }

    private static ReloadCoordinator Coordinator(WorldTestHost host) => host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator;

    [Fact]
    public async Task NewAndChangedTemplates_AreVisibleToTheFeature_AndToAnInventoryAlreadyOnline()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("HOLDER", "Holder");
        ItemsFeature items = host.WorldServices.GetRequiredService<ItemsFeature>();
        Assert.Null(items.Templates.Find(Added));
        content.Templates.Templates.Add(new ItemTemplate { Entry = Added, Class = 0, Name = "Added By Reload", DisplayId = 1, Quality = 2 });
        content.Templates.Templates[1] = content.Templates.Templates[1] with { SellPrice = 777 };

        ReloadResult result = await Coordinator(host).ReloadAsync("item_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal("Added By Reload", items.Templates.Find(Added)?.Name);
        Assert.Equal(777u, items.Templates.Find(Jerky)?.SellPrice);
        Assert.Equal("Added By Reload", await host.PlayerStateAsync("Holder", p => p.Inventory.Templates.Find(Added)?.Name));
        Assert.Equal(777u, await host.PlayerStateAsync("Holder", p => p.Inventory.Templates.Find(Jerky)?.SellPrice));
        Assert.Contains($"{content.Templates.Templates.Count} item templates", result.Message);
    }

    [Fact]
    public async Task AnItemAlreadyHeld_KeepsItsCreationRecord_UntilTheNextLogin()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("KEEPER", "Keeper");
        content.Templates.Templates[1] = content.Templates.Templates[1] with { Name = "Tough Jerky Mk2", SellPrice = 777 };

        await Coordinator(host).ReloadAsync("item_template");

        (string heldName, uint heldPrice) = await host.PlayerStateAsync("Keeper", p =>
        {
            Item held = p.Inventory.AllItems.First(i => i.Entry == Jerky);
            return (held.Template.Name, held.Template.SellPrice);
        });
        Assert.Equal("Tough Jerky", heldName);
        Assert.Equal(1u, heldPrice);
        Assert.Equal("Tough Jerky Mk2", await host.PlayerStateAsync("Keeper", p => p.Inventory.Templates.Find(Jerky)?.Name));
    }

    [Fact]
    public async Task AnEmptyTable_KeepsTheLoadedTemplates()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("EMPTY", "Empty");
        ItemsFeature items = host.WorldServices.GetRequiredService<ItemsFeature>();
        int before = items.Templates.Count;
        content.Templates.Templates.Clear();

        ReloadResult result = await Coordinator(host).ReloadAsync("item_template");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Equal(before, items.Templates.Count);
        Assert.NotNull(items.Templates.Find(Jerky));
    }

    [Fact]
    public async Task DuplicateEntries_AreRejected()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("DUPES", "Dupes");
        content.Templates.Templates.Add(content.Templates.Templates[1]);

        ReloadResult result = await Coordinator(host).ReloadAsync("item_template");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Contains(result.Notes, n => n.Contains(Jerky.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReloadingBeforeAnyoneNeededItems_LoadsThemFirst()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        ItemsFeature items = host.WorldServices.GetRequiredService<ItemsFeature>();
        Assert.Equal(0, items.Templates.Count);

        ReloadResult result = await Coordinator(host).ReloadAsync("item_template");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(content.Templates.Templates.Count, items.Templates.Count);
    }

    [Fact]
    public async Task WithoutAnItemContentSource_TheReloadFails_AndNothingChanges()
    {
        await using var host = WorldTestHost.Start();
        ItemsFeature items = host.WorldServices.GetRequiredService<ItemsFeature>();

        ReloadResult result = await Coordinator(host).ReloadAsync("item_template");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("item content", result.Message);
        Assert.Equal(0, items.Templates.Count);
    }

    [Fact]
    public async Task TheStartingOutfit_FollowsTheReload()
    {
        ItemTestContent content = Content();
        await using WorldTestHost host = Start(content);
        ItemsFeature items = host.WorldServices.GetRequiredService<ItemsFeature>();
        await items.EnsureLoadedAsync();
        content.Templates.StartingItems.Add(new StartingItem(1, 1, 6948, 2));

        await Coordinator(host).ReloadAsync("item_template");

        Assert.Equal(4, items.Templates.StartingItems(1, 1).Count);
    }
}
