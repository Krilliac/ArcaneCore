using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Server;

/// <summary>
/// The .server commands: levels, info and motd (vmangos ServerCommands.cpp:302-323; levels Chat.cpp:944-1000). The shutdown
/// state machine and its commands are the ops lane's (tests/ArcaneCore.World.Tests/Ops); the GM lane's duplicate scheduler
/// and its unit tests were removed at wave-2 integration.
/// </summary>
public sealed class ServerControlTests
{
    [Fact]
    public void Levels_ServerShutdownFamilyIsAdministratorOnly()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "server shutdown", "server restart", "server idleshutdown", "server idlerestart", "server shutdown cancel", "server set motd" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.GameMaster));
            Assert.NotNull(table.Resolve(path, AccountSecurity.Administrator));
        }

        Assert.NotNull(table.Resolve("server info", AccountSecurity.Player));
    }

    [Fact]
    public async Task ServerInfo_ShowsRevision_Players_AndUptime()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("SRVINF", "Srvinf");
        await player.CollectAsync();

        await player.SendChatAsync(ChatType.Say, Language.Common, ".server info");

        Assert.StartsWith("Core revision: ", (await player.ReadChatAsync()).Text);
        Assert.Matches(@"^Players online: 1 \(0 queued\)\. Max online: 1 \(0 queued\)\.$", (await player.ReadChatAsync()).Text);
        Assert.Matches(@"^Server uptime: .+(Seconds?|Minutes?|Hours?|Days?)", (await player.ReadChatAsync()).Text.TrimEnd());
    }

    [Fact]
    public async Task SetMotd_ChangesTheMessage_AndMotdShowsIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("SRVMOT", "Srvmot", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server set motd Hello world");
        Assert.Equal("Message of the day changed to:\r", (await admin.ReadChatAsync()).Text);
        Assert.Equal("Hello world", (await admin.ReadChatAsync()).Text);

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server motd");
        Assert.Equal("Current Message of the day: \r", (await admin.ReadChatAsync()).Text);
        Assert.Equal("Hello world", (await admin.ReadChatAsync()).Text);
    }
}
