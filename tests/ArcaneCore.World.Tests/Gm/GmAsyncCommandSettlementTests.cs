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
    /// <summary>
    /// Runs the command as the console and reports whether it settled before a reply existed. That is judged where the order is decided, not by when the handler
    /// returned: each reply line notes whether the command had already settled when it was written (RA would then have sent <c>#done ok</c> first), and at handler
    /// return a settled command counts only if no reply had been written yet. A worker that replies and releases its hold before the world thread gets back from the
    /// handler (a fast store on a loaded box) is the correct order, and must not count as early.
    /// </summary>
    private static async Task<(CommandContext Context, bool SettledBeforeItsReply, ConcurrentQueue<string> Replies)> RunAsync(
        WorldTestHost host, WorldSession session, string path, string args)
    {
        var replies = new ConcurrentQueue<string>();
        CommandContext? running = null;
        int settledBeforeAReply = 0;
        void Output(string line)
        {
            if (Volatile.Read(ref running) is { WhenSettled.IsCompleted: true }) Interlocked.Exchange(ref settledBeforeAReply, 1);
            replies.Enqueue(line);
        }

        (CommandContext context, bool settledWithoutReply) = await host.OnWorldAsync(() =>
        {
            CommandTable table = ChatCommands.CreateTable();
            CommandContext ctx = CommandContext.ForConsole(session, table, Output);
            Volatile.Write(ref running, ctx);
            ChatCommand command = table.Resolve(path, AccountSecurity.Administrator)!;
            ctx.Bind(command, path); // what CommandTable.Execute does
            Assert.True(command.Handler!(ctx, args));
            ctx.Complete();
            // Settled first, replies second: a hold is released only after its reply was enqueued, so a correct command that settled already shows its reply here.
            bool settled = ctx.WhenSettled.IsCompleted;
            return (ctx, settled && replies.IsEmpty);
        });
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));
        return (context, settledWithoutReply || Volatile.Read(ref settledBeforeAReply) != 0, replies);
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
