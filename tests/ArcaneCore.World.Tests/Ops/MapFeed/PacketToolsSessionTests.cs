using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The packet tools' session side (docs/areas/map-feed.md, "Packet tools"): a session gets the dispatcher gate from the configuration
/// (not only from the feed's own start), an injection ends the session on a broken invariant or a foreign cancellation the way the reader
/// would, a packet over the packet budget is answered <c>dropped</c>, and a console retired after the tools were disposed is disposed.
/// No pipe: the sessions are injected into directly.
/// </summary>
public sealed class PacketToolsSessionTests
{
    private static PacketToolsPermit Permit(bool toolsEnabled, bool feedEnabled, bool allowOutside, string? environment)
        => new(
            Options.Create(new WorldPacketToolsOptions { Enabled = toolsEnabled, AllowOutsideDevelopment = allowOutside }),
            Options.Create(new WorldMapFeedOptions { Enabled = feedEnabled }),
            environment is null ? null : new TestHostEnvironment(environment));

    /// <summary>A world with one player in it; the feed is never started, so only the permit built from configuration can give the session its gate.</summary>
    private static async Task<(FeedWorld World, WorldTestClient Player, WorldSession Session)> PlayerAsync(PacketToolsPermit permit)
    {
        FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { StartFeed = false, Permit = permit });
        WorldTestClient player = await world.AddPlayerAsync("Feedalice");
        return (world, player, world.Host.Registry.Sessions.Single(s => s.Player?.Name == "Feedalice"));
    }

    /// <summary>Replace the session handler of CMSG_QUERY_TIME on the host's table (the sessions share it).</summary>
    private static void ReplaceQueryTimeHandler(FeedWorld world, SessionHandler handler)
    {
        var replacement = new OpcodeTable();
        replacement.OnSession(WorldOpcode.CmsgQueryTime, SessionStates.Authenticated, handler);
        OpcodeTable swapped = world.Host.Opcodes.TrySwap([WorldOpcode.CmsgQueryTime], replacement, out string? error)!;
        Assert.Null(error);
        world.Host.Opcodes.Replace(swapped);
    }

    private static Task<InjectVerdict> InjectQueryTimeAsync(WorldSession session, CancellationToken token = default)
        => session.InjectAsync(WorldOpcode.CmsgQueryTime, [], TimeSpan.FromSeconds(5), token);

    [Theory]
    [InlineData(true, true, false, "Development", true)]
    [InlineData(true, true, false, "development", true)]
    [InlineData(true, true, false, "Production", false)]
    [InlineData(true, true, true, "Production", true)]
    [InlineData(false, true, true, "Development", false)]
    [InlineData(true, false, false, "Development", false)]
    [InlineData(true, true, false, null, false)]
    public void ThePermit_IsConfiguredFromTheOptionsAndTheEnvironment_BeforeTheFeedStarts(bool toolsEnabled, bool feedEnabled, bool allowOutside, string? environment, bool expected)
    {
        PacketToolsPermit permit = Permit(toolsEnabled, feedEnabled, allowOutside, environment);

        Assert.Equal(expected, permit.Configured);
        Assert.Equal(expected, permit.GatesSessions);
        Assert.False(permit.Granted);
    }

    [Fact]
    public void ThePermitWithoutConfiguration_GatesSessionsOnlyWhileTheFeedGrantsIt()
    {
        var permit = new PacketToolsPermit();
        Assert.False(permit.Configured);
        Assert.False(permit.GatesSessions);

        permit.Granted = true;
        Assert.True(permit.GatesSessions);

        permit.Granted = false;
        Assert.False(permit.GatesSessions);
    }

    [Fact]
    public void ThePermit_ResolvedFromTheContainer_ReadsTheOptionsAndTheHostEnvironment()
    {
        var on = new ServiceCollection();
        on.Configure<WorldPacketToolsOptions>(o => o.Enabled = true);
        on.Configure<WorldMapFeedOptions>(o => o.Enabled = true);
        on.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        on.AddSingleton<PacketToolsPermit>();
        using ServiceProvider onProvider = on.BuildServiceProvider();
        Assert.True(onProvider.GetRequiredService<PacketToolsPermit>().Configured);

        // A host without an environment (or without the tools' options) must not gate sessions.
        var noEnvironment = new ServiceCollection();
        noEnvironment.Configure<WorldPacketToolsOptions>(o => o.Enabled = true);
        noEnvironment.Configure<WorldMapFeedOptions>(o => o.Enabled = true);
        noEnvironment.AddSingleton<PacketToolsPermit>();
        using ServiceProvider noEnvironmentProvider = noEnvironment.BuildServiceProvider();
        Assert.False(noEnvironmentProvider.GetRequiredService<PacketToolsPermit>().Configured);

        var off = new ServiceCollection();
        off.Configure<WorldPacketToolsOptions>(_ => { });
        off.Configure<WorldMapFeedOptions>(o => o.Enabled = true);
        off.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development"));
        off.AddSingleton<PacketToolsPermit>();
        using ServiceProvider offProvider = off.BuildServiceProvider();
        Assert.False(offProvider.GetRequiredService<PacketToolsPermit>().Configured);
    }

    [Fact]
    public async Task ASessionBuiltWhileTheToolsAreConfiguredOn_GetsTheGate_BeforeTheFeedEverGrantsTheTools()
    {
        // The daemon restores playerbots before the feed's own start: the session is built while Granted is still false.
        PacketToolsPermit permit = Permit(toolsEnabled: true, feedEnabled: true, allowOutside: false, "Development");
        (FeedWorld world, WorldTestClient player, WorldSession session) = await PlayerAsync(permit);
        await using (world)
        await using (player)
        {
            Assert.False(permit.Granted);

            Assert.Equal(InjectVerdict.Handled, await InjectQueryTimeAsync(session));
            byte[] reply = await player.ReadUntilAsync(WorldOpcode.SmsgQueryTimeResponse);
            Assert.Equal(4, reply.Length);
        }
    }

    [Fact]
    public async Task ASessionBuiltWhileTheToolsAreConfiguredOff_HasNoGate_AndIsNotInjectable()
    {
        foreach ((bool toolsEnabled, bool feedEnabled, string environment) in new[] { (false, true, "Development"), (true, false, "Development"), (true, true, "Production") })
        {
            PacketToolsPermit permit = Permit(toolsEnabled, feedEnabled, allowOutside: false, environment);
            (FeedWorld world, WorldTestClient player, WorldSession session) = await PlayerAsync(permit);
            await using (world)
            await using (player)
            {
                Assert.Equal(InjectVerdict.NotInjectable, await InjectQueryTimeAsync(session));
            }
        }
    }

    [Fact]
    public async Task AnInjectedHandlerThatBreaksAnInvariant_PropagatesIt_AndEndsTheSession()
    {
        (FeedWorld world, WorldTestClient player, WorldSession session) = await PlayerAsync(Permit(true, true, false, "Development"));
        await using (world)
        await using (player)
        {
            ReplaceQueryTimeHandler(world, (_, _) => throw new InvariantViolationException("injected invariant", "test", "PacketToolsSessionTests.cs", 1));

            await Assert.ThrowsAsync<InvariantViolationException>(() => InjectQueryTimeAsync(session));

            // Kick sets the session's cancellation before the exception leaves InjectAsync, so the next injection already sees it.
            Assert.Equal(InjectVerdict.Closed, await InjectQueryTimeAsync(session));
            await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session is closed after the broken invariant");
        }
    }

    [Fact]
    public async Task AnInjectedHandlerThatIsCancelledForAnotherReason_IsAFault_NotACancellation_AndEndsTheSession()
    {
        (FeedWorld world, WorldTestClient player, WorldSession session) = await PlayerAsync(Permit(true, true, false, "Development"));
        await using (world)
        await using (player)
        {
            // A cancellation of a token that is not the caller's (a timeout inside the handler, say).
            using var foreign = new CancellationTokenSource();
            foreign.Cancel();
            ReplaceQueryTimeHandler(world, (_, _) => throw new OperationCanceledException(foreign.Token));

            Assert.Equal(InjectVerdict.Disconnected, await InjectQueryTimeAsync(session));

            Assert.Equal(InjectVerdict.Closed, await InjectQueryTimeAsync(session));
            await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session is closed after the foreign cancellation");
        }
    }

    [Fact]
    public async Task AnInjectionTheCallerCancels_RethrowsTheCancellation_AndLeavesTheSessionUp()
    {
        (FeedWorld world, WorldTestClient player, WorldSession session) = await PlayerAsync(Permit(true, true, false, "Development"));
        await using (world)
        await using (player)
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReplaceQueryTimeHandler(world, async (_, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, caller.Token);
            });

            Task<InjectVerdict> injection = InjectQueryTimeAsync(session, caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => injection);

            // Not a fault of the session: it still takes the next injection.
            ReplaceQueryTimeHandler(world, (_, _) => Task.CompletedTask);
            Assert.Equal(InjectVerdict.Handled, await InjectQueryTimeAsync(session));
            Assert.NotEqual(SessionState.Closed, session.State);
        }
    }

    [Fact]
    public async Task APacketOverThePacketBudget_IsAnsweredDropped_AsTheClientsPacketIsDropped_AndTheSessionStaysUp()
    {
        const string Account = "TOOLBUDGET";
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton(new PacketToolsPermit { Granted = true }));
        var guard = new NetGuard(
            new NetProtectionOptions { WorldOpcodeBurst = 2, WorldOpcodeRefillPerSecond = 0, WorldPacketsPerSecond = 0, WorldFloodPacketsPerSecond = 0 },
            () => 0, () => 0, NullLogger.Instance);
        byte[] key = await host.AddAccountAsync(Account);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var built = new TaskCompletionSource<WorldSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task sessionTask = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync(stop.Token);
            await using NetworkStream stream = server.GetStream();
            await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
            var worldSession = new WorldSession(stream, "127.0.0.1:41001", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
                new WorldSessionOptions(), NullLogger.Instance, guard);
            built.SetResult(worldSession);
            await worldSession.RunAsync(stop.Token);
        });

        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        await using var client = new WorldTestClient(socket);
        try
        {
            await client.AuthenticateAsync(Account, key);
            WorldSession session = await built.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WorldTestHost.WaitForAsync(() => session.State == SessionState.CharacterSelect, "the session is at the character screen");

            // CMSG_CHAR_ENUM is a session opcode of the character screen; the bucket of an opcode holds 2 packets and never refills.
            var verdicts = new List<InjectVerdict>();
            for (int i = 0; i < 6; i++)
            {
                verdicts.Add(await session.InjectAsync(WorldOpcode.CmsgCharEnum, [], TimeSpan.FromSeconds(5), CancellationToken.None));
            }

            int firstDropped = verdicts.IndexOf(InjectVerdict.Dropped);
            Assert.InRange(firstDropped, 0, 2); // the bucket holds 2 (the client may have used one already)
            Assert.All(verdicts.Take(firstDropped), v => Assert.Equal(InjectVerdict.Handled, v));
            Assert.All(verdicts.Skip(firstDropped), v => Assert.Equal(InjectVerdict.Dropped, v));
            Assert.Equal(verdicts.Count - firstDropped, guard.DroppedPackets);
            Assert.Equal(0, guard.FloodDisconnects);
            Assert.Equal(SessionState.CharacterSelect, session.State);
            Assert.False(sessionTask.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await sessionTask.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task ARetiredConsole_IsDisposedAtOnce_WhenTheToolsWereDisposedFirst_AndNotBeforeTheyAre_WhenRetiredEarlier()
    {
        FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { StartFeed = false, Permit = Permit(true, true, false, "Development") });
        await using (world)
        {
            var provider = new ScopeCountingProvider(world.Host.WorldServices);
            var tools = new PacketTools(provider, world.Host.World, NullLogger.Instance);

            // Retired while the tools live: kept for the grace period, disposed by DisposeAsync.
            ToolConsole early = tools.CreateConsole(_ => { });
            tools.Retire(early);
            await Task.Delay(100);
            Assert.Equal(0, provider.DisposedScopes);

            // The stop cancels the console's own grace wait, which then disposes it too; whichever removes it first disposes it, once.
            await tools.DisposeAsync();
            await WorldTestHost.WaitForAsync(() => provider.DisposedScopes == 1, "the console retired earlier is disposed when the tools are");

            // Retired after DisposeAsync swept the retired consoles: nothing else would ever dispose it.
            ToolConsole late = tools.CreateConsole(_ => { });
            tools.Retire(late);
            await WorldTestHost.WaitForAsync(() => provider.DisposedScopes == 2, "the console retired after the tools were disposed is disposed");
            await Task.Delay(100);
            Assert.Equal(2, provider.DisposedScopes); // each console once
        }
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "PacketToolsSessionTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>The host's services, with the scopes handed to consoles counted when they are disposed.</summary>
    private sealed class ScopeCountingProvider(IServiceProvider inner) : IServiceProvider, IServiceScopeFactory
    {
        private int _disposed;

        public int DisposedScopes => Volatile.Read(ref _disposed);

        public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? this : inner.GetService(serviceType);

        public IServiceScope CreateScope() => new CountedScope(inner.GetRequiredService<IServiceScopeFactory>().CreateScope(), () => Interlocked.Increment(ref _disposed));

        private sealed class CountedScope(IServiceScope scope, Action onDispose) : IServiceScope
        {
            public IServiceProvider ServiceProvider => scope.ServiceProvider;

            public void Dispose()
            {
                scope.Dispose();
                onDispose();
            }
        }
    }
}
