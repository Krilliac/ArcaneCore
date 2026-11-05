using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The production edges of realm presence that <see cref="PresenceWireTests"/> bypasses: the world feed in <see cref="SocialFeature"/> (login, the one-second diff pass, logout),
/// the real kick of a registered <see cref="ArcaneCore.World.Net.WorldSession"/>, and the hosted peer link that wires a holder's listener and a worker's client.
/// </summary>
public sealed class PresenceFeedTests
{
    private static PresenceSnapshot Snapshot(ulong guid, int account, long generation)
        => new(guid, $"Feed{guid}", account, generation, 0, 12, 0, 10, 1, 1, 0, 0, null, 0, false, false);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, $"not met in time: {what}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task SocialFeature_FeedsLoginAZoneMapLevelChangeAndLogout_IntoTheHoldersDirectory()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
        var routed = new LeaseRoutedPresence(holder, static () => true, static () => null);
        await using var publisher = new ClusterPresencePublisher(routed, "w1", "gw", static _ => { });

        var services = new ServiceCollection();
        services.AddSingleton<ISocialStore>(new EmptySocialStore());
        services.AddSingleton(publisher);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var characters = new CharacterDirectory();
        characters.Add(new CharacterIdentity(41, 77, "Feeder", 1, 0, 1));
        await using var feature = new SocialFeature(
            characters, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance, services: provider);
        Player player = CreatePlayer(41, 77, "Feeder", new FeedSession(77, generation: 3));
        feature.Attach(world);
        world.Start();

        await feature.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await world.InvokeAsync(() =>
        {
            world.AddPlayer(player);
            world.NotifyLoggedIn(player);
            return true;
        });

        // Login: the entry exists, keyed by the character's guid, with the session's account, name and zone.
        Assert.True(await publisher.FlushAsync());
        RealmPresenceEntry first = Assert.IsType<RealmPresenceEntry>(directory.FindByGuid(41));
        Assert.Equal("Feeder", first.CharacterName);
        Assert.Equal(77, first.AccountId);
        Assert.Equal("w1", first.WorkerId);
        Assert.Equal(12u, first.ZoneId);
        Assert.Equal(0u, first.MapId);

        // A zone, map and level change reaches the entry through the feature's diff pass.
        await world.InvokeAsync(() =>
        {
            player.ZoneId = 14;
            player.MapId = 1;
            player.Level = 11;
            feature.RunPeriodicStatsPass();
            return true;
        });
        Assert.True(await publisher.FlushAsync());
        RealmPresenceEntry changed = Assert.IsType<RealmPresenceEntry>(directory.FindByGuid(41));
        Assert.Equal(14u, changed.ZoneId);
        Assert.Equal(1u, changed.MapId);
        Assert.Equal(11, changed.Level);
        Assert.True(changed.PresenceVersion > first.PresenceVersion);

        // A pass with nothing changed sends nothing.
        await world.InvokeAsync(() =>
        {
            feature.RunPeriodicStatsPass();
            return true;
        });
        Assert.Equal(0, publisher.PendingCount);

        // Logout removes the entry.
        await world.InvokeAsync(() =>
        {
            world.RemovePlayer(player);
            return true;
        });
        Assert.True(await publisher.FlushAsync());
        Assert.Null(directory.FindByGuid(41));
        Assert.Equal(0, publisher.RegisteredCount);
    }

    [Fact]
    public async Task APresenceKick_DisconnectsTheRegisteredSessionOfTheAccount()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("KICKACC", "Kickee");
        Player player = await host.PlayerAsync("Kickee");
        int account = player.AccountId;
        long generation = player.Session.SessionGeneration;
        Assert.True(generation > 0);
        ArcaneCore.World.Net.WorldSession session = Assert.IsType<ArcaneCore.World.Net.WorldSession>(host.Registry.Find(account));
        Assert.Null(session.KickReason);

        var directory = new InMemoryRealmPresenceDirectory();
        var holder = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
        var routed = new LeaseRoutedPresence(holder, static () => true, static () => null);
        await using var oldWorker = new ClusterPresencePublisher(routed, "w-old", "gw", id => host.Registry.KickSuperseded(id));
        await using var newWorker = new ClusterPresencePublisher(routed, "w-new", "gw", static _ => { });

        oldWorker.PublishLogin(Snapshot(guid: (ulong)player.Guid.Low, account, generation));
        Assert.True(await oldWorker.FlushAsync());
        Assert.Null(session.KickReason); // registering alone kicks nothing

        newWorker.PublishLogin(Snapshot(guid: 9001, account, generation: 1)); // the same account logs in on another worker
        Assert.True(await newWorker.FlushAsync());
        Assert.Null(session.KickReason); // the kick is queued for the old worker, not applied yet

        Assert.True(await oldWorker.FlushAsync()); // the old worker's next flush delivers it
        Assert.NotNull(session.KickReason);
        Assert.Contains("another world worker", session.KickReason);
    }

    [Fact]
    public async Task PeerLinkHostedService_WiresTheHoldersListenerAndAWorkersClient_SoPresenceCrossesWorkers()
    {
        const string Secret = "hosted-peer-secret-0123456789";
        string? previous = Environment.GetEnvironmentVariable(PeerLinkOptions.SecretEnvironmentVariable);
        Environment.SetEnvironmentVariable(PeerLinkOptions.SecretEnvironmentVariable, Secret);
        try
        {
            int port;
            using (var probe = ArcaneCore.TestSupport.LoopbackPorts.CreateListener())
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var store = new InMemoryRealmAuthorityLeaseStore();
            var options = new PeerLinkOptions
            {
                BindAddress = "127.0.0.1",
                Port = port,
                ReconnectInitialMs = 20,
                ReconnectMaxMs = 100,
                HolderPollMs = 30,
                RequestTimeoutMs = 3000,
            };
            options.Nodes["node-h"] = $"127.0.0.1:{port}";

            await using RealmAuthorityLeaseKeeper holderKeeper = NewKeeper(store, "node-h");
            await using RealmAuthorityLeaseKeeper workerKeeper = NewKeeper(store, "node-w");
            await holderKeeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.Null(workerKeeper.TokenFor(RealmAuthorityScopes.Social));

            await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
            using var world = new WorldRuntime(
                new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
            var directory = new InMemoryRealmPresenceDirectory();
            var holderService = new PresenceHolderService(directory, new InMemoryClusterSocialRouter(directory));
            var workerDirectory = new InMemoryRealmPresenceDirectory();
            var workerLocal = new PresenceHolderService(workerDirectory, new InMemoryClusterSocialRouter(workerDirectory));
            var holderRuntime = new PeerLinkRuntime();
            var workerRuntime = new PeerLinkRuntime();
            var holderHosted = new PeerLinkHostedService(
                Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(WorkerOptions("node-h")), holderKeeper, store, holderService, holderRuntime,
                provider.GetRequiredService<IServiceScopeFactory>(), world, NullLoggerFactory.Instance);
            var workerHosted = new PeerLinkHostedService(
                Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(WorkerOptions("node-w")), workerKeeper, store, workerLocal, workerRuntime,
                provider.GetRequiredService<IServiceScopeFactory>(), world, NullLoggerFactory.Instance);
            await holderHosted.StartAsync(CancellationToken.None);
            await workerHosted.StartAsync(CancellationToken.None);
            try
            {
                PeerPresenceClient remote = Assert.IsType<PeerPresenceClient>(workerRuntime.Presence);

                // The worker does not hold the Social lease: its LeaseRoutedPresence goes over the link to the holder's PresenceHolderService.
                var routed = new LeaseRoutedPresence(workerLocal, static () => false, () => workerRuntime.Presence);
                await using var publisher = new ClusterPresencePublisher(routed, "node-w", "gw", static _ => { });
                publisher.PublishLogin(Snapshot(guid: 600, account: 12, generation: 1));
                bool flushed = false;
                await WaitUntilAsync(() =>
                {
                    flushed = publisher.FlushAsync().GetAwaiter().GetResult();
                    return flushed;
                }, "the worker's link to the holder admitted and the login written");

                RealmPresenceEntry entry = Assert.IsType<RealmPresenceEntry>(directory.FindByGuid(600));
                Assert.Equal("node-w", entry.WorkerId);
                Assert.Equal(12, entry.AccountId);
                Assert.NotNull(remote);
            }
            finally
            {
                await workerHosted.StopAsync(CancellationToken.None);
                await holderHosted.StopAsync(CancellationToken.None);
            }

            Assert.Null(workerRuntime.Presence);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PeerLinkOptions.SecretEnvironmentVariable, previous);
        }
    }

    private static GatewayWorkerOptions WorkerOptions(string node) => new() { Enabled = true, NodeId = node, VersionsFromDatabase = false };

    private static RealmAuthorityLeaseKeeper NewKeeper(IRealmAuthorityLeaseStore store, string node) => new(
        store,
        Microsoft.Extensions.Options.Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker }),
        Microsoft.Extensions.Options.Options.Create(WorkerOptions(node)),
        NullLogger<RealmAuthorityLeaseKeeper>.Instance,
        null,
        null,
        _ => { },
        null);

    private static Player CreatePlayer(int id, int accountId, string name, IPlayerSession session)
    {
        var record = new CharacterRecord
        {
            Id = id, AccountId = accountId, Name = name,
            Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 10, ZoneId = 12,
        };
        return new Player(record, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
    }

    private sealed class FeedSession(int accountId, long generation) : IPlayerSession
    {
        public int AccountId => accountId;
        public long SessionGeneration => generation;
        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    private sealed class EmptySocialStore : ISocialStore
    {
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
