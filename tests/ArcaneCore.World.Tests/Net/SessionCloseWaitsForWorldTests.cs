using System.Net;
using System.Net.Sockets;
using ArcaneCore.World.Tests.Characters.Creation;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// A command a session queued for the world thread (EnterWorld resolves services from <see cref="WorldSession.Services"/>) must finish
/// before the connection's DI scope is disposed. The scope ends when <see cref="WorldSession.RunAsync"/> returns, so a closing session
/// waits (bounded) for the close command it posts behind its earlier commands (World.Post is FIFO). The wait never hangs a stopped or stalled world.
/// </summary>
public sealed class SessionCloseWaitsForWorldTests
{
    [Fact]
    public async Task ACommandQueuedBeforeDisconnect_UsesTheSessionScope_BeforeItEnds()
    {
        await using var host = WorldTestHost.Start();
        using var gate = new ManualResetEventSlim(false);
        using var blocked = new ManualResetEventSlim(false);
        Exception? commandFault = null;
        var commandDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using AsyncServiceScope scope = host.WorldServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        (Task run, WorldSession session, TcpClient client) = await StartAsync(host, scope.ServiceProvider, new WorldSessionOptions(), null);
        using (client)
        {
            // The world thread is busy; the command behind it is what EnterWorld is: it needs the scope later.
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
                    commandDone.TrySetResult();
                }
            });

            client.Dispose(); // the disconnect: the read loop ends and the session closes
            await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session to close");

            // Fixed: RunAsync stays parked behind the world thread, so the scope is still alive when the command runs.
            // Unfixed: RunAsync returns at once and the scope is disposed under the queued command.
            Task first = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));
            if (ReferenceEquals(first, run))
            {
                await scope.DisposeAsync();
            }

            gate.Set();
            await commandDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(commandFault);
        }
    }

    [Fact]
    public async Task ADisconnect_WhenTheWorldHasStopped_DoesNotWaitForIt()
    {
        await using var host = WorldTestHost.Start();
        host.World.Stop();
        await using AsyncServiceScope scope = host.WorldServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        (Task run, WorldSession session, TcpClient client) = await StartAsync(host, scope.ServiceProvider, new WorldSessionOptions(), null);
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
        (Task run, WorldSession session, TcpClient client) = await StartAsync(host, scope.ServiceProvider, options, log.CreateLogger("session"));
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
                Assert.False(gate.IsSet);
                Assert.Contains(log.Lines, line => line.Contains("did not run the session close", StringComparison.Ordinal));
            }
        }
        finally
        {
            gate.Set();
        }
    }

    private static async Task<(Task Run, WorldSession Session, TcpClient Client)> StartAsync(
        WorldTestHost host, IServiceProvider services, WorldSessionOptions options, Microsoft.Extensions.Logging.ILogger? logger)
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
                stream, "127.0.0.1:50001", services, host.Opcodes, host.World, host.Registry, options,
                logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
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
}
