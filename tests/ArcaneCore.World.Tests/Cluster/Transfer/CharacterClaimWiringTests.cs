using System.Reflection;
using System.Runtime.ExceptionServices;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// Step C5 of the per-worker character writer lock (_wf/writer-lock-DESIGN.md section 1b), the product wiring on top of <c>CharacterClaimTests</c>: the login claim and the
/// logout release run through the real login path of a world in Worker scope (<see cref="CharacterClaimService"/>, <see cref="CharacterClaimFeature"/>), the login-time reap is
/// built from <c>Cluster:Fencing:ReapDeadOwners</c>/<c>ReapAfterSeconds</c> and backed by the real shard advisory-lock probe, and the main character save queue and the honor writer
/// are claim-checked like the keyed queues. Claims live in a real SQLite characters database (real claim store, real write fence); the world itself runs on the in-memory harness.
/// </summary>
public sealed class CharacterClaimWiringTests
{
    private static readonly WorkerNodeId NodeA = new("node-a");
    private static readonly WorkerNodeId NodeB = new("node-b");

    [Fact]
    public async Task ARealLogin_ClaimsTheCharacter_AndTheLogoutReleasesItOnceTheSavesAreDurable()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await using WorldTestHost host = rig.StartWorld(a);
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, "CLAIMWIRE1", "Claimant");

        Assert.Null(await rig.Store.GetActiveOwnershipAsync((ulong)id));
        await client.LoginAsync((ulong)id);
        Assert.True(host.World.IsOnline(ObjectGuid.Player((uint)id)));
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync((ulong)id)); // claimed before the character was attached

        await client.DisposeAsync(); // logout
        await WorldTestHost.WaitForAsync(() => Task.Run(() => rig.Store.GetActiveOwnershipAsync((ulong)id)).GetAwaiter().GetResult() is null, "the claim is released after the logout save");
        Assert.False(host.World.IsOnline(ObjectGuid.Player((uint)id)));
    }

    [Fact]
    public async Task ADisconnect_AfterTryBeginLogin_BeforeEnterWorldRuns_ReleasesTheLoginsClaim_AndFaultsNothing()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await using WorldTestHost host = rig.StartWorld(a);
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, "CLAIMWIRE9", "Vanisher");
        host.ExpectSessionFaults = true; // a fault escaping the session lands in SessionFaults instead of vanishing
        var disposedFaults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Action<ArcaneCore.Kernel.Diagnostics.FaultNotice> watch = n =>
        {
            if (n.ExceptionType.Contains(nameof(ObjectDisposedException), StringComparison.Ordinal))
                disposedFaults.Enqueue(n.Site + ": " + n.Message);
        };
        ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.FaultNoticed += watch; // world-thread and transfer faults go through the ledger

        using var gate = new ManualResetEventSlim(false);
        using var blocked = new ManualResetEventSlim(false);
        try
        {
            // The world thread is held, so the login's EnterWorld command queues behind this one and cannot run yet.
            host.World.Post(() =>
            {
                blocked.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            });
            Assert.True(blocked.Wait(TimeSpan.FromSeconds(10)), "the world thread never reached the blocking command");

            var login = new PacketWriter(8);
            login.WriteUInt64((ulong)id);
            await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
            await WorldTestHost.WaitForAsync(() => host.Registry.Sessions.Any(s => s.State == SessionState.LoggingIn), "the session to pass TryBeginLogin");
            WorldSession session = host.Registry.Sessions.Single();
            Assert.Equal(a, (await rig.Store.GetActiveOwnershipAsync((ulong)id))); // the login claimed the character before it was attached

            await client.DisposeAsync(); // the disconnect lands between TryBeginLogin and EnterWorld
            await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session to close");
        }
        finally
        {
            gate.Set();
        }

        await WorldTestHost.WaitForAsync(() => Task.Run(() => rig.Store.GetActiveOwnershipAsync((ulong)id)).GetAwaiter().GetResult() is null, "the unentered login's claim to be released");
        Assert.False(host.World.IsOnline(ObjectGuid.Player((uint)id)));
        ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.FaultNoticed -= watch;
        Assert.Empty(host.SessionFaults);
        Assert.Empty(disposedFaults);
    }

    [Fact]
    public async Task ARealLogin_IsRefused_WhileAnotherLiveWorkerHoldsTheCharacter()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await using WorldTestHost host = rig.StartWorld(a);
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, "CLAIMWIRE2", "Taken");
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync((ulong)id, b)).Outcome);

        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)id);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        byte[] failed = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
        Assert.Equal((byte)CharResult.CharLoginDuplicateCharacter, failed[0]);
        Assert.Equal(0, host.World.OnlinePlayerCount);
        Assert.Equal(b, await rig.Store.GetActiveOwnershipAsync((ulong)id)); // the holder keeps it
    }

    [Fact]
    public async Task ALoginTimeReap_UsesTheConfiguredOptions_AndTheRealShardLockProbe()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        long stale = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

        // Shard a is silent for ten minutes but its advisory lock is still held: it is alive, so b's login is refused and nothing is reaped.
        await using (WorldTestHost held = rig.StartWorld(b))
        {
            (WorldTestClient client, int id) = await ConnectWithCharacterAsync(held, "CLAIMWIRE3", "Held");
            await rig.Store.TryClaimCharacterAsync((ulong)id, a);
            Assert.True(await rig.HeartbeatAsync(a, stale));
            using (CharacterDatabaseWriterLock.TryAcquireShard(rig.ConnectionOptions, "a")!)
            {
                var login = new PacketWriter(8);
                login.WriteUInt64((ulong)id);
                await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
                byte[] refused = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
                Assert.Equal((byte)CharResult.CharLoginDuplicateCharacter, refused[0]);
            }

            Assert.True((await rig.OwnershipRowAsync(a.Scope)).Active);
            Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync((ulong)id));
        }

        // The lock is free now (the process is gone) and the heartbeat is stale: the login reaps shard a and takes the character over.
        await using (WorldTestHost reaping = rig.StartWorld(b))
        {
            (WorldTestClient client, int id) = await ConnectWithCharacterAsync(reaping, "CLAIMWIRE4", "Reaped");
            await rig.Store.TryClaimCharacterAsync((ulong)id, a);
            Assert.True(await rig.HeartbeatAsync(a, stale));
            await client.LoginAsync((ulong)id);
            Assert.Equal(b, await rig.Store.GetActiveOwnershipAsync((ulong)id));
            Assert.False((await rig.OwnershipRowAsync(a.Scope)).Active);
        }

        // ReapDeadOwners off: the same silent, lock-free shard is left to an operator.
        await using (WorldTestHost off = rig.StartWorld(b, reapDeadOwners: false))
        {
            CharacterOwnershipToken a2 = await rig.AcquireWorkerAsync("a2", 2);
            (WorldTestClient client, int id) = await ConnectWithCharacterAsync(off, "CLAIMWIRE5", "Left");
            // Each world host numbers characters from its own in-memory store, so this character has the id the previous section's login took over, and that world's
            // shutdown kept b's claim (a claim still held when a world stops is taken over once its incarnation is dead, never deleted at shutdown). Drop it here so a2 holds it.
            await rig.Store.ReleaseCharacterClaimAsync((ulong)id, b);
            Assert.True((await rig.Store.TryClaimCharacterAsync((ulong)id, a2)).IsClaimed);
            Assert.True(await rig.HeartbeatAsync(a2, stale));
            var login = new PacketWriter(8);
            login.WriteUInt64((ulong)id);
            await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
            byte[] refused = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
            Assert.Equal((byte)CharResult.CharLoginDuplicateCharacter, refused[0]);
            Assert.True((await rig.OwnershipRowAsync(a2.Scope)).Active);
        }
    }

    [Fact]
    public async Task TheClaimService_IsInertWithoutAWorkerToken_AndAReleaseNeverDeletesAnotherHoldersClaim()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        CharacterOwnershipToken world = await rig.AcquireWorldAsync();
        using var claims = new CharacterClaimService(rig.Services);

        Assert.Equal(CharacterClaimOutcome.NotWorkerScope, (await claims.ClaimAsync(30, null)).Outcome);
        CharacterClaimResult realm = await claims.ClaimAsync(30, world);
        Assert.Equal(CharacterClaimOutcome.NotWorkerScope, realm.Outcome);
        Assert.True(CharacterClaimService.Admits(realm));
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(30));

        (CharacterClaimResult claimed, long ticket) = await claims.ClaimWithTicketAsync(31, a);
        Assert.Equal(CharacterClaimOutcome.Claimed, claimed.Outcome);
        Assert.True(CharacterClaimService.Admits(claimed));
        CharacterClaimResult refused = await claims.ClaimAsync(31, b);
        Assert.Equal(CharacterClaimOutcome.RefusedLiveOwner, refused.Outcome);
        Assert.False(CharacterClaimService.Admits(refused));

        claims.ScheduleRelease(31, b); // not the holder: the delete is exact on the token
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(31));

        // A release only deletes with the ticket of the login that is releasing (an unticketed one never deletes a claim a live login holds).
        claims.ScheduleRelease(31, a, () => true, ticket); // the character is online again: it keeps the claim
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(31));

        (CharacterClaimResult again, long secondTicket) = await claims.ClaimWithTicketAsync(31, a);
        Assert.Equal(CharacterClaimOutcome.AlreadyHeld, again.Outcome);
        claims.ScheduleRelease(31, a, () => false, secondTicket);
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(31));
    }

    [Fact]
    public async Task AReleaseWaitingForItsFlush_IsSupersededByANewLoginsClaim()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var gate = new TaskCompletionSource();
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        services.AddSingleton(new CharacterPersistenceBarrier(new SlowParticipantProvider(rig.Services, gate.Task)));
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);
        await claims.ClaimAsync(32, a);

        claims.ScheduleRelease(32, a); // waits in the persistence barrier until the gate opens
        CharacterClaimResult relogin = await claims.ClaimAsync(32, a); // the next login of the same character on this shard
        Assert.Equal(CharacterClaimOutcome.AlreadyHeld, relogin.Outcome);
        gate.SetResult();
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(32)); // the stale release did not strip the new session's claim
    }

    [Fact]
    public async Task AReleaseOfAnOlderLogin_NeverDeletesANewerLoginsClaim_WhenTheNewerClaimCameFirst_SessionReplacement()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        (CharacterClaimResult first, long ticket1) = await claims.ClaimWithTicketAsync(34, a);
        (CharacterClaimResult second, long ticket2) = await claims.ClaimWithTicketAsync(34, a); // the replacing session claims while the first still holds it
        Assert.Equal(CharacterClaimOutcome.Claimed, first.Outcome);
        Assert.Equal(CharacterClaimOutcome.AlreadyHeld, second.Outcome);
        Assert.True(ticket1 > 0);
        Assert.True(ticket2 > ticket1); // every claimed outcome, AlreadyHeld included, gets a newer ticket

        claims.ScheduleRelease(34, a, () => false, ticket1); // the replaced session's ReleaseUnenteredClaim, scheduled after the newer claim
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(34)); // the replacing session's claim survives

        claims.ScheduleRelease(34, a, () => false, ticket2); // the newest login's own release still works
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(34));
    }

    [Fact]
    public async Task ALogoutRelease_PostedAfterAQuickRelogin_KeepsTheRelogInsClaim_AndTheRelogInsOwnLogoutReleasesIt()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        (_, long loggedOut) = await claims.ClaimWithTicketAsync(35, a); // the login that is now logging out
        (_, long relogin) = await claims.ClaimWithTicketAsync(35, a); // the quick relogin claimed before the posted logout release ran
        Assert.True(relogin > loggedOut);

        claims.ScheduleRelease(35, a, () => false, loggedOut); // the posted ScheduleRelease of the logout runs now; the relogin has not entered the world yet, so isOnline says false
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(35));

        claims.ScheduleRelease(35, a, () => false, relogin);
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(35));
    }

    [Fact]
    public async Task ALogoutRelease_OfATransferredInLogin_PostedAfterAQuickRelogin_KeepsTheRelogInsClaim()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        Assert.True((await rig.Store.TryClaimCharacterAsync(37, a)).IsClaimed); // the claim a committed transfer moved to this worker's scope
        long transferredIn = claims.IssueTicketForReceivedClaim(37, a); // WorkerTransferTarget activation
        var shadow = new object();
        claims.BindTicket(shadow, transferredIn);
        Assert.True(transferredIn > 0);
        Assert.Equal(transferredIn, claims.TicketOf(shadow)); // the logout path reads a non-zero ticket, not an unticketed release

        (CharacterClaimResult relogin, long reloginTicket) = await claims.ClaimWithTicketAsync(37, a); // the quick relogin on the same worker claims first
        Assert.Equal(CharacterClaimOutcome.AlreadyHeld, relogin.Outcome);
        Assert.True(reloginTicket > transferredIn);

        claims.ScheduleRelease(37, a, () => false, claims.TicketOf(shadow)); // the posted logout release of the transferred-in player
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(37));

        claims.ScheduleRelease(37, a, () => false, reloginTicket);
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(37));
    }

    [Fact]
    public async Task AReceivedClaimsTicket_IsRefusedWithoutAWorkerToken()
    {
        var services = new ServiceCollection();
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        Assert.Equal(0L, claims.IssueTicketForReceivedClaim(38, null));
    }

    [Fact]
    public async Task AnAbandonedLoginsTicket_StopsInvalidatingTheOlderLoginsRelease()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        (_, long older) = await claims.ClaimWithTicketAsync(36, a);
        (_, long duplicate) = await claims.ClaimWithTicketAsync(36, a); // a second login of the online character, refused entry in EnterWorld
        claims.AbandonTicket(36, duplicate); // the older login keeps the claim, so its release is valid again

        claims.ScheduleRelease(36, a, () => false, older);
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(36));
    }

    [Fact]
    public async Task AnAbandonedLoginsTicket_ReleasesTheClaim_WhenNoOtherLoginIsLive_AndNobodyIsOnline()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        (_, long older) = await claims.ClaimWithTicketAsync(39, a);
        (_, long duplicate) = await claims.ClaimWithTicketAsync(39, a); // a second login of the character, claimed while the first was online
        claims.ScheduleRelease(39, a, () => false, older); // the first login logged out: its release is skipped, the duplicate's ticket is newer
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(39));

        claims.AbandonTicket(39, duplicate, a, () => false); // EnterWorld refused the duplicate and nobody is online: nothing else would ever release the claim
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(39));
    }

    [Fact]
    public async Task AnAbandonedLoginsTicket_KeepsTheClaim_WhileTheOlderLoginIsLive_OrTheCharacterIsOnline()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        (_, long older) = await claims.ClaimWithTicketAsync(40, a);
        (_, long duplicate) = await claims.ClaimWithTicketAsync(40, a);
        claims.AbandonTicket(40, duplicate, a, () => false); // nobody is online, so only the older live ticket keeps the claim: its logout owns the release
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(40));

        claims.ScheduleRelease(40, a, () => false, older);
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(40));

        (_, long first) = await claims.ClaimWithTicketAsync(41, a);
        (_, long second) = await claims.ClaimWithTicketAsync(41, a);
        claims.ScheduleRelease(41, a, () => false, first); // skipped: a newer ticket exists
        claims.AbandonTicket(41, second, a, () => true); // no live ticket is left but the character is online: the claim stays
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(41));
    }

    [Fact]
    public async Task ATicketlessRelease_RefusesToDeleteTheClaim_WhileALoginTicketIsLive()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        (_, long live) = await claims.ClaimWithTicketAsync(52, a);
        claims.ScheduleRelease(52, a, () => false); // ticket zero: what an abandoned login's release carries
        await claims.DrainAsync();
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(52)); // the live ticket keeps the claim

        claims.ScheduleRelease(52, a, () => false, live);
        await claims.DrainAsync();
        Assert.Null(await rig.Store.GetActiveOwnershipAsync(52));
    }

    [Fact]
    public async Task RegressionPin_AnAbandonedLoginsRelease_NeverDeletesTheClaimOfALoginThatClaimsRightAfter()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var services = new ServiceCollection();
        services.AddSingleton(rig.Store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);

        (_, long abandoned) = await claims.ClaimWithTicketAsync(53, a);

        // Hold the character's release gate so the scheduled release cannot run before the new login's ticket exists (the window of the race).
        System.Reflection.FieldInfo gates = typeof(CharacterClaimService).GetField("_gates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("CharacterClaimService._gates was renamed; update this test.");
        SemaphoreSlim gate = ((SemaphoreSlim[])gates.GetValue(claims)!)[53 % 32];
        await gate.WaitAsync();
        try
        {
            claims.AbandonTicket(53, abandoned, a, () => false); // the release marker is registered with the "no live ticket" observation
            long next = claims.IssueTicketForReceivedClaim(53, a); // a new login's ticket lands right after: it supersedes the pending release
            Assert.NotEqual(0L, next);
        }
        finally
        {
            gate.Release();
        }

        await claims.DrainAsync();

        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync(53));
    }

    [Fact]
    public async Task TheClaimTicket_IsBoundToThePlayerObject_ForTheLogoutPath()
    {
        var services = new ServiceCollection();
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var claims = new CharacterClaimService(provider);
        var player = new object();

        Assert.Equal(0L, claims.TicketOf(player));
        claims.BindTicket(player, 7);
        Assert.Equal(7L, claims.TicketOf(player));
    }

    [Fact]
    public async Task TwoConcurrentFirstClaims_OfTheSameCharacter_ExactlyOneWins_AndTheLoserIsRefusedNotThrown()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);

        CharacterClaimResult[] results = await Task.WhenAll(
            Task.Run(() => rig.Store.TryClaimCharacterAsync(33, a)),
            Task.Run(() => rig.Store.TryClaimCharacterAsync(33, b)));

        Assert.Single(results, r => r.IsClaimed);
        Assert.Single(results, r => r.Outcome == CharacterClaimOutcome.RefusedLiveOwner);
        CharacterOwnershipToken holder = (await rig.Store.GetActiveOwnershipAsync(33))!;
        Assert.Equal(results.Single(r => r.IsClaimed).Claim, holder);
    }

    [Fact]
    public async Task TheMainSaveQueuesSnapshot_OfARetiredSource_IsDurablyRefusedAfterTheCommit_AndTheTargetsIsWritten()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Retired");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);

        // While the claim is a's, a's snapshot lands.
        var queue = new CharacterSaveQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        queue.Start();
        rig.Holder.Current = a;
        queue.Enqueue(State(character, 5, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(5u, (await rig.CharacterAsync(character))!.MapId);

        // The character moves to b with a committed transfer. a still has a snapshot for it: refused by the database, dropped (not retained forever).
        await rig.MoveClaimAsync(character, a, b);
        rig.Holder.Current = a;
        queue.Enqueue(State(character, 7, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(queue.RetainedCharacters);
        Assert.Null(queue.TerminalFailure); // one character lost to another world never halts the queue
        Assert.Equal(5u, (await rig.CharacterAsync(character))!.MapId);

        // b writes the same character through the same queue type under its own claim.
        rig.Holder.Current = b;
        queue.Enqueue(State(character, 30, b));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(30u, (await rig.CharacterAsync(character))!.MapId);
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AnUnclaimedCharactersSnapshot_IsRefused_UntilTheLoginClaimsIt()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Unclaimed");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var queue = new CharacterSaveQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        queue.Start();
        rig.Holder.Current = a;

        queue.Enqueue(State(character, 9, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(9u, (await rig.CharacterAsync(character))!.MapId); // no claim, no write

        using var claims = new CharacterClaimService(rig.Services);
        Assert.True(CharacterClaimService.Admits(await claims.ClaimAsync(character, a)));
        queue.Enqueue(State(character, 9, a));
        await queue.FlushCharacterAsync(character).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(9u, (await rig.CharacterAsync(character))!.MapId);
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheHonorQueue_RefusesAndDropsARetiredSourcesWrite_AndTheTargetWritesItsOwn()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Honored");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        await rig.MoveClaimAsync(character, a, b);

        var queue = new HonorWriteQueue(rig.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        queue.Start();
        rig.Holder.Current = a;
        queue.SaveState(character, HonorState(42));
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(queue.RetainedCharacters);
        Assert.Null(queue.TerminalFailure);
        Assert.Equal(0u, (await rig.HonorOfAsync(character)).State.Standing);

        rig.Holder.Current = b;
        queue.SaveState(character, HonorState(77));
        await queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(77u, (await rig.HonorOfAsync(character)).State.Standing);
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The previous owner's final save lands after this login read the character and before its claim is admitted (the owner releases its claim only once that save is durable).
    /// The player must be built from the record read after the claim: the record read before it still has the old money and map, which would lose or duplicate copper and put the
    /// character on the wrong map.
    /// </summary>
    [Fact]
    public async Task ALogin_BuildsThePlayerFromTheRecordReadAfterItsClaim_WhenThePreviousOwnersFinalSaveLandsInBetween()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        StaleReadStore? reads = null;
        await using WorldTestHost host = rig.StartWorld(a, configure: services =>
        {
            ICharacterStore inner = (ICharacterStore)services.Last(d => d.ServiceType == typeof(ICharacterStore)).ImplementationInstance!;
            reads = StaleReadStore.Wrap(inner);
            services.AddSingleton((ICharacterStore)(object)reads);
        });
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, "CLAIMWIRE9", "Latesave");
        CharacterRecord record = (await host.Characters.GetByIdAsync(id))!;
        Assert.Equal(0u, record.Money);
        Assert.Equal(0u, record.MapId);

        // The previous owner (b, another worker) holds the character while this login starts. Right after the login's second read (the one after its drain), b's final save
        // lands (money and map change) and b releases its claim, so the login's claim is admitted with that read already in hand.
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync((ulong)id, b)).Outcome);
        reads!.Arm(id, readNumber: 2, async () =>
        {
            lock (record)
            {
                record.Money = 98765;
                record.MapId = 1;
                (record.X, record.Y, record.Z, record.Orientation) = (1629.36f, -4373.39f, 31.25f, 3.14f);
            }

            Assert.True(await rig.Store.ReleaseCharacterClaimAsync((ulong)id, b));
        });

        await client.LoginAsync((ulong)id);

        Assert.True(reads.Fired);
        ObjectGuid guid = ObjectGuid.Player((uint)id);
        (uint money, uint map) = await host.World.InvokeAsync(() =>
        {
            ArcaneCore.Game.Entities.Player player = host.World.FindOnlinePlayer(guid) ?? throw new InvalidOperationException("the character is not online");
            return (player.Money, player.MapId);
        });
        Assert.Equal(98765u, money);
        Assert.Equal(1u, map);
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync((ulong)id));
    }

    /// <summary>
    /// The offline arrival of a committed transfer (the client is gone) writes through the real write fence like a save-queue write: it is claim-checked (a live worker that does
    /// not hold the character's claim is refused), a token of another incarnation of the holder's scope is refused, and the holder's write moves only the position.
    /// </summary>
    [Fact]
    public async Task TheOfflineArrival_IsClaimChecked_RefusesAnotherIncarnationsToken_AndWritesOnlyThePosition()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Arriver", money: 4321);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a); // the commit gave the claim to a, the target
        var arrival = new CharacterStoreOfflineArrival(rig.Services.GetRequiredService<IServiceScopeFactory>());
        PlayerTransferBundle bundle = PlayerTransferValidatorTests.CreateSampleValidBundle(Guid.NewGuid(), ObjectGuid.Player((uint)character).Value, NodeA, NodeB) with
        {
            CharacterId = character,
            AccountId = 1,
            NotLeaveTransport = false,
        };

        // b is a live worker with a valid token of its own, but the claim is a's.
        rig.Holder.Current = b;
        await Assert.ThrowsAsync<CharacterClaimRejectedException>(() => arrival.CompleteAsync(bundle, b, 0, CancellationToken.None));

        // a's scope under an incarnation that is not the live one (a restarted target's dead token, say).
        var stale = new CharacterOwnershipToken(a.Scope, a.Epoch, Guid.NewGuid());
        rig.Holder.Current = stale;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => arrival.CompleteAsync(bundle, stale, 0, CancellationToken.None));
        Assert.Equal(0u, (await rig.CharacterAsync(character))!.MapId);

        rig.Holder.Current = a;
        await arrival.CompleteAsync(bundle, a, 0, CancellationToken.None);
        CharacterRecord stored = (await rig.CharacterAsync(character))!;
        Assert.Equal(bundle.TargetMapId, stored.MapId);
        Assert.Equal(bundle.TargetPosition.X, stored.X);
        Assert.Equal(bundle.TargetPosition.Y, stored.Y);
        Assert.Equal(4321u, stored.Money);
        Assert.Equal(1, stored.Level);
    }

    /// <summary>
    /// A claim that reached a worker through a committed transfer whose character nobody plays there is released once the transfer is marked completed (no logout would
    /// release it), so another worker's login can claim the character; while the transfer is still in flight the release deletes nothing and waits.
    /// </summary>
    [Fact]
    public async Task AReceivedClaimsRelease_WaitsForTheTransferToComplete_ThenAnotherWorkerCanClaim()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Received");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        Guid transfer = await rig.CommitClaimMoveAsync(character, a, b); // committed to b, not yet completed
        using var claims = new CharacterClaimService(rig.Services) { TransferSettlePoll = TimeSpan.FromMilliseconds(50) };

        claims.ScheduleReleaseOfReceivedClaim(character, b, () => false);
        await Task.Delay(400);
        Assert.Equal(b.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope); // still in flight: nothing deleted
        Assert.Equal(CharacterClaimOutcome.RefusedTransferInFlight, (await rig.Store.TryClaimCharacterAsync((ulong)character, a)).Outcome);

        await rig.Store.MarkCompletedAsync(transfer);
        await WorldTestHost.WaitForAsync(() => Task.Run(() => rig.Store.GetActiveOwnershipAsync((ulong)character)).GetAwaiter().GetResult() is null, "the received claim is released once the transfer completed");
        await claims.DrainAsync();
        Assert.True((await rig.Store.TryClaimCharacterAsync((ulong)character, a)).IsClaimed);
    }

    /// <summary>A failed poll of the claim row says nothing about the transfer: the received-claim release logs it, keeps polling, and still releases once the transfer completed.</summary>
    [Fact]
    public async Task AReceivedClaimsRelease_SurvivesAFailedPoll_AndStillReleasesOnceTheTransferCompleted()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("PollFail");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        Guid transfer = await rig.CommitClaimMoveAsync(character, a, b);
        var scopes = new FailingScopeFactory(rig.Services.GetRequiredService<IServiceScopeFactory>());
        var flaky = new EfTransferDecisionStore(scopes);
        using var claims = new CharacterClaimService(new StoreOverrideProvider(rig.Services, flaky)) { TransferSettlePoll = TimeSpan.FromMilliseconds(50) };

        scopes.FailNext(1); // the first poll throws
        claims.ScheduleReleaseOfReceivedClaim(character, b, () => false);
        await WorldTestHost.WaitForAsync(() => scopes.Failures >= 1, "the first poll to fail");
        await Task.Delay(300); // later polls run: the release did not end on the failure
        Assert.False(claims.DrainAsync().IsCompleted, "the release ended on a failed poll");
        Assert.Equal(b.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope);

        await rig.Store.MarkCompletedAsync(transfer);
        await WorldTestHost.WaitForAsync(() => Task.Run(() => rig.Store.GetActiveOwnershipAsync((ulong)character)).GetAwaiter().GetResult() is null, "the received claim is released after the failed poll");
        await claims.DrainAsync();
    }

    /// <summary>A character this process gave away with a handoff is refused before anything is read, even under the token that holds its claim, and nothing is written.</summary>
    [Fact]
    public async Task TheOfflineArrival_OfARetiredCharacter_IsRefused_AndWritesNothing()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Retiree", money: 4321);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        rig.Holder.Current = a;
        var retirement = new CharacterRetirementRegistry();
        retirement.Retire([character]);
        var arrival = new CharacterStoreOfflineArrival(rig.Services.GetRequiredService<IServiceScopeFactory>(), retirement);
        PlayerTransferBundle bundle = PlayerTransferValidatorTests.CreateSampleValidBundle(Guid.NewGuid(), ObjectGuid.Player((uint)character).Value, NodeA, NodeB) with
        {
            CharacterId = character,
            AccountId = 1,
            NotLeaveTransport = false,
        };

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => arrival.CompleteAsync(bundle, a, 0, CancellationToken.None));

        Assert.Contains("retired", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0u, (await rig.CharacterAsync(character))!.MapId);
    }

    /// <summary>A login of the character that supersedes the pending release ends its poll while the transfer is still unsettled, and the claim stays.</summary>
    [Fact]
    public async Task AReceivedClaimsReleasePoll_EndsWhenALoginSupersedesIt()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Superseded");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        await rig.CommitClaimMoveAsync(character, a, b); // never completed
        using var claims = new CharacterClaimService(rig.Services) { TransferSettlePoll = TimeSpan.FromMilliseconds(50) };

        claims.ScheduleReleaseOfReceivedClaim(character, b, () => false);
        await Task.Delay(300);
        Assert.False(claims.DrainAsync().IsCompleted, "the poll must still be waiting for the transfer");

        claims.ScheduleRelease(character, b, () => false); // a later release registers its own marker: the first poll is superseded
        await claims.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(b.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope);
    }

    /// <summary>Disposing the service cancels a poll that waits for an unsettled transfer (no 60 s limit any more), and the claim stays.</summary>
    [Fact]
    public async Task AReceivedClaimsReleasePoll_EndsWhenTheServiceIsDisposed()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Disposed");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        await rig.CommitClaimMoveAsync(character, a, b); // never completed
        var claims = new CharacterClaimService(rig.Services) { TransferSettlePoll = TimeSpan.FromMilliseconds(50) };

        claims.ScheduleReleaseOfReceivedClaim(character, b, () => false);
        await Task.Delay(300);
        Task pending = claims.DrainAsync();
        Assert.False(pending.IsCompleted, "the poll must still be waiting for the transfer");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        claims.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "Dispose waited out its grace period: the poll did not observe the cancellation");
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(b.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope);
    }

    /// <summary>A scope factory that throws from the next N scope creations, then delegates.</summary>
    private sealed class FailingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _failNext;
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public void FailNext(int count) => Volatile.Write(ref _failNext, count);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Decrement(ref _failNext) >= 0)
            {
                Interlocked.Increment(ref _failures);
                throw new InvalidOperationException("injected store failure");
            }

            Interlocked.Exchange(ref _failNext, 0);
            return inner.CreateScope();
        }
    }

    /// <summary>The rig's provider, except that <see cref="EfTransferDecisionStore"/> resolves to the given instance.</summary>
    private sealed class StoreOverrideProvider(IServiceProvider inner, EfTransferDecisionStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(EfTransferDecisionStore) ? store : inner.GetService(serviceType);
    }

    [Fact]
    public async Task AReceivedClaimsRelease_KeepsTheClaim_WhenTheCharacterIsOnlineByThen()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Online");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        await rig.Store.MarkCompletedAsync(await rig.CommitClaimMoveAsync(character, a, b));
        using var claims = new CharacterClaimService(rig.Services) { TransferSettlePoll = TimeSpan.FromMilliseconds(50) };

        claims.ScheduleReleaseOfReceivedClaim(character, b, () => true);
        await claims.DrainAsync();
        Assert.Equal(b.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope);
    }

    /// <summary>An <see cref="ICharacterStore"/> whose reads of one character return copies, and which runs a callback right after the Nth read of it (before returning it).</summary>
    public class StaleReadStore : DispatchProxy
    {
        private ICharacterStore _inner = null!;
        private int _armedId;
        private int _fireAt;
        private int _reads;
        private Func<Task>? _onRead;

        public bool Fired { get; private set; }

        public static StaleReadStore Wrap(ICharacterStore inner)
        {
            object proxy = Create<ICharacterStore, StaleReadStore>();
            var store = (StaleReadStore)proxy;
            store._inner = inner;
            return store;
        }

        public void Arm(int characterId, int readNumber, Func<Task> onRead)
        {
            _armedId = characterId;
            _fireAt = readNumber;
            _reads = 0;
            _onRead = onRead;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(ICharacterStore.GetByIdAsync) && args is [int id, CancellationToken ct] && _onRead is not null && id == _armedId)
            {
                return ReadAsync(id, ct);
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        }

        private async Task<CharacterRecord?> ReadAsync(int id, CancellationToken ct)
        {
            CharacterRecord? record = await _inner.GetByIdAsync(id, ct);
            CharacterRecord? copy = record is null ? null : Copy(record);
            if (++_reads == _fireAt && _onRead is { } onRead)
            {
                _onRead = null;
                await onRead();
                Fired = true;
            }

            return copy;
        }

        private static CharacterRecord Copy(CharacterRecord source)
        {
            var copy = new CharacterRecord { Name = source.Name };
            lock (source)
            {
                foreach (PropertyInfo property in typeof(CharacterRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                        property.SetValue(copy, property.GetValue(source));
                }
            }

            return copy;
        }
    }

    private static CharacterState State(int id, uint map, CharacterOwnershipToken ownership)
        => new(id, map, 1, 1, 2, 3, 0, 1, 1, Ownership: ownership);

    private static CharacterHonorState HonorState(uint standing) => new(0f, 0, standing, 0, 0f, 0, 0, 0, false);

    private static async Task<(WorldTestClient Client, int Id)> ConnectWithCharacterAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        return (client, record.Id);
    }

    /// <summary>Gives <see cref="CharacterPersistenceBarrier"/> one participant that waits for a gate, so a release can be held inside its flush.</summary>
    private sealed class SlowParticipantProvider(IServiceProvider inner, Task gate) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IEnumerable<ICharacterPersistenceParticipant>)
                ? new ICharacterPersistenceParticipant[] { new GatedParticipant(gate) }
                : inner.GetService(serviceType);
    }

    private sealed class GatedParticipant(Task gate) : ICharacterPersistenceParticipant
    {
        public string Name => "gated";

        public async Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default) => await gate.WaitAsync(cancellationToken);
    }

    /// <summary>One SQLite characters database with the write fence on and a process-wide ownership holder the cases switch between workers, plus a factory for Worker-scope worlds on top of it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string _file;

        private Rig(ServiceProvider services, string file)
        {
            _services = services;
            _file = file;
            Holder = services.GetRequiredService<CharacterOwnershipContext>();
            Store = services.GetRequiredService<EfTransferDecisionStore>();
        }

        public CharacterOwnershipContext Holder { get; }
        public EfTransferDecisionStore Store { get; }
        public IServiceProvider Services => _services;

        public DatabaseConnectionOptions ConnectionOptions => new() { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={_file}" };

        public static async Task<Rig> CreateAsync(TempDirectory directory)
        {
            string file = Path.Combine(directory.Path, "characters.db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={file}",
                [DataServiceCollectionExtensions.FencingEnabledKey] = "true",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCharacterDatabase(configuration);
            ServiceProvider provider = services.BuildServiceProvider();
            await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            return new Rig(provider, file);
        }

        /// <summary>
        /// A Worker-scope world on the in-memory harness: its ownership token is <paramref name="token"/>, and its claim service, persistence barrier and shard-lock guard run
        /// against this rig's database with <c>ReapDeadOwners</c> as given and the default <c>ReapAfterSeconds</c>.
        /// </summary>
        public WorldTestHost StartWorld(CharacterOwnershipToken token, bool reapDeadOwners = true, Action<IServiceCollection>? configure = null)
        {
            WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(Store);
                services.AddSingleton(Options.Create(new CharacterFencingOptions { Scope = CharacterFencingScope.Worker, ReapDeadOwners = reapDeadOwners }));
                services.AddSingleton(new WorldWriterGuard(Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={_file}" })));
                services.TryAddSingleton<CharacterPersistenceBarrier>();
                services.TryAddSingleton<CharacterClaimService>();
                configure?.Invoke(services);
            });
            host.World.CharacterOwnership = token;
            return host;
        }

        public async Task<int> CreateCharacterAsync(string name, uint money = 0)
        {
            // Before any ownership row is active, so the tokenless write is allowed.
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1, Money = money })).Id;
        }

        public async Task<CharacterRecord?> CharacterAsync(int characterId)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(characterId);
        }

        public async Task<CharacterHonorData> HonorOfAsync(int characterId)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IHonorStore>().LoadAsync(characterId);
        }

        public async Task<CharacterOwnershipToken> AcquireWorkerAsync(string shard, uint map)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
                .TryAcquireAsync(CharacterOwnershipScopes.ForShard(shard), "node-" + shard, Guid.NewGuid(), [map]))!;
        }

        public async Task<CharacterOwnershipToken> AcquireWorldAsync()
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryAcquireAsync("world", "node-world", Guid.NewGuid()))!;
        }

        public async Task<bool> HeartbeatAsync(CharacterOwnershipToken token, long nowUnixMs)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().HeartbeatAsync(token, nowUnixMs);
        }

        public async Task<CharacterOwnershipRow> OwnershipRowAsync(string scopeName)
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            return await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scopeName);
        }

        /// <summary>Moves the claim of <paramref name="character"/> from <paramref name="from"/> to <paramref name="to"/> with a full committed transfer (what a cross-worker handoff does).</summary>
        public async Task MoveClaimAsync(int character, CharacterOwnershipToken from, CharacterOwnershipToken to)
        {
            Guid transfer = Guid.NewGuid();
            await Store.CreateReservationAsync(transfer, (ulong)character, 1, 1, NodeA, NodeB, 1, 0, 30, 0, from);
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, (ulong)character, NodeA, NodeB, from));
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.InstallPassive);
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.StageWorldPort);
            await Store.CommitDecisionAsync(transfer, to.Incarnation, CancellationToken.None, to.Scope);
            await Store.MarkCompletedAsync(transfer);
        }

        /// <summary>Moves the claim of <paramref name="character"/> from <paramref name="from"/> to <paramref name="to"/> up to the commit, without marking the transfer completed; returns its id.</summary>
        public async Task<Guid> CommitClaimMoveAsync(int character, CharacterOwnershipToken from, CharacterOwnershipToken to)
        {
            Guid transfer = Guid.NewGuid();
            await Store.CreateReservationAsync(transfer, (ulong)character, 1, 1, NodeA, NodeB, 1, 0, 30, 0, from);
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.SuspendAndCapture, PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, (ulong)character, NodeA, NodeB, from));
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.InstallPassive);
            await Store.AdvancePreCommitStageAsync(transfer, PlayerTransferStage.StageWorldPort);
            await Store.CommitDecisionAsync(transfer, to.Incarnation, CancellationToken.None, to.Scope);
            return transfer;
        }

        public async ValueTask DisposeAsync() => await _services.DisposeAsync();
    }
}
