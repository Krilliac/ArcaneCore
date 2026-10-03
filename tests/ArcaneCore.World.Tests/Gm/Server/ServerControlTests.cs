using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Server;

/// <summary>
/// The shutdown state machine and the .server commands (vmangos World.cpp:2667-2780,
/// ServerCommands.cpp:302-323,332-495; World.h:64-81; levels Chat.cpp:944-1000). Packet layout
/// from wow_messages smsg_server_message.wowm (u32 type + CString).
/// </summary>
public sealed class ServerControlTests
{
    private static ShutdownScheduler Scheduler(List<(ServerMessageType Type, string Text)> sent, Func<int>? sessions = null)
        => new(sessions ?? (() => 0), (type, text) => sent.Add((type, text)));

    private static void Run(ShutdownScheduler scheduler, int seconds)
    {
        for (int i = 0; i < seconds && !scheduler.StopRequested; i++)
        {
            scheduler.Tick(1);
        }
    }

    [Fact]
    public void Shutdown_AnnouncesAtTheRetailSchedule_ThenStops()
    {
        var sent = new List<(ServerMessageType Type, string Text)>();
        ShutdownScheduler scheduler = Scheduler(sent);

        scheduler.Request(600, ShutdownMask.None, ShutdownScheduler.ShutdownExitCode);
        Assert.Equal([(ServerMessageType.ShutdownTime, "10 Minutes ")], sent);   // World.cpp:2711, show == true

        Run(scheduler, 1000);

        string[] expected =
        [
            "10 Minutes ", "5 Minutes ", "4 Minutes ", "3 Minutes ", "2 Minutes ", "1 Minute ",
            "25 Seconds.", "20 Seconds.", "15 Seconds.", "10 Seconds.",
            "9 Seconds.", "8 Seconds.", "7 Seconds.", "6 Seconds.", "5 Seconds.", "4 Seconds.", "3 Seconds.", "2 Seconds.", "1 Second.",
        ];
        Assert.Equal(expected, sent.Select(m => m.Text));
        Assert.All(sent, m => Assert.Equal(ServerMessageType.ShutdownTime, m.Type));
        Assert.True(scheduler.StopRequested);
        Assert.Equal(0, scheduler.ExitCode);
    }

    [Fact]
    public void Shutdown_HoursAreAnnouncedHourlyBelowTwelveHours()
    {
        var sent = new List<(ServerMessageType Type, string Text)>();
        ShutdownScheduler scheduler = Scheduler(sent);
        scheduler.Request(3 * 3600, ShutdownMask.None, 0);

        // Jump to just before the 2 h mark and across it: only the whole hour is announced.
        scheduler.Tick(3600 - 1);
        Assert.Single(sent);
        scheduler.Tick(1);
        Assert.Equal("2 Hours ", sent[^1].Text);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void Restart_UsesTheRestartMessagesAndExitCode()
    {
        var sent = new List<(ServerMessageType Type, string Text)>();
        ShutdownScheduler scheduler = Scheduler(sent);

        scheduler.Request(90, ShutdownMask.Restart, ShutdownScheduler.RestartExitCode);

        Assert.Equal([(ServerMessageType.RestartTime, "1 Minute 30 Seconds.")], sent);
        Run(scheduler, 100);
        Assert.True(scheduler.StopRequested);
        Assert.Equal(2, scheduler.ExitCode);
    }

    [Fact]
    public void Cancel_SendsTheCancelledMessage_AndResetsEverything()
    {
        var sent = new List<(ServerMessageType Type, string Text)>();
        ShutdownScheduler scheduler = Scheduler(sent);
        scheduler.Request(600, ShutdownMask.Restart, 7);
        sent.Clear();

        scheduler.Cancel();

        Assert.Equal([(ServerMessageType.RestartCancelled, string.Empty)], sent);
        Assert.Equal(0u, scheduler.Timer);
        Assert.Equal(ShutdownMask.None, scheduler.Mask);
        Assert.Equal(ShutdownScheduler.ShutdownExitCode, scheduler.ExitCode);
        Run(scheduler, 1000);
        Assert.False(scheduler.StopRequested);

        sent.Clear();
        scheduler.Cancel();   // nothing pending: silent (World.cpp:2757)
        Assert.Empty(sent);

        scheduler.Request(30, ShutdownMask.None, 0);
        sent.Clear();
        scheduler.Cancel();
        Assert.Equal([(ServerMessageType.ShutdownCancelled, string.Empty)], sent);
    }

    [Fact]
    public void ZeroDelay_StopsAtOnce_WithoutAMessage()
    {
        var sent = new List<(ServerMessageType Type, string Text)>();
        ShutdownScheduler scheduler = Scheduler(sent);

        scheduler.Request(0, ShutdownMask.None, 0);

        Assert.True(scheduler.StopRequested);
        Assert.Empty(sent);
    }

    [Fact]
    public void Idle_SendsNothing_AndWaitsForTheLastSessionToLeave()
    {
        var sent = new List<(ServerMessageType Type, string Text)>();
        int sessions = 3;
        ShutdownScheduler scheduler = Scheduler(sent, () => sessions);

        scheduler.Request(10, ShutdownMask.Idle, 0);
        Run(scheduler, 30);

        Assert.Empty(sent);
        Assert.False(scheduler.StopRequested);          // the timer waits at 1 second (World.cpp:2683)
        Assert.Equal(1u, scheduler.Timer);

        sessions = 0;
        scheduler.Tick(1);
        Assert.True(scheduler.StopRequested);

        // A zero-delay idle request with sessions online arms the 1 second timer instead of stopping.
        var again = Scheduler([], () => 1);
        again.Request(0, ShutdownMask.Idle, 0);
        Assert.False(again.StopRequested);
        Assert.Equal(1u, again.Timer);
    }

    [Fact]
    public void AfterTheStopFlag_FurtherRequestsAreIgnored()
    {
        ShutdownScheduler scheduler = Scheduler([]);
        scheduler.Request(0, ShutdownMask.None, 5);

        scheduler.Request(60, ShutdownMask.Restart, 2);

        Assert.Equal(5, scheduler.ExitCode);
        Assert.Equal(0u, scheduler.Timer);
    }

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
    public async Task ShutdownCommand_BroadcastsTheCountdown_AndCancelBroadcastsTheCancel()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("SRVADM", "Srvadm", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("SRVOTH", "Srvoth");
        await admin.CollectAsync();
        await other.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server shutdown 600");
        Assert.Equal((1u, "10 Minutes "), ReadServerMessage(await other.ReadUntilAsync(WorldOpcode.SmsgServerMessage)));
        Assert.Equal((1u, "10 Minutes "), ReadServerMessage(await admin.ReadUntilAsync(WorldOpcode.SmsgServerMessage)));

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server shutdown cancel");
        Assert.Equal((4u, string.Empty), ReadServerMessage(await other.ReadUntilAsync(WorldOpcode.SmsgServerMessage)));

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server restart 90 3");
        Assert.Equal((2u, "1 Minute 30 Seconds."), ReadServerMessage(await other.ReadUntilAsync(WorldOpcode.SmsgServerMessage)));
        Assert.Equal(3, host.WorldServices.GetRequiredService<ShutdownFeature>().Scheduler.ExitCode);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server restart cancel");
        Assert.Equal((5u, string.Empty), ReadServerMessage(await other.ReadUntilAsync(WorldOpcode.SmsgServerMessage)));
    }

    [Theory]
    [InlineData(".server shutdown")]
    [InlineData(".server shutdown abc")]
    [InlineData(".server shutdown 10 126")]
    [InlineData(".server restart 10 x")]
    public async Task ShutdownCommand_RejectsBadArguments_WithTheSyntaxLine(string command)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("SRVBAD", "Srvbad", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, command);

        Assert.StartsWith("Syntax:", (await admin.ReadChatAsync()).Text);
        Assert.False(host.WorldServices.GetRequiredService<ShutdownFeature>().Scheduler.StopRequested);
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

    private static (uint Type, string Text) ReadServerMessage(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt32(), reader.ReadCString());
    }
}
