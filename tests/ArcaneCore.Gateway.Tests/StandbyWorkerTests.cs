using System.Net;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Tier 3b groundwork, standby mode (Cluster:Worker:Standby, docs/design/hot-reload-tiers.md section 5.3): a second world on the same
/// databases boots, validates its schema and links to the gateway, but claims no ownership, takes no writer lock, opens no client listener,
/// attaches no feature and admits no session until it is promoted under the ownership a takeover handed to its incarnation.
/// </summary>
public sealed class StandbyWorkerTests
{
    internal const string Secret = "standby-link-secret-0123456789";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandbyRegistration_ClosesAdmission_RemovesTheListener_AndRunsTheSchemaValidateOnly(bool standby)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<WorldWriterGuard>(); // as AddWorld registers it: with database options
        services.AddHostedService<WorldServer>();
        services.AddGatewayWorker(new ConfigurationBuilder()
            .AddInMemoryCollection(Worker(standby)).Build());

        Assert.Equal(standby, !services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorldServer)));
        ServiceDescriptor guard = Assert.Single(services, d => d.ServiceType == typeof(WorldWriterGuard));
        Assert.Equal(standby, guard.ImplementationFactory is not null); // the standby's guard keeps the database options but defers the lock to promotion: Acquire takes nothing
        using ServiceProvider provider = services.BuildServiceProvider();
        WorkerAdmission admission = provider.GetRequiredService<WorkerAdmission>();
        Assert.Equal(!standby, admission.IsOpen);
        Assert.Equal(standby, provider.GetRequiredService<IOptions<ArcaneCore.Data.DatabaseOptions>>().Value.Upgrade.ValidateOnly);
        if (standby)
        {
            Assert.False(provider.GetRequiredService<WorldWriterGuard>().Acquire());
        }
    }

    private sealed class StopCounter : IHostApplicationLifetime
    {
        public int Stops;

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(ref Stops);
    }

    [Fact]
    public void AStandbysGuard_StopsTheHost_AndSkipsTheShutdownSaves_WhenItsLockIsTakenOver()
    {
        // The standby's guard is built by AddGatewayWorker, not by AddWorld: it must still be wired to the host lifetime and the shutdown lanes, or a promoted standby that finds its lock held by
        // another world would only log.
        var lifetime = new StopCounter();
        var drain = new ArcaneCore.World.Persistence.ShutdownDrain();
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton(drain);
        services.AddSingleton<WorldWriterGuard>();
        services.AddHostedService<WorldServer>();
        services.AddGatewayWorker(new ConfigurationBuilder().AddInMemoryCollection(Worker(standby: true)).Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        int exitCode = ArcaneCore.Kernel.Ops.ExitCodes.Current;
        try
        {
            provider.GetRequiredService<WorldWriterGuard>().OnLockConflict("PostgreSQL advisory lock 1: held by another session");

            Assert.Equal(1, lifetime.Stops);
            Assert.NotNull(drain.Saves.SuppressedReason);
            Assert.NotNull(drain.Features.SuppressedReason);
            Assert.NotNull(drain.Saves.Refuse());
        }
        finally { ArcaneCore.Kernel.Ops.ExitCodes.Current = exitCode; }
    }

    [Fact]
    public async Task StandbyWithoutWorkerMode_IsRefusedAtStart()
    {
        var host = new WorldHost(
            null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldHost>.Instance,
            workerOptions: Options.Create(new GatewayWorkerOptions { Enabled = false, Standby = true }));
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));
        Assert.Contains("Cluster:Worker:Standby requires Cluster:Worker:Enabled", refused.Message);
    }

    [Fact]
    public async Task AClosedWorker_RefusesTheOpenInPlainText_AndCreatesNoSession()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(cts.Token);
        var admission = new WorkerAdmission();
        admission.Close("draining");
        await using Linked linked = await LinkAsync(server, cts.Token, admission);
        await server.AddAccountAsync("CLOSED", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, "CLOSED", "PASSWORD", cts.Token);

        await using (WorldClient refused = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token))
        {
            Assert.Equal((byte)AuthResponseCode.Unavailable, await refused.AuthenticateAsync("CLOSED", logon.SessionKey, cts.Token));
        }

        Assert.Empty(linked.Endpoint.Acceptor!.Sessions);
        admission.Open();
        await using WorldClient admitted = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await admitted.AuthenticateAsync("CLOSED", logon.SessionKey, cts.Token));
    }

    [Fact]
    public async Task AStandbyWorld_LoadsAndLinks_ButOwnsNothing_AndAdmitsNoSession()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer active = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer standby = await StartStandbyAsync(active, cts.Token);
        WorldHost activeHost = Host(active);
        WorldHost standbyHost = Host(standby);

        Assert.False(activeHost.IsStandby);
        Assert.True(standbyHost.IsStandby);
        Assert.NotNull(active.World.CharacterOwnership);
        Assert.Null(standby.World.CharacterOwnership);
        Assert.Null(standby.Services.GetRequiredService<WorldWriterGuard>().LockPath);
        Assert.DoesNotContain(standby.Services.GetServices<IHostedService>(), s => s is WorldServer);
        Assert.Equal(WorkerAdmission.StandbyReason, standby.Services.GetRequiredService<WorkerAdmission>().ClosedReason);

        // The ownership row still names the active world.
        CharacterOwnershipRow row = await OwnershipAsync(active);
        Assert.Equal(active.World.CharacterOwnership!.Epoch, row.Epoch);
        Assert.Equal(activeHost.Incarnation.ToString("D"), row.OwnerIncarnation);

        // Linked to a gateway, the standby refuses every session in plain text and opens nothing.
        await using Linked linked = await LinkAsync(standby, cts.Token);
        await active.AddAccountAsync("STANDBY", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(active.RealmEndpoint, "STANDBY", "PASSWORD", cts.Token);
        await using WorldClient client = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Unavailable, await client.AuthenticateAsync("STANDBY", logon.SessionKey, cts.Token));
        Assert.Empty(linked.Endpoint.Acceptor!.Sessions);

        // A token that was not handed to this standby's incarnation does not promote it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => standbyHost.PromoteAsync(active.World.CharacterOwnership!, cts.Token));
        Assert.True(standbyHost.IsStandby);
    }

    [Fact]
    public async Task ARefusedPromotion_ChangesNothing_AndTheRightTokenStillPromotes()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer active = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer standby = await StartStandbyAsync(active, cts.Token);
        WorldHost standbyHost = Host(standby);
        CharacterOwnershipToken old = active.World.CharacterOwnership!;

        // Before any takeover the database still names the active world: even a token shaped for this standby is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            standbyHost.PromoteAsync(old with { Epoch = old.Epoch + 1, Incarnation = standbyHost.Incarnation }, cts.Token));
        Assert.True(standbyHost.IsStandby);
        Assert.Null(standby.World.CharacterOwnership);
        Assert.False(standby.Services.GetRequiredService<WorkerAdmission>().IsOpen);

        CharacterOwnershipToken handed = await TakeOverAsync(standby, old, cts.Token);
        Assert.Equal(old.Epoch + 1, handed.Epoch);

        // A token one epoch ahead of the database is refused too, and does not use up the promotion.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            standbyHost.PromoteAsync(handed with { Epoch = handed.Epoch + 1 }, cts.Token));
        Assert.True(standbyHost.IsStandby);

        ReleaseWriterLock(active);
        await standbyHost.PromoteAsync(handed, cts.Token);
        Assert.False(standbyHost.IsStandby);
        Assert.Equal(handed, standby.World.CharacterOwnership);

        // Promoted means promoted: the active world (never a standby) refuses.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Host(active).PromoteAsync(handed, cts.Token));
    }

    [Fact]
    public async Task ARefusedPromotion_OfAReleasedOwnership_ChangesNothing_AndClaimsNothing()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer active = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer standby = await StartStandbyAsync(active, cts.Token);
        WorldHost standbyHost = Host(standby);
        CharacterOwnershipToken old = active.World.CharacterOwnership!;

        // The active world gives the ownership back (as a clean stop does): the row is inactive, and no takeover handed it to the standby.
        await using (AsyncServiceScope scope = standby.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().ReleaseAsync(old, cts.Token);
        CharacterOwnershipRow released = await OwnershipAsync(standby);
        Assert.False(released.Active);

        // Neither the epoch a claim would produce nor any other is promoted, and the refusal claims nothing: a promotion only reads the row.
        foreach (long epoch in new[] { old.Epoch + 1, old.Epoch + 5 })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                standbyHost.PromoteAsync(old with { Epoch = epoch, Incarnation = standbyHost.Incarnation }, cts.Token));
            Assert.True(standbyHost.IsStandby);
            Assert.Null(standby.World.CharacterOwnership);
            Assert.False(standby.Services.GetRequiredService<WorkerAdmission>().IsOpen);
            CharacterOwnershipRow after = await OwnershipAsync(standby);
            Assert.False(after.Active);
            Assert.Null(after.OwnerIncarnation);
            Assert.Equal(released.Epoch, after.Epoch);
            Assert.Equal(released.WriteSequence, after.WriteSequence);
        }
    }

    internal static void ReleaseWriterLock(SyntheticArcaneServer server) => server.Services.GetRequiredService<WorldWriterGuard>().Dispose();

    [Fact]
    public async Task APromotion_IsRefusedWhileTheOldWorldHoldsTheWriterLock_AndSucceedsAfterItIsReleased()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer active = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer standby = await StartStandbyAsync(active, cts.Token);
        WorldHost standbyHost = Host(standby);
        WorldWriterGuard activeGuard = active.Services.GetRequiredService<WorldWriterGuard>();
        WorldWriterGuard standbyGuard = standby.Services.GetRequiredService<WorldWriterGuard>();
        Assert.NotNull(activeGuard.LockPath); // the active world holds the sidecar lock (the premise of this test: a file-backed SQLite database)
        Assert.Null(standbyGuard.LockPath);   // a standby holds none until it is promoted

        CharacterOwnershipToken handed = await TakeOverAsync(standby, active.World.CharacterOwnership!, cts.Token);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => standbyHost.PromoteAsync(handed, cts.Token));
        Assert.Contains("writer lock", refused.Message);
        Assert.Contains("stays a standby", refused.Message);
        Assert.True(standbyHost.IsStandby);
        Assert.Null(standbyGuard.LockPath);
        Assert.Null(standby.World.CharacterOwnership);
        Assert.False(standby.Services.GetRequiredService<WorkerAdmission>().IsOpen);

        ReleaseWriterLock(active);
        await standbyHost.PromoteAsync(handed, cts.Token); // the refused promotion did not use up the one promotion
        Assert.False(standbyHost.IsStandby);
        Assert.NotNull(standbyGuard.LockPath);
        Assert.Equal(handed, standby.World.CharacterOwnership);
    }

    internal static async Task<CharacterOwnershipToken> TakeOverAsync(SyntheticArcaneServer standby, CharacterOwnershipToken old, CancellationToken token)
    {
        WorldHost host = Host(standby);
        await using AsyncServiceScope scope = standby.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
            .TryTakeoverAsync(old, host.OwnerNode, host.Incarnation, token))!;
    }

    [Fact]
    public async Task APromotedStandby_TakesSessions_AndItsWritesPassTheFence_WhileTheOldWorldIsFenced()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using SyntheticArcaneServer active = await StartActiveAsync(cts.Token);
        await using SyntheticArcaneServer standby = await StartStandbyAsync(active, cts.Token);
        WorldHost standbyHost = Host(standby);
        CharacterOwnershipToken old = active.World.CharacterOwnership!;

        CharacterOwnershipToken handed = await TakeOverAsync(standby, old, cts.Token);

        ReleaseWriterLock(active); // the old world gives up its writer lock (a handoff does this after the takeover)
        await standbyHost.PromoteAsync(handed, cts.Token);
        Assert.False(standbyHost.IsStandby);
        Assert.Equal(handed, standby.World.CharacterOwnership);
        Assert.True(standby.Services.GetRequiredService<WorkerAdmission>().IsOpen);
        Assert.True(await standby.World.InvokeAsync(() => true).WaitAsync(cts.Token)); // promotion attached the features and started the simulation
        Assert.Same(standbyHost.PromoteAsync(handed, cts.Token), standbyHost.PromoteAsync(handed, cts.Token)); // once

        // Through the gateway the promoted world takes the login and creates a character (a fenced write under the new epoch).
        await using Linked linked = await LinkAsync(standby, cts.Token);
        await active.AddAccountAsync("NEWEPOCH", "PASSWORD", cts.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(active.RealmEndpoint, "NEWEPOCH", "PASSWORD", cts.Token);
        await using WorldClient client = await WorldClient.ConnectAsync(linked.Gateway.ListenEndPoint, cts.Token);
        Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync("NEWEPOCH", logon.SessionKey, cts.Token));
        await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate("Newepoch"), cts.Token);
        WorldFrame created = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, cts.Token);
        Assert.Equal(new byte[] { 0x2E }, created.Payload);

        // The old world still holds epoch N: its characters writes are refused.
        await using (AsyncServiceScope scope = active.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArcaneCore.Data.Characters.CharacterDbContext>();
            Exception? refused = await Record.ExceptionAsync(() =>
                new ArcaneCore.Data.Social.EfSocialStore(db).SetSocialAsync(1, 2, ArcaneCore.Kernel.Social.SocialFlags.Friend, cts.Token));
            Assert.True(Contains<CharacterOwnershipRejectedException>(refused), "the old world's write was not fenced: " + refused);
        }
    }

    internal static WorldHost Host(SyntheticArcaneServer server)
        => server.Services.GetServices<IHostedService>().OfType<WorldHost>().Single();

    internal static Task<SyntheticArcaneServer> StartActiveAsync(CancellationToken token) => SyntheticArcaneServer.StartAsync(
        new SyntheticServerOptions
        {
            EnableFencing = true,
            Configuration = Worker(standby: false),
        }, configureServices: null, token);

    internal static Task<SyntheticArcaneServer> StartStandbyAsync(SyntheticArcaneServer active, CancellationToken token) => SyntheticArcaneServer.StartAsync(
        new SyntheticServerOptions
        {
            EnableFencing = true,
            SharedDatabaseDirectory = active.DataDirectory,
            Configuration = Worker(standby: true),
        }, configureServices: null, token);

    private static Dictionary<string, string?> Worker(bool standby) => new()
    {
        ["Cluster:Worker:Enabled"] = "true",
        ["Cluster:Worker:Standby"] = standby ? "true" : "false",
        ["Cluster:Worker:Port"] = "0",
        ["Cluster:Worker:LinkSecret"] = Secret,
    };

    internal static async Task<CharacterOwnershipRow> OwnershipAsync(SyntheticArcaneServer server)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ArcaneCore.Data.Characters.CharacterDbContext>()
            .Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == "world");
    }

    internal static async Task<Linked> LinkAsync(SyntheticArcaneServer server, CancellationToken token, WorkerAdmission? admission = null)
    {
        IServiceProvider services = server.Services;
        // A worker-mode fixture registers its own endpoint (with its admission); otherwise build one by hand as GatewayEndToEndTests does.
        GatewayWorkerEndpoint endpoint = admission is null
            ? services.GetRequiredService<GatewayWorkerEndpoint>()
            : new GatewayWorkerEndpoint(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GatewayWorkerOptions { Enabled = true, Port = 0, LinkSecret = Secret }),
                services.GetRequiredService<IOptions<WorldSessionOptions>>(),
                services.GetRequiredService<ArcaneCore.World.Handlers.OpcodeTable>(),
                server.World,
                services.GetRequiredService<SessionRegistry>(),
                services.GetRequiredService<ILoggerFactory>(),
                admission);
        await endpoint.StartAsync(token);
        var gateway = new GatewayServer(
            server.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions
            {
                BindAddress = IPAddress.Loopback.ToString(),
                Port = 0,
                WorkerAddress = IPAddress.Loopback.ToString(),
                WorkerPort = endpoint.Host!.BoundEndPoint.Port,
                LinkSecret = Secret,
                LinkRetryDelay = TimeSpan.FromMilliseconds(100),
            }),
            server.Services.GetRequiredService<ILoggerFactory>());
        await gateway.StartAsync(token);
        await gateway.WaitForLinkAsync(token);
        return new Linked(endpoint, gateway);
    }

    internal static byte[] CharacterCreate(string name)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(name);
        byte[] payload = new byte[text.Length + 10];
        text.CopyTo(payload, 0);
        payload[text.Length + 1] = 1; // human
        payload[text.Length + 2] = 1; // warrior
        return payload;
    }

    internal static bool Contains<T>(Exception? error) where T : Exception
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is T)
                return true;
        }

        return false;
    }

    internal sealed record Linked(GatewayWorkerEndpoint Endpoint, GatewayServer Gateway) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
            await Endpoint.StopAsync(CancellationToken.None);
        }
    }
}
