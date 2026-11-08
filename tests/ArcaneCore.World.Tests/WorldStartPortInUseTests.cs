using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A world port that another process holds. The refusal used to escape <see cref="WorldStartup.RunAsync"/> as an <see cref="InvalidOperationException"/> (the early probe) or a
/// <see cref="System.Net.Sockets.SocketException"/> (the real bind), which the crash handler turns into a fail-fast with a dump. Now both end the run with a failure exit code.
/// These run the real generic host through <see cref="WorldStartup.RunAsync"/>; the exit code is process-global, so they share the non-parallel collection.
/// </summary>
[Collection(ExitCodesCollection.Name)]
public sealed class WorldStartPortInUseTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TheRealBindOnATakenPort_ReturnsAFailureExitCode_StopsTheStartedServices_AndDoesNotEscapeRunAsync()
    {
        int before = ExitCodes.Current;
        var taken = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        taken.Start();
        var started = new Recorder();
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            int port = ((IPEndPoint)taken.LocalEndpoint).Port;
            IHost host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
                services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
                services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
                services.AddSingleton<IHostedService>(started);
                services.AddSingleton<IHostedService>(sp => new WorldServer(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new WorldOptions { BindAddress = IPAddress.Loopback.ToString(), Port = port }),
                    Options.Create(new WorldSessionOptions()), new OpcodeTable(), sp.GetRequiredService<WorldRuntime>(), new SessionRegistry(),
                    NullLoggerFactory.Instance, NullLogger<WorldServer>.Instance));
            }).Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
            Assert.True(started.Stopped, "a hosted service started before the listener must still be stopped in order");
        }
        finally
        {
            taken.Stop();
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task TheEarlyProbeRefusal_ReturnsAFailureExitCode_AlsoWhenTheHostWrapsItInAnAggregate()
    {
        int before = ExitCodes.Current;
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            IHost host = new HostBuilder().ConfigureServices(services =>
                services.AddSingleton<IHostedService>(new Refuses(new AggregateException(new WorldPortInUseException("the world port 127.0.0.1:1 is already in use")))))
                .Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task TheRealEarlyProbe_OnATakenPort_ReturnsAFailureExitCode_ThroughWorldStartupRunAsync()
    {
        int before = ExitCodes.Current;
        var taken = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        taken.Start();
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            int port = ((IPEndPoint)taken.LocalEndpoint).Port;
            IHost host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
                services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
                services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
                // The real WorldHost with its real probe (no stub, no WorldServer: ListenerExpected stands in for the registration the probe would look for).
                services.AddSingleton<IHostedService>(sp => new WorldHost(
                    sp.GetRequiredService<WorldRuntime>(), sp.GetRequiredService<CharacterSaveQueue>(), new CharacterDirectory(), sp.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<WorldHost>.Instance, worldOptions: Options.Create(new WorldOptions { BindAddress = IPAddress.Loopback.ToString(), Port = port })) { ListenerExpected = true });
            }).Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
        }
        finally
        {
            taken.Stop();
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task AGatewayLinkPortHeldByAnotherListener_RefusesTheStartAsPortInUse_NotARawSocketException()
    {
        var taken = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        taken.Start();
        try
        {
            int port = ((IPEndPoint)taken.LocalEndpoint).Port;
            await using var link = new GatewaySessionLinkHost(
                GatewayLinkManifest.Create(ClusterRole.Worker, "worker-port-clash", Guid.NewGuid(), 1, "dev", 1, 1),
                new NeverOpensAcceptor(), () => 1, "port-clash-secret-0123456789", new IPEndPoint(IPAddress.Loopback, port));

            var failure = Assert.Throws<ArcaneCore.World.Net.WorldPortInUseException>(link.Start);

            Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), failure.Message);
            Assert.IsType<SocketException>(failure.InnerException);
        }
        finally
        {
            taken.Stop();
        }
    }

    [Theory]
    [InlineData(SocketError.AddressAlreadyInUse, true)]
    [InlineData(SocketError.AccessDenied, false)]
    [InlineData(SocketError.AddressNotAvailable, false)]
    [InlineData(SocketError.TooManyOpenSockets, false)]
    [InlineData(SocketError.ConnectionReset, false)]
    public void OnlyAddressInUse_IsAClash_AndEveryOtherBindErrorIsDescribedAsItself(SocketError error, bool clash)
    {
        var ex = new SocketException((int)error);
        Assert.Equal(clash, ArcaneCore.World.Net.WorldPortInUseException.IsClash(ex));

        WorldBindFailedException failure = WorldBindFailedException.For("world port", "127.0.0.1:1", ex);

        Assert.Equal(clash, failure is WorldPortInUseException);
        Assert.Equal(clash, failure.Message.Contains("already in use", StringComparison.Ordinal));
        Assert.Contains("127.0.0.1:1", failure.Message);
        Assert.Same(ex, failure.InnerException);
    }

    [Fact]
    public async Task ANonClashBindError_AtTheRealWorldBind_EndsAsACleanFailedStart_WithItsTrueDescription_ThroughRunAsync()
    {
        int before = ExitCodes.Current;
        var started = new Recorder();
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            // 203.0.113.0/24 (TEST-NET-3) is never an address of this host: the bind fails with AddressNotAvailable (not AddressAlreadyInUse).
            IHost host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
                services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
                services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
                services.AddSingleton<IHostedService>(started);
                services.AddSingleton<IHostedService>(sp => new WorldServer(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new WorldOptions { BindAddress = "203.0.113.77", Port = 45871 }),
                    Options.Create(new WorldSessionOptions()), new OpcodeTable(), sp.GetRequiredService<WorldRuntime>(), new SessionRegistry(),
                    NullLoggerFactory.Instance, NullLogger<WorldServer>.Instance));
            }).Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
            Assert.True(started.Stopped, "a hosted service started before the listener must still be stopped in order");
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task ANonClashBindError_AtTheEarlyProbe_StopsBeforeTheOwnershipClaimAndTheBotRestore_AndReturnsAFailureExitCode()
    {
        int before = ExitCodes.Current;
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            var ownership = new CountingOwnershipStore();
            IHost host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
                services.AddSingleton<ICharacterOwnershipStore>(ownership);
                services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
                services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
                services.AddSingleton<IHostedService>(sp => new WorldHost(
                    sp.GetRequiredService<WorldRuntime>(), sp.GetRequiredService<CharacterSaveQueue>(), new CharacterDirectory(), sp.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<WorldHost>.Instance, worldOptions: Options.Create(new WorldOptions { BindAddress = "203.0.113.77", Port = 45872 })) { ListenerExpected = true });
            }).Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
            Assert.Equal(0, ownership.Calls); // the ownership claim, and with it the bot restore after it, never ran
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task ANonClashBindError_AtTheEarlyProbe_ThrowsABindFailure_ThatNamesTheRealError_NotAnInUseClaim()
    {
        IHost host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
            services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
            services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
            services.AddSingleton<IHostedService>(sp => new WorldHost(
                sp.GetRequiredService<WorldRuntime>(), sp.GetRequiredService<CharacterSaveQueue>(), new CharacterDirectory(), sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorldHost>.Instance, worldOptions: Options.Create(new WorldOptions { BindAddress = "203.0.113.77", Port = 45873 })) { ListenerExpected = true });
        }).Build();

        var failure = await Assert.ThrowsAsync<WorldBindFailedException>(() => host.StartAsync()).WaitAsync(Deadline);

        Assert.Contains("203.0.113.77:45873", failure.Message);
        Assert.DoesNotContain("already in use", failure.Message, StringComparison.Ordinal);
        Assert.IsType<SocketException>(failure.InnerException);
    }

    private sealed class NeverOpensAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
            => throw new NotSupportedException("no gateway connects in this test");
    }

    /// <summary>An ownership store that counts how often the start reached the ownership claim (the step before the bot restore).</summary>
    private sealed class CountingOwnershipStore : ICharacterOwnershipStore
    {
        public int Calls { get; private set; }

        public Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(false);
        }

        public Task<CharacterOwnershipToken?> GetCurrentAsync(string scope, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<CharacterOwnershipToken?>(null);
        }

        public Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<CharacterOwnershipToken?>(null);
        }

        public Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<CharacterOwnershipToken?>(null);
        }

        public Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class Recorder : IHostedService
    {
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Refuses(Exception failure) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw failure;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
