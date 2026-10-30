using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>
/// A GM command that finishes its work off the world thread holds itself open (<see cref="CommandContext.Defer"/>): RA and the control
/// pipe report it done only once <see cref="CommandContext.WhenSettled"/> completes, so the reply (or the secret) must exist by then and a
/// failure must be visible as <see cref="CommandContext.Failure"/> (RemoteAdminServer.RunAsync, WorldControlPipe).
/// </summary>
public sealed class GmAsyncCommandSettlementTests
{
    private static async Task<(CommandContext Context, bool SettledAtHandlerReturn, ConcurrentQueue<string> Replies)> RunAsync(
        WorldTestHost host, WorldSession session, string path, string args)
    {
        var replies = new ConcurrentQueue<string>();
        (CommandContext context, bool early) = await host.OnWorldAsync(() =>
        {
            CommandTable table = ChatCommands.CreateTable();
            CommandContext ctx = CommandContext.ForConsole(session, table, replies.Enqueue);
            ChatCommand command = table.Resolve(path, AccountSecurity.Administrator)!;
            ctx.Bind(command, path); // what CommandTable.Execute does
            Assert.True(command.Handler!(ctx, args));
            ctx.Complete();
            return (ctx, ctx.WhenSettled.IsCompleted);
        });
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));
        return (context, early, replies);
    }

    [Fact]
    public async Task AnAccountFactorCommand_StaysOpenUntilItsReplyWasSent()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ALSADMIN", "Alsadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "account totp set", "NOSUCHACCT");

        Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
        Assert.Equal("Account does not exist.", Assert.Single(replies));
        Assert.Null(context.Failure);
    }

    [Fact]
    public async Task APlayerDumpLoad_StaysOpenUntilItsReplyWasPosted_AndReportsTheFailure()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("PDSADMIN", "Pdsadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "pdump load", "dump.txt nosuchacct Clone");

        Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
        Assert.Equal("Account NOSUCHACCT does not exist.", Assert.Single(replies));
        Assert.Equal("no-such-account", context.Failure);
    }
}
