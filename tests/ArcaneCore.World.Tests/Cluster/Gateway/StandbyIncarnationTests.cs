using System.Net;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Gateway;

/// <summary>
/// The link incarnation a worker advertises is <see cref="WorldHost.Incarnation"/>, standby included (a standby owns no characters ownership
/// token, so before this it advertised a random id): the gateway then addresses the standby by the same id the ownership takeover hands the
/// shard to. Real link host on 127.0.0.1:0 with a real <see cref="GatewaySessionLinkClient"/>; the world host and runtime are not started.
/// </summary>
public sealed class StandbyIncarnationTests
{
    private const string Secret = "standby-incarnation-secret-0123456789";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    [Fact]
    public void ResolveLinkIncarnation_PrefersTheWorldHost_ThenTheOwnershipToken_ThenAFreshId()
    {
        using Rig rig = Rig.Create(standby: true);
        Guid token = Guid.NewGuid();

        Assert.Equal(rig.Host.Incarnation, GatewayWorkerEndpoint.ResolveLinkIncarnation(rig.Host, token));
        Assert.Equal(rig.Host.Incarnation, GatewayWorkerEndpoint.ResolveLinkIncarnation(rig.Host, null));
        Assert.Equal(token, GatewayWorkerEndpoint.ResolveLinkIncarnation(null, token));
        Guid fresh = GatewayWorkerEndpoint.ResolveLinkIncarnation(null, null);
        Assert.NotEqual(Guid.Empty, fresh);
        Assert.NotEqual(token, fresh);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLinkAdvertisesTheWorldHostsIncarnation_AndAGatewayIsAdmittedUnderIt(bool standby)
    {
        using var cts = new CancellationTokenSource(Budget);
        using Rig rig = Rig.Create(standby);
        Assert.Equal(standby, rig.Host.IsStandby);
        Assert.Null(rig.World.CharacterOwnership); // a standby owns nothing: the incarnation cannot come from an ownership token

        await rig.Endpoint.StartAsync(cts.Token);
        try
        {
            Assert.Equal(rig.Host.Incarnation, rig.Endpoint.LinkIncarnation);
            Assert.NotEqual(Guid.Empty, rig.Endpoint.LinkIncarnation);

            var options = new GatewayWorkerOptions();
            ClusterNodeManifest gateway = GatewayLinkManifest.Create(
                ClusterRole.Gateway, "gw-1", Guid.NewGuid(), options.RealmId, options.ContentRevision, options.CharactersSchemaVersion, options.WorldSchemaVersion);
            await using var client = new GatewaySessionLinkClient(
                gateway, new IPEndPoint(IPAddress.Loopback, rig.Endpoint.Host!.BoundEndPoint.Port), Secret, NullLogger.Instance);
            await client.ConnectAsync(cts.Token);
            while (!client.IsConnected)
            {
                await Task.Delay(20, cts.Token);
            }

            Assert.True(client.IsConnected);
            Assert.Equal(rig.Host.Incarnation, rig.Endpoint.LinkIncarnation); // stable across the link's lifetime
        }
        finally
        {
            await rig.Endpoint.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AHandBuiltEndpointWithoutAWorldHost_StillAdvertisesAnIncarnation()
    {
        using var cts = new CancellationTokenSource(Budget);
        using Rig rig = Rig.Create(standby: true, withRootServices: false);

        await rig.Endpoint.StartAsync(cts.Token);
        try
        {
            Assert.NotEqual(Guid.Empty, rig.Endpoint.LinkIncarnation);
            Assert.NotEqual(rig.Host.Incarnation, rig.Endpoint.LinkIncarnation);
        }
        finally
        {
            await rig.Endpoint.StopAsync(CancellationToken.None);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CharacterSaveQueue _saveQueue;
        private readonly SessionRegistry _registry;

        private Rig(ServiceProvider provider, CharacterSaveQueue saveQueue, SessionRegistry registry, WorldRuntime world, WorldHost host, GatewayWorkerEndpoint endpoint)
        {
            _provider = provider;
            _saveQueue = saveQueue;
            _registry = registry;
            World = world;
            Host = host;
            Endpoint = endpoint;
        }

        public WorldRuntime World { get; }

        public WorldHost Host { get; }

        public GatewayWorkerEndpoint Endpoint { get; }

        public static Rig Create(bool standby, bool withRootServices = true)
        {
            var workerOptions = Options.Create(new GatewayWorkerOptions
            {
                Enabled = true,
                Standby = standby,
                Port = 0,
                LinkSecret = Secret,
                VersionsFromDatabase = false,
            });
            var services = new ServiceCollection();
            services.AddLogging();
            ServiceProvider provider = services.BuildServiceProvider();
            IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            var host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, workerOptions: workerOptions);

            // The endpoint finds the world host the way the real container hands it out: as one of the hosted services.
            IServiceProvider? root = withRootServices ? new HostedServices(host) : null;
            var registry = new SessionRegistry();
            var endpoint = new GatewayWorkerEndpoint(
                scopes, workerOptions, Options.Create(new WorldSessionOptions()), new OpcodeTable(), world, registry, NullLoggerFactory.Instance,
                rootServices: root);
            return new Rig(provider, saveQueue, registry, world, host, endpoint);
        }

        public void Dispose()
        {
            _registry.Dispose();
            World.Dispose();
            _saveQueue.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>A provider whose only answer is the hosted-service enumeration (what <c>GetServices&lt;IHostedService&gt;()</c> asks for).</summary>
    private sealed class HostedServices(WorldHost host) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IEnumerable<IHostedService>) ? new IHostedService[] { host } : null;
    }
}
