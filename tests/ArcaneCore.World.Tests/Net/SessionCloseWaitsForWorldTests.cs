using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Characters.Creation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// A command a session already put on the world thread (EnterWorld past its claim check, or a handler mid-run) resolves services from
/// <see cref="WorldSession.Services"/>, the connection's DI scope. The scope ends when the session task returns, so a closing session
/// waits (bounded) for the close command it posts behind its earlier commands (World.Post is FIFO). The wait never hangs a stopped or
/// stalled world.
/// </summary>
public sealed class SessionCloseWaitsForWorldTests
{
    private static readonly TimeSpan LongGrace = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ACommandOnTheWorldThread_UsesTheSessionScope_BeforeASocketSessionEnds()
    {
        await using var host = WorldTestHost.Start();
        await using AsyncServiceScope scope = host.WorldServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        (Task run, WorldSession session, TcpClient client) = await StartSocketAsync(host, scope.ServiceProvider, new WorldSessionOptions { WorldCloseGrace = LongGrace }, null);
        using (client)
        {
            await AssertScopeOutlivesPendingCommandAsync(host, scope, run, session, () => client.Dispose());
        }
    }

    [Fact]
    public async Task ACommandOnTheWorldThread_UsesTheSessionScope_BeforeAGatewayBackedSessionEnds()
    {
        await using var host = WorldTestHost.Start();
        await host.AddAccountAsync("CLOSEWAIT");
        int accountId = (await host.Accounts.FindByUsernameAsync("CLOSEWAIT"))!.Id;
        await using AsyncServiceScope scope = host.WorldServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        WorldSession session = WorldSession.CreateRemote(
            new NullRemoteOutput(), "127.0.0.1:41003", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions { WorldCloseGrace = LongGrace }, NullLogger.Instance);
        Assert.Equal(AuthResponseCode.Ok, await session.AuthenticateRemoteAsync(accountId, "CLOSEWAIT", 5875, CancellationToken.None));
        session.StartRemote();
        Task run = Task.Run(() => session.RunRemoteAsync(CancellationToken.None));

        await AssertScopeOutlivesPendingCommandAsync(host, scope, run, session, () => session.CloseRemoteFromGateway("test disconnect"));
    }

    [Fact]
    public async Task ADisconnect_WhenTheWorldHasStopped_DoesNotWaitForIt()
    {
        await using var host = WorldTestHost.Start();
        host.World.Stop();
        await using AsyncServiceScope scope = host.WorldServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        (Task run, WorldSession session, TcpClient client) = await StartSocketAsync(host, scope.ServiceProvider, new WorldSessionOptions(), null);
        using (client)
        {
            client.Dispose();
            // The default bound is 10 s: finishing well inside it shows the stopped world was not waited for.
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(SessionState.Closed, session.State);
        }
    }

    [Fact]
    public async Task ADisconnect_WhenTheWorldThreadIsStalled_ProceedsAfterTheBoundAndLogsIt()
    {
        await using var host = WorldTestHost.Start();
        using var gate = new ManualResetEventSlim(false);
        using var blocked = new ManualResetEventSlim(false);
        var log = new LogCapture();
        await using AsyncServiceScope scope = host.WorldServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var options = new WorldSessionOptions { WorldCloseGrace = TimeSpan.FromMilliseconds(300) };
        (Task run, WorldSession session, TcpClient client) = await StartSocketAsync(host, scope.ServiceProvider, options, log.CreateLogger("session"));
        try
        {
            using (client)
            {
                host.World.Post(() =>
                {
                    blocked.Set();
                    gate.Wait(TimeSpan.FromSeconds(30));
                });
                Assert.True(blocked.Wait(TimeSpan.FromSeconds(10)), "the world thread never reached the blocking command");

                client.Dispose();
                await run.WaitAsync(TimeSpan.FromSeconds(10)); // returns while the world thread is still stalled
                Assert.Contains(log.Lines, line => line.Contains("did not run the session close", StringComparison.Ordinal));
            }
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// The world thread is busy with a first command; behind it a command that needs the session's scope. The connection closes. The session task
    /// must stay parked (it would not with a sleep, or with no wait) until the world thread has run that command, and only then may the scope end.
    /// </summary>
    private static async Task AssertScopeOutlivesPendingCommandAsync(WorldTestHost host, AsyncServiceScope scope, Task run, WorldSession session, Action closeConnection)
    {
        using var gate = new ManualResetEventSlim(false);
        using var blocked = new ManualResetEventSlim(false);
        Exception? commandFault = null;
        bool sessionTaskDoneWhenCommandEnded = false;
        var commandDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            host.World.Post(() =>
            {
                blocked.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            });
            Assert.True(blocked.Wait(TimeSpan.FromSeconds(10)), "the world thread never reached the blocking command");
            host.World.Post(() =>
            {
                try
                {
                    _ = session.Services.GetService(typeof(IServiceScopeFactory));
                }
                catch (Exception ex)
                {
                    commandFault = ex;
                }
                finally
                {
                    sessionTaskDoneWhenCommandEnded = run.IsCompleted;
                    commandDone.TrySetResult();
                }
            });

            closeConnection();
            await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session to close");

            // Pending command: the session task must be parked. An unfixed session returns here, and the scope is disposed under the queued command.
            Task first = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMilliseconds(750)));
            bool returnedEarly = ReferenceEquals(first, run);
            if (returnedEarly)
            {
                await scope.DisposeAsync();
            }

            gate.Set(); // the world thread is released: it runs the command, then the close
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            await commandDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(commandFault is not null, commandFault?.GetType().Name + ": " + commandFault?.Message);
            Assert.False(sessionTaskDoneWhenCommandEnded, "the session task had already returned when the pending command finished");
            Assert.False(returnedEarly, "the session task returned while a command was still pending on the world thread");
        }
        finally
        {
            gate.Set();
        }
    }

    private static async Task<(Task Run, WorldSession Session, TcpClient Client)> StartSocketAsync(
        WorldTestHost host, IServiceProvider services, WorldSessionOptions options, ILogger? logger)
    {
        var listener = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        listener.Start();
        try
        {
            var client = new TcpClient();
            Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            TcpClient server = await listener.AcceptTcpClientAsync();
            await connect;
            NetworkStream stream = server.GetStream();
            var session = new WorldSession(
                stream, "127.0.0.1:50001", services, host.Opcodes, host.World, host.Registry, options, logger ?? NullLogger.Instance);
            Task run = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync(CancellationToken.None);
                }
                finally
                {
                    server.Dispose();
                }
            });
            return (run, session, client);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class NullRemoteOutput : IRemoteSessionOutput
    {
        public bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload) => true;
    }
}
