using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.send mail/money/items/message (vmangos MiscCommands.cpp HandleSend*Command) on a host without an economy store.</summary>
public sealed class GmSendCommandTests
{
    [Fact]
    public void Send_UsesTheVmangosRetailLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.NotNull(table.Resolve("send mail", AccountSecurity.Moderator));
        foreach (string path in new[] { "send money", "send items", "send message" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.GameMaster));
            Assert.NotNull(table.Resolve(path, AccountSecurity.Administrator));
        }
    }

    [Fact]
    public async Task Mail_ToAnUnknownName_IsPlayerNotFound_AndWithoutAStore_SaysSo()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SENDGM", "Sendgm", AccountSecurity.Administrator);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".send mail Nobodyhere \"Hi\" \"There\"");
        Assert.Equal(GmStrings.PlayerNotFound, (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".send money Sendgm \"Hi\" \"There\" 100");
        Assert.Equal(GmSendCommands.MailUnavailable, (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task Items_ChecksTheItemAndTheOneStackLimit()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = 117, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 });
        WorldTestHost started;
        using (content.Use())
        {
            started = WorldTestHost.Start();
        }

        await using WorldTestHost host = started;
        await using WorldTestClient gm = await host.EnterWorldAsync("SENDITEMS", "Senditems", AccountSecurity.Administrator);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".send items Senditems \"Hi\" \"There\" 987654");
        Assert.Equal(GmStrings.ItemIdInvalid(987654), (await gm.ReadChatAsync()).Text);

        // One more than a full stack of 117 is two stacks.
        uint stack = await host.PlayerStateAsync("Senditems", p => Math.Max(1u, p.Inventory.Templates.Find(117)!.Stackable));
        await gm.SendChatAsync(ChatType.Say, Language.Common, $".send items Senditems \"Hi\" \"There\" 117:{stack + 1}");
        Assert.Equal(GmSendCommands.ItemsLimit, (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task Message_ShowsTheTextAndTheAdministratorHeader()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SENDMSG", "Sendmsg", AccountSecurity.Administrator);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".send message Sendmsg Server restart in 5 minutes");

        Assert.Equal("Server restart in 5 minutes", Text(await gm.ReadUntilAsync(WorldOpcode.SmsgAreaTriggerMessage)));
        Assert.Equal(GmSendCommands.AdministratorHeader, Text(await gm.ReadUntilAsync(WorldOpcode.SmsgAreaTriggerMessage)));
        Assert.Equal(GmSendCommands.MessageSent("Sendmsg", "Server restart in 5 minutes"), (await gm.ReadChatAsync()).Text);
    }

    private static string Text(byte[] payload) => Encoding.UTF8.GetString(payload, 4, BitConverter.ToInt32(payload, 0) - 1);
}