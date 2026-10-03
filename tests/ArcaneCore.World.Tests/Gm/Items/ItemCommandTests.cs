using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Items;

/// <summary>
/// .additem and .deleteitem (vmangos CharacterCommands.cpp:3267-3450; levels Chat.cpp:1277-1278).
/// Reply texts: mangos-classic mangos_string 434, 435, 496, 497, 499 (see GmStrings).
/// </summary>
public sealed class ItemCommandTests
{
    private const uint Jerky = 117;
    private const uint Shortsword = 25;
    private const uint Hearthstone = 6948;

    [Fact]
    public void Commands_NeedTheGameMasterLevel()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string command in new[] { "additem", "deleteitem" })
        {
            Assert.Null(table.Resolve(command, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(command, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task AddItem_StoresTheStackOnTheCaller_AndSendsThePushResult()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("ITEMGM1", "Itemgm", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        uint before = await host.PlayerStateAsync("Itemgm", p => p.Inventory.GetItemCount(Jerky));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".additem 117 5");
        byte[] push = await gm.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);

        Assert.Equal(before + 5, await host.PlayerStateAsync("Itemgm", p => p.Inventory.GetItemCount(Jerky)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(8)));   // received
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(12)));  // created
        Assert.Equal(Jerky, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(25)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(37)));
    }

    [Fact]
    public async Task AddItem_ResolvesIdsLinksAndExactNames_FirstEntryWinsAmongDuplicates()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("ITEMGM2", "Itemgmb", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Assert.Null(await Run(gm, ".additem |cffffffff|Hitem:117:0:0:0:0:0:0:0|h[Tough Jerky]|h|r 2", expectReply: false));
        Assert.Null(await Run(gm, ".additem [Worn Shortsword]", expectReply: false));
        Assert.Null(await Run(gm, ".additem Shared", expectReply: false));   // two templates are called "Shared"
        Assert.Equal(2u + 4, await host.PlayerStateAsync("Itemgmb", p => p.Inventory.GetItemCount(Jerky)));
        Assert.Equal(1u, await host.PlayerStateAsync("Itemgmb", p => p.Inventory.GetItemCount(30000)));
        Assert.Equal(0u, await host.PlayerStateAsync("Itemgmb", p => p.Inventory.GetItemCount(30001)));

        Assert.Equal("Could not find 'Nosuchitem'", await Run(gm, ".additem Nosuchitem"));
        Assert.Equal("Invalid item id: 999999", await Run(gm, ".additem 999999"));
    }

    [Fact]
    public async Task AddItem_WithTooLittleRoom_StoresWhatFits_AndReportsTheRest()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("ITEMGM3", "Itemgmc", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        uint before = await host.PlayerStateAsync("Itemgmc", p => p.Inventory.GetItemCount(Shortsword));

        string? reply = await Run(gm, ".additem 25 100");
        uint stored = await host.PlayerStateAsync("Itemgmc", p => p.Inventory.GetItemCount(Shortsword)) - before;

        Assert.InRange(stored, 1u, 99u);
        Assert.Equal($"Cannot create item '25' (amount: {100 - stored})", reply);

        // A full bag stores nothing: the same text with the whole amount.
        Assert.Equal("Cannot create item '25' (amount: 5)", await Run(gm, ".additem 25 5"));
    }

    [Fact]
    public async Task AddItem_ForAnotherPlayer_PushesToBoth_AndKeepsTheirBinding_WhileSelfGrantsAreUnbound()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("ITEMGM4", "Itemgmd", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await host.EnterWorldAsync("ITEMVIC4", "Itemvicd");
        await gm.CollectAsync();
        await victim.CollectAsync();

        // Own grant of a bind-on-pickup item stays unbound (vmangos SetBinding(false)).
        Assert.Null(await Run(gm, ".additem 6948", expectReply: false));
        Assert.False(await host.PlayerStateAsync("Itemgmd", p => p.Inventory.AllItems.Where(i => i.Entry == Hearthstone).Any(i => i.IsSoulBound)));

        await SelectAsync(host, "Itemgmd", "Itemvicd");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".additem 6948");
        byte[] toVictim = await victim.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
        byte[] toGm = await gm.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(toVictim.AsSpan(8)));  // received
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(toVictim.AsSpan(12))); // not created
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(toGm.AsSpan(8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(toGm.AsSpan(12)));
        Assert.Equal(1u, await host.PlayerStateAsync("Itemgmd", p => p.Inventory.GetItemCount(Hearthstone)));
        Assert.Equal(1u, await host.PlayerStateAsync("Itemvicd", p => p.Inventory.GetItemCount(Hearthstone)));
        Assert.True(await host.PlayerStateAsync("Itemvicd", p => p.Inventory.AllItems.Where(i => i.Entry == Hearthstone).Any(i => i.IsSoulBound)));
    }

    [Fact]
    public async Task AddItem_WithANegativeCount_RemovesWhenHeld_AndRefusesOtherwise()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("ITEMGM5", "Itemgme", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Assert.Equal("Removed itemID = 117, amount = 3 from |cffffffff|Hplayer:Itemgme|h[Itemgme]|h|r", await Run(gm, ".additem 117 -3"));
        Assert.Equal(1u, await host.PlayerStateAsync("Itemgme", p => p.Inventory.GetItemCount(Jerky)));
        Assert.Equal("Cannot remove 50 instances of 117 - maximum value is 1", await Run(gm, ".additem 117 -50"));
    }

    [Fact]
    public async Task DeleteItem_RemovesFromSelfOrANamedPlayer_AndChecksTheirStock()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("ITEMGM6", "Itemgmf", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await host.EnterWorldAsync("ITEMVIC6", "Itemvicf");
        await gm.CollectAsync();

        Assert.Null(await Run(gm, ".deleteitem 117 2", expectReply: false));
        Assert.Equal(2u, await host.PlayerStateAsync("Itemgmf", p => p.Inventory.GetItemCount(Jerky)));
        Assert.Equal("Cannot remove 9 instances of 117 - maximum value is 2", await Run(gm, ".deleteitem 117 9"));

        Assert.Null(await Run(gm, ".deleteitem 117 1 itemvicf", expectReply: false));   // names are case-normalised
        Assert.Equal(3u, await host.PlayerStateAsync("Itemvicf", p => p.Inventory.GetItemCount(Jerky)));
        Assert.Equal("Player not found!", await Run(gm, ".deleteitem 117 1 Nobodyhere"));
        Assert.Equal("Cannot remove 1 instances of 999999 - maximum value is 0", await Run(gm, ".deleteitem 999999"));   // vmangos has no prototype check here
    }

    private static async Task SelectAsync(WorldTestHost host, string caller, string target)
    {
        Player targetPlayer = await host.PlayerAsync(target);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(caller)!.Selection = targetPlayer.Guid);
    }

    /// <summary>Send a command; the next chat line (the reply) or null when it produced none.</summary>
    private static async Task<string?> Run(WorldTestClient client, string command, bool expectReply = true)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        if (expectReply)
        {
            return (await client.ReadChatAsync()).Text;
        }

        // Flush: a following sentinel command's reply proves nothing else was said before it.
        await client.SendChatAsync(ChatType.Say, Language.Common, ".help");
        string next = (await client.ReadChatAsync()).Text;
        Assert.StartsWith("Commands available", next);
        return null;
    }

    private static WorldTestHost Start()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = 38, Class = 4, SubClass = 0, Name = "Recruit's Shirt", DisplayId = 9891, Quality = 1, InventoryType = 4 },
            new ItemTemplate { Entry = 117, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = 6948, Class = 15, SubClass = 0, Name = "Hearthstone", DisplayId = 6418, Quality = 1, Bonding = 1 },
            new ItemTemplate { Entry = 30001, Class = 15, SubClass = 0, Name = "Shared", DisplayId = 1, Quality = 1 },
            new ItemTemplate { Entry = 30000, Class = 15, SubClass = 0, Name = "Shared", DisplayId = 2, Quality = 1 },
        ]);
        content.Templates.StartingItems.AddRange([new StartingItem(1, 1, 38, 1), new StartingItem(1, 1, 117, 4)]);
        using (content.Use())
        {
            return WorldTestHost.Start();
        }
    }
}
