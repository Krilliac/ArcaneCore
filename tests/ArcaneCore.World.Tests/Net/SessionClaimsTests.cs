using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// Verification of M15.0 item A4 / M15.1 prerequisite: <see cref="AccountSessionClaim"/>,
/// <see cref="IAccountSessionClaimStore"/>, and <see cref="ISessionClaims"/> on <see cref="SessionRegistry"/>.
/// </summary>
public sealed class SessionClaimsTests
{
    [Fact]
    public void ClaimStore_AssignsStrictlyMonotonicGenerations_EvenAcrossReleaseAndInvalidate()
    {
        var store = new InMemoryAccountSessionClaimStore();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();

        Assert.Equal(0, store.GetGeneration(42));
        Assert.Null(store.GetActive(42));

        AccountSessionClaim claim1 = store.Claim(42, nodeA);
        Assert.Equal(new AccountSessionClaim(42, 1, nodeA), claim1);
        Assert.Equal(claim1, store.GetActive(42));
        Assert.Equal(1, store.GetGeneration(42));

        // Clean release clears the active claim but preserves the generation high-water mark.
        Assert.True(store.TryRelease(claim1));
        Assert.Null(store.GetActive(42));
        Assert.Equal(1, store.GetGeneration(42));

        AccountSessionClaim claim2 = store.Claim(42, nodeB);
        Assert.Equal(new AccountSessionClaim(42, 2, nodeB), claim2);
        Assert.Equal(claim2, store.GetActive(42));
        Assert.Equal(2, store.GetGeneration(42));

        // Explicit invalidation advances the generation floor and clears the active claim.
        long floor = store.Invalidate(42);
        Assert.Equal(3, floor);
        Assert.Null(store.GetActive(42));
        Assert.Equal(3, store.GetGeneration(42));

        AccountSessionClaim claim4 = store.Claim(42, nodeA);
        Assert.Equal(new AccountSessionClaim(42, 4, nodeA), claim4);
    }

    [Fact]
    public void ClaimStore_StaleOrForeignRelease_NeverClearsNewerGeneration()
    {
        var store = new InMemoryAccountSessionClaimStore();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();

        AccountSessionClaim claim1 = store.Claim(7, nodeA);
        AccountSessionClaim claim2 = store.Claim(7, nodeB);

        // Late release of generation 1 after generation 2 took over must be a no-op.
        Assert.False(store.TryRelease(claim1));
        Assert.Equal(claim2, store.GetActive(7));

        // Release with matching generation but wrong NodeIncarnation must also be rejected.
        Assert.False(store.TryRelease(claim2 with { NodeIncarnation = nodeA }));
        Assert.Equal(claim2, store.GetActive(7));

        // Exact active claim releases cleanly.
        Assert.True(store.TryRelease(claim2));
        Assert.Null(store.GetActive(7));
    }

    [Fact]
    public async Task Reconnect_IncrementsSessionGeneration_AndLateStaleUnregisterDoesNotClearCurrentClaim()
    {
        await using var host = WorldTestHost.Start();
        ISessionClaims claims = host.WorldServices.GetRequiredService<ISessionClaims>();
        Assert.Same(host.Registry, claims);

        byte[] key = await host.AddAccountAsync("RECONNECTOR");
        Account account = (await host.Accounts.FindByUsernameAsync("RECONNECTOR"))!;

        WorldTestClient client1 = await host.ConnectAsync();
        await client1.AuthenticateAsync("RECONNECTOR", key);

        WorldSession? session1 = host.Registry.Find(account.Id);
        Assert.NotNull(session1);
        Assert.Equal(1, session1.SessionGeneration);
        Assert.Equal(1, ((IPlayerSession)session1).SessionGeneration);
        Assert.Equal(new AccountSessionClaim(account.Id, 1, claims.NodeIncarnation), session1.SessionClaim);
        Assert.True(claims.IsCurrent(session1));
        Assert.True(claims.IsCurrent(account.Id, 1));

        // Second login for the same account replaces generation 1 with generation 2.
        WorldTestClient client2 = await host.ConnectAsync();
        await client2.AuthenticateAsync("RECONNECTOR", key);

        WorldSession? session2 = host.Registry.Find(account.Id);
        Assert.NotNull(session2);
        Assert.NotSame(session1, session2);
        Assert.Equal(2, session2.SessionGeneration);
        Assert.Equal(new AccountSessionClaim(account.Id, 2, claims.NodeIncarnation), claims.GetClaim(account.Id));
        Assert.False(claims.IsCurrent(session1));
        Assert.False(session1.IsCurrentSession);
        Assert.True(claims.IsCurrent(session2));
        Assert.True(session2.IsCurrentSession);

        // Even if session1's Unregister runs again after session2 is registered, session2's claim and registration survive.
        host.Registry.Unregister(session1);
        Assert.Same(session2, host.Registry.Find(account.Id));
        Assert.Equal(new AccountSessionClaim(account.Id, 2, claims.NodeIncarnation), claims.GetClaim(account.Id));
        Assert.True(claims.IsCurrent(session2));

        // And a superseded session cannot begin a character login even if called directly.
        Assert.False(session1.TryBeginLogin());
    }

    [Fact]
    public async Task CrossGatewayReplacement_SharedClaimStore_RevokesAndKicksPreviousGatewaySession()
    {
        var sharedClaims = new InMemoryAccountSessionClaimStore();
        var sharedAccounts = new InMemoryAccountStore();
        byte[] key = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength);
        Account account = await sharedAccounts.CreateAsync(new Account
        {
            Username = "ROAMER",
            Salt = new byte[32],
            Verifier = new byte[32],
            SessionKey = key,
        });

        await using var gatewayA = WorldTestHost.Start(configureServices: s =>
        {
            s.AddSingleton<IAccountStore>(sharedAccounts);
            s.AddSingleton<IAccountSessionClaimStore>(sharedClaims);
        });
        await using var gatewayB = WorldTestHost.Start(configureServices: s =>
        {
            s.AddSingleton<IAccountStore>(sharedAccounts);
            s.AddSingleton<IAccountSessionClaimStore>(sharedClaims);
        });

        Assert.NotEqual(gatewayA.Registry.NodeIncarnation, gatewayB.Registry.NodeIncarnation);

        // Log into gateway A first (generation 1).
        WorldTestClient clientA = await gatewayA.ConnectAsync();
        await clientA.AuthenticateAsync("ROAMER", key);
        await clientA.CreateCharacterAsync("Roamhero");
        ulong charGuid = (ulong)(await gatewayA.Characters.GetByAccountAsync(account.Id)).Single().Id;
        await clientA.LoginAsync(charGuid);

        WorldSession? sessionA = gatewayA.Registry.Find(account.Id);
        Assert.NotNull(sessionA);
        Assert.Equal(1, sessionA.SessionGeneration);
        Assert.Equal(new AccountSessionClaim(account.Id, 1, gatewayA.Registry.NodeIncarnation), sharedClaims.GetActive(account.Id));

        // Now authenticate the same account on gateway B: generation advances to 2 and gateway A's session is revoked & kicked.
        WorldTestClient clientB = await gatewayB.ConnectAsync();
        await clientB.AuthenticateAsync("ROAMER", key);

        WorldSession? sessionB = gatewayB.Registry.Find(account.Id);
        Assert.NotNull(sessionB);
        Assert.Equal(2, sessionB.SessionGeneration);
        Assert.Equal(new AccountSessionClaim(account.Id, 2, gatewayB.Registry.NodeIncarnation), sharedClaims.GetActive(account.Id));
        Assert.True(gatewayB.Registry.IsCurrent(sessionB));

        // Gateway A's registry immediately dropped sessionA and kicked it, removing its player from gateway A's world.
        Assert.Null(gatewayA.Registry.Find(account.Id));
        Assert.False(gatewayA.Registry.IsCurrent(sessionA));
        Assert.False(sessionA.IsCurrentSession);
        await gatewayA.WaitForWorldAsync(() => gatewayA.World.OnlinePlayerCount == 0, "gateway A player removed after cross-gateway supersession");

        // When sessionA finishes closing and calls Unregister on gateway A, gateway B's generation-2 claim remains intact.
        gatewayA.Registry.Unregister(sessionA);
        Assert.Equal(new AccountSessionClaim(account.Id, 2, gatewayB.Registry.NodeIncarnation), sharedClaims.GetActive(account.Id));
        Assert.Same(sessionB, gatewayB.Registry.Find(account.Id));
        Assert.True(gatewayB.Registry.IsCurrent(sessionB));
    }

    [Fact]
    public async Task Invalidate_AdvancesGenerationFloor_AndKicksActiveSession()
    {
        await using var host = WorldTestHost.Start();
        ISessionClaims claims = host.WorldServices.GetRequiredService<ISessionClaims>();

        await host.EnterWorldAsync("INVALIDATED", "Targethero");
        Account account = (await host.Accounts.FindByUsernameAsync("INVALIDATED"))!;
        WorldSession? session = host.Registry.Find(account.Id);
        Assert.NotNull(session);
        Assert.Equal(1, session.SessionGeneration);

        long invalidatedGen = claims.Invalidate(account.Id);
        Assert.Equal(2, invalidatedGen);
        Assert.Null(claims.GetClaim(account.Id));
        Assert.Null(host.Registry.Find(account.Id));
        Assert.False(claims.IsCurrent(session));

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "invalidated session kicked from world");
    }

    [Fact]
    public void ConsoleSession_HasZeroGeneration_AndRemainsCurrentWithoutRegistryClaim()
    {
        using var registry = new SessionRegistry();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var saveQueue = new ArcaneCore.World.Persistence.CharacterSaveQueue(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ArcaneCore.World.Persistence.CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);

        WorldSession console = WorldSession.CreateConsole(
            services, new ArcaneCore.World.Handlers.OpcodeTable(), world, registry, new WorldSessionOptions(), NullLogger.Instance);

        Assert.Equal(0, console.AccountId);
        Assert.Equal(0, console.SessionGeneration);
        Assert.Null(console.SessionClaim);
        Assert.True(console.IsCurrentSession);
    }
}
