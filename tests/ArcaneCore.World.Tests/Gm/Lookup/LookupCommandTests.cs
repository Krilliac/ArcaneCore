using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Lookup;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Lookup;

/// <summary>
/// .lookup item, creature, object and tele (vmangos LookupCommands.cpp:167-258, 604-667, 790-850,
/// 1264-1304; levels Chat.cpp:557-575; texts mangos_string 163, 166, 168, 436, 447, 448, 512, 514,
/// 516 and vmangos 1105/1152). Fixtures are synthetic rows, not dump data.
/// </summary>
public sealed class LookupCommandTests
{
    [Fact]
    public void Levels_RootModerator_SubcommandsTicketMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.NotNull(table.Resolve("lookup", AccountSecurity.Moderator));
        foreach (string leaf in new[] { "item", "creature", "object", "tele" })
        {
            Assert.Null(table.Resolve("lookup " + leaf, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve("lookup " + leaf, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task LookupItem_ListsEveryCaseInsensitiveSubstringMatch_ByEntry_WithQualityLinks()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LKITGM", "Lkitgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        bool usable = await host.PlayerStateAsync("Lkitgm", p => p.Inventory.CanUseItem(p.Inventory.Templates.Find(117)!) == InventoryResult.Ok);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup item JERKY");
        Assert.Equal($"117 - |cffffffff|Hitem:117:0:0:0:0:0:0:0|h[Tough Jerky]|h|r {(usable ? "[usable]" : string.Empty)}", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup item o");   // all three names contain an "o": listed by entry
        string first = (await gm.ReadChatAsync()).Text;
        string second = (await gm.ReadChatAsync()).Text;
        Assert.StartsWith("25 - |cffffffff|Hitem:25:", first);
        Assert.StartsWith("117 - |cffffffff|Hitem:117:", second);
        Assert.StartsWith("2362 - |cffffffff|Hitem:2362:", (await gm.ReadChatAsync()).Text);   // Worn Wooden Shield also has an "o"

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup item zzz");
        Assert.Equal("No items found!", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup item");
        Assert.StartsWith("Incorrect syntax.", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task LookupCreatureAndObject_UseTheirEntryLinks()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LKCRGM", "Lkcrgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup creature wolf");
        Assert.Equal("299 - |cffffffff|Hcreature_entry:299|h[Young Wolf]|h|r ", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup creature dragon");
        Assert.Equal("No creatures found!", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup object chest");
        Assert.Equal("2061 - |cffffffff|Hgameobject_entry:2061|h[Battered Chest]|h|r ", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup object door");
        Assert.Equal("No gameobjects found!", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task LookupTele_ListsClickableLocations_OrSaysNoneMatch()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LKTLGM", "Lktlgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup tele STORM");
        Assert.Equal("Locations found are:", (await gm.ReadChatAsync()).Text);
        Assert.Equal("  |cffffffff|Htele:1|h[Stormwind]|h|r", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup tele nothinghere");
        Assert.Equal("There are no teleport locations matching your request.", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup tele");
        Assert.StartsWith("Incorrect syntax.", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public void ResultCap_DefaultsToUnlimited_AndStopsAfterTheLimitWhenSet()
    {
        Assert.Equal(0, new GmOptions().LookupMaxResults);
        Assert.Equal(["a", "b"], LookupText.Limit(["a", "b"], 0, out bool omitted0));
        Assert.False(omitted0);
        Assert.Equal(["a"], LookupText.Limit(["a", "b", "c"], 1, out bool omitted));
        Assert.True(omitted);
        Assert.Equal(["a", "b"], LookupText.Limit(["a", "b"], 2, out bool exact));
        Assert.False(exact);
    }

    private static WorldTestHost Start()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 117, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = 2362, Class = 4, SubClass = 6, Name = "Worn Wooden Shield", DisplayId = 18730, Quality = 1, InventoryType = 14, Armor = 5 },
        ]);
        var wolf = new CreatureTemplate { Entry = 299, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 32 };
        var chest = new GameObjectTemplate { Entry = 2061, Type = 3, DisplayId = 10, Name = "Battered Chest", Data = new uint[GameObjectTemplate.DataCount] };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([wolf], [], [], [], []));
        GameObjectTestStore.Current.Value = new GameObjectTestContext(new GameObjectContent([chest], [], [], [], []), LootContent.Empty);
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
            GameObjectTestStore.Current.Value = null;
        }
    }
}
