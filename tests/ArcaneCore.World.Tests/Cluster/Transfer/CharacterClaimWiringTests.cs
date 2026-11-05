using System.Reflection;
using System.Runtime.ExceptionServices;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.WorldData;
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
/// Tests that read the process-wide <see cref="ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared"/> before and after a scenario. xunit runs a collection with parallelization disabled alone, after
/// the parallel ones finished, so no other test records into the ledger or calls <c>Reset</c> (the fault-ledger tests of the assembly do) while such a scenario runs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedFaultLedgerCollection
{
    public const string Name = "Shared fault ledger";
}

/// <summary>
/// Step C5 of the per-worker character writer lock (_wf/writer-lock-DESIGN.md section 1b), the product wiring on top of <c>CharacterClaimTests</c>: the login claim and the
/// logout release run through the real login path of a world in Worker scope (<see cref="CharacterClaimService"/>, <see cref="CharacterClaimFeature"/>), the login-time reap is
/// built from <c>Cluster:Fencing:ReapDeadOwners</c>/<c>ReapAfterSeconds</c> and backed by the real shard advisory-lock probe, and the main character save queue and the honor writer
/// are claim-checked like the keyed queues. Claims live in a real SQLite characters database (real claim store, real write fence); the world itself runs on the in-memory harness.
/// </summary>
[Collection(SharedFaultLedgerCollection.Name)]
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
        // FaultLedger.Shared is process-wide and throttles what it announces, so no handler is attached: the count of every recorded ObjectDisposedException is read before and after instead. This class is in a
        // collection that runs alone, so nothing else records or resets the ledger meanwhile, and no frame filter is needed (a disposed-scope fault thrown deeper in a feature counts too).
        long epoch = ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Epoch;
        long disposedBefore = DisposedFaults();

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
        Assert.Empty(host.SessionFaults);
        Assert.Equal(epoch, ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Epoch); // a Reset in between would hide a fault: the comparison below would not mean anything
        Assert.Equal(disposedBefore, DisposedFaults());
    }

    /// <summary>Every recorded occurrence (throttling only limits what is logged, not what is counted) of an ObjectDisposedException, whatever its frames.</summary>
    private static long DisposedFaults() => ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Snapshot()
        .Where(f => f.ExceptionType.Contains(nameof(ObjectDisposedException), StringComparison.Ordinal))
        .Sum(f => f.Count);

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
                (record.HomeMapId, record.HomeZoneId, record.HomeX, record.HomeY, record.HomeZ) = (1u, 14u, 1500.5f, -4400.25f, 40.75f);
            }

            Assert.True(await rig.Store.ReleaseCharacterClaimAsync((ulong)id, b));
        });

        await client.LoginAsync((ulong)id);

        Assert.True(reads.Fired);
        ObjectGuid guid = ObjectGuid.Player((uint)id);
        (uint money, uint map, HomeBind home) = await host.World.InvokeAsync(() =>
        {
            ArcaneCore.Game.Entities.Player player = host.World.FindOnlinePlayer(guid) ?? throw new InvalidOperationException("the character is not online");
            return (player.Money, player.MapId, player.Home);
        });
        Assert.Equal(98765u, money);
        Assert.Equal(1u, map);
        Assert.Equal(new HomeBind(1u, 14u, 1500.5f, -4400.25f, 40.75f), home); // the bind point the previous owner saved, not the one read before the claim
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync((ulong)id));
    }

    [Fact]
    public Task ALogin_GivesTheClaimBack_WhenTheReadAfterTheClaimFails()
        => AssertPostClaimFailureGivesTheClaimBackAsync("CLAIMWIRE13", "Rereader", (_, trip, reads) => reads.BeforeRead = () => trip.ThrowWhileClaimedAsync("the read after the claim failed"));

    [Fact]
    public Task ALogin_GivesTheClaimBack_WhenTheRenameGateRefusesAfterTheClaim()
        => AssertPostClaimFailureGivesTheClaimBackAsync("CLAIMWIRE10", "Renamer",
            (services, trip, _) => services.AddSingleton<ICharacterRenameStore>(new PostClaimRenameStore(trip)));

    [Fact]
    public Task ALogin_GivesTheClaimBack_WhenTheWorldDataIsMissingAfterTheClaim()
        => AssertPostClaimFailureGivesTheClaimBackAsync("CLAIMWIRE11", "Nodata",
            (services, trip, _) => services.AddSingleton<IWorldDataStore>(new PostClaimWorldData(
                (IWorldDataStore)services.Last(d => d.ServiceType == typeof(IWorldDataStore)).ImplementationInstance!, trip, throws: false)),
            alterRecordWhenClaimed: ChangeRace);

    [Fact]
    public Task ALogin_GivesTheClaimBack_WhenTheWorldDataReadThrowsAfterTheClaim()
        => AssertPostClaimFailureGivesTheClaimBackAsync("CLAIMWIRE12", "Databoom",
            (services, trip, _) => services.AddSingleton<IWorldDataStore>(new PostClaimWorldData(
                (IWorldDataStore)services.Last(d => d.ServiceType == typeof(IWorldDataStore)).ImplementationInstance!, trip, throws: true)),
            alterRecordWhenClaimed: ChangeRace);

    /// <summary>
    /// The post-claim race and class lookup is made only when the record read after the claim names another race, gender or class than the one read before it, so the tests of a
    /// failing lookup change the race while the claim is held (the in-memory record is the one the login reads next).
    /// </summary>
    private static void ChangeRace(CharacterRecord record) => record.Race = (byte)(record.Race == 1 ? 2 : 1);

    /// <summary>The bind point is the one field the previous owner's save can move: these tests clear it on the record read after the claim, so the bind point has to come from the start position.</summary>
    private static void ClearHome(CharacterRecord record) => (record.HomeMapId, record.HomeZoneId, record.HomeX, record.HomeY, record.HomeZ) = (0u, 0u, 0f, 0f, 0f);

    /// <summary>Race, gender and class unchanged across the claim, so only the bind point is looked up again, and its start-position lookup throws.</summary>
    [Fact]
    public Task ALogin_GivesTheClaimBack_WhenTheStartPositionLookupThrowsAfterTheClaim_WithTheRaceUnchanged()
        => AssertPostClaimFailureGivesTheClaimBackAsync("CLAIMWIRE16", "Startboom",
            (services, trip, _) => services.AddSingleton<IWorldDataStore>(new PostClaimStartPositionData(
                (IWorldDataStore)services.Last(d => d.ServiceType == typeof(IWorldDataStore)).ImplementationInstance!, trip, throws: true)),
            alterRecordWhenClaimed: ClearHome);

    /// <summary>Race, gender and class unchanged across the claim and no bind point or start position for it: the login is refused (home.IsUnset in the same-keys branch) and gives the claim back.</summary>
    [Fact]
    public Task ALogin_GivesTheClaimBack_WhenTheBindPointIsUnsetAndHasNoStartPositionAfterTheClaim_WithTheRaceUnchanged()
        => AssertPostClaimFailureGivesTheClaimBackAsync("CLAIMWIRE17", "Nostart",
            (services, trip, _) => services.AddSingleton<IWorldDataStore>(new PostClaimStartPositionData(
                (IWorldDataStore)services.Last(d => d.ServiceType == typeof(IWorldDataStore)).ImplementationInstance!, trip, throws: false)),
            alterRecordWhenClaimed: ClearHome);

    /// <summary>
    /// A gateway-backed login waits for the previous owner's claim (CharacterHandlers.DuplicateLoginWait) and replaces its record with a newer read while it waits. The race and class
    /// data are compared against the keys they were read for, not against that newer record: a character whose race changed during the wait gets the data of its new race.
    /// </summary>
    [Fact]
    public async Task ALoginThatWaitsForTheClaim_ReadsTheRaceDataAgain_WhenTheRaceChangedDuringTheWait()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        var counter = new CountingWorldData();
        StaleReadStore? reads = null;
        await using WorldTestHost host = rig.StartWorld(a, configure: services =>
        {
            counter.Inner = (IWorldDataStore)services.Last(d => d.ServiceType == typeof(IWorldDataStore)).ImplementationInstance!;
            services.AddSingleton<IWorldDataStore>(counter);
            ICharacterStore inner = (ICharacterStore)services.Last(d => d.ServiceType == typeof(ICharacterStore)).ImplementationInstance!;
            reads = StaleReadStore.Wrap(inner);
            services.AddSingleton((ICharacterStore)(object)reads);
        });
        const string account = "CLAIMWIRE18";
        await host.AddAccountAsync(account);
        int accountId = (await host.Accounts.FindByUsernameAsync(account))!.Id;
        CharacterRecord record = await host.Characters.CreateAsync(new CharacterRecord
        {
            AccountId = accountId, Name = "Racechanger", Race = 1, Class = 1, Gender = 0, Level = 10,
            MapId = 0, ZoneId = 12, X = -8949.95f, Y = -132.493f, Z = 83.5312f,
            HomeMapId = 0, HomeZoneId = 12, HomeX = -8949.95f, HomeY = -132.493f, HomeZ = 83.5312f,
        });
        int id = record.Id;

        // The previous owner b holds the claim, so this login's first claim is refused and (a gateway session) it waits and reads the record again. The read that wait makes is the
        // third of the login (the load, the one after the drain, then this); right before it the character changes race and b releases, so that read returns the new race and the
        // next claim is admitted.
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync((ulong)id, b)).Outcome);
        int readCount = 0;
        bool fired = false;
        reads!.BeforeRead = async () =>
        {
            if (Interlocked.Increment(ref readCount) != 3)
            {
                return;
            }

            lock (record)
            {
                record.Race = 2;
            }

            Assert.True(await rig.Store.ReleaseCharacterClaimAsync((ulong)id, b));
            fired = true;
        };

        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        WorldSession session = WorldSession.CreateRemote(new ClaimWiringRemoteOutput(), "127.0.0.1:41005", scope.ServiceProvider, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions(), NullLogger.Instance);
        Assert.Equal(AuthResponseCode.Ok, await session.AuthenticateRemoteAsync(accountId, account, 5875, CancellationToken.None));
        session.StartRemote();
        Task run = Task.Run(() => session.RunRemoteAsync(CancellationToken.None));
        counter.Reset();
        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)id);
        session.DeliverRemote(WorldOpcode.CmsgPlayerLogin, login.ToArray());

        await WorldTestHost.WaitForAsync(() => host.World.IsOnline(ObjectGuid.Player((uint)id)), "the waiting login enters the world");
        Assert.True(fired, "the read made while the login waited never ran");
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync((ulong)id));
        Assert.Equal(2, counter.RaceReads); // once for the race read before the claim, once more because the race changed while the login waited
        session.CloseRemoteFromGateway("test over");
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class ClaimWiringRemoteOutput : IRemoteSessionOutput
    {
        public bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload) => true;
    }

    private sealed class PostClaimStartPositionData(IWorldDataStore inner, PostClaimTrip trip, bool throws) : IWorldDataStore
    {
        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => inner.GetClassInfoAsync(cls, cancellationToken);

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default) => inner.IsValidRaceClassAsync(race, cls, cancellationToken);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default) => inner.GetRaceInfoAsync(race, gender, cancellationToken);

        public async Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        {
            if (!await trip.ClaimedAsync())
            {
                return await inner.GetStartPositionAsync(race, cls, cancellationToken);
            }

            if (throws)
            {
                throw new InvalidOperationException("the start position lookup failed after the claim");
            }

            return null;
        }
    }

    /// <summary>The login reads race and class data once and not again after the claim while the character's race, gender and class are unchanged.</summary>
    [Fact]
    public async Task ALogin_DoesNotReadTheRaceDataAgainAfterTheClaim_WhenTheRaceIsUnchanged()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        var counter = new CountingWorldData();
        await using WorldTestHost host = rig.StartWorld(a, configure: services =>
        {
            counter.Inner = (IWorldDataStore)services.Last(d => d.ServiceType == typeof(IWorldDataStore)).ImplementationInstance!;
            services.AddSingleton<IWorldDataStore>(counter);
        });
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, "CLAIMWIRE15", "Cacher");
        counter.Reset();

        await client.LoginAsync((ulong)id);

        Assert.True(host.World.IsOnline(ObjectGuid.Player((uint)id)));
        Assert.Equal(a, await rig.Store.GetActiveOwnershipAsync((ulong)id)); // the claim was taken, so the post-claim branch ran
        Assert.Equal(1, counter.RaceReads);
        Assert.Equal(1, counter.ClassReads);
    }

    private sealed class CountingWorldData : IWorldDataStore
    {
        private int _raceReads;
        private int _classReads;

        public IWorldDataStore Inner { get; set; } = null!;

        public int RaceReads => Volatile.Read(ref _raceReads);

        public int ClassReads => Volatile.Read(ref _classReads);

        public void Reset()
        {
            Volatile.Write(ref _raceReads, 0);
            Volatile.Write(ref _classReads, 0);
        }

        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default) => Inner.GetStartPositionAsync(race, cls, cancellationToken);

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default) => Inner.IsValidRaceClassAsync(race, cls, cancellationToken);

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _classReads);
            return Inner.GetClassInfoAsync(cls, cancellationToken);
        }

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _raceReads);
            return Inner.GetRaceInfoAsync(race, gender, cancellationToken);
        }
    }

    [Fact]
    public Task ALogin_GivesTheClaimBack_WhenThePlayerBuildThrowsAfterTheClaim()
        => AssertPostClaimFailureGivesTheClaimBackAsync("CLAIMWIRE14", "Nobuttons", (_, trip, reads) => reads.BeforeActionButtons = () => trip.ThrowWhileClaimedAsync("the action buttons could not be read"));

    /// <summary>
    /// A login whose step after the claim fails, by a refusal or a throw. <c>faultAfterClaim</c> installs the fault through <see cref="PostClaimTrip"/>, which fires only while the claim row
    /// names this world, so the same checks made before the claim pass and the login really claims. The login is answered as failed, the fault saw the claim held, no player exists, and the
    /// claim is given back: a login on another worker can then claim the character.
    /// </summary>
    private static async Task AssertPostClaimFailureGivesTheClaimBackAsync(string account, string name, Action<IServiceCollection, PostClaimTrip, StaleReadStore> faultAfterClaim,
        Action<CharacterRecord>? alterRecordWhenClaimed = null)
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        var trip = new PostClaimTrip(rig.Store, a);
        await using WorldTestHost host = rig.StartWorld(a, configure: services =>
        {
            ICharacterStore inner = (ICharacterStore)services.Last(d => d.ServiceType == typeof(ICharacterStore)).ImplementationInstance!;
            StaleReadStore reads = StaleReadStore.Wrap(inner);
            services.AddSingleton((ICharacterStore)(object)reads);
            faultAfterClaim(services, trip, reads);
            if (alterRecordWhenClaimed is not null)
            {
                Func<Task>? prior = reads.BeforeRead;
                reads.BeforeRead = async () =>
                {
                    if (prior is not null)
                    {
                        await prior();
                    }

                    // The read that follows is the one made after the claim: the record it returns names another race.
                    if (await trip.IsClaimedAsync() && await inner.GetByIdAsync(trip.CharacterId) is { } live)
                    {
                        lock (live)
                        {
                            alterRecordWhenClaimed(live);
                        }
                    }
                };
            }
        });
        (WorldTestClient client, int id) = await ConnectWithCharacterAsync(host, account, name);
        trip.CharacterId = id;

        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)id);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        byte[] failed = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);

        Assert.Equal((byte)CharResult.CharLoginFailed, failed[0]);
        Assert.Equal(a, trip.HeldAtFault); // read inside the fault: this login held the claim when the step failed
        Assert.False(host.World.IsOnline(ObjectGuid.Player((uint)id)));
        await WorldTestHost.WaitForAsync(() => Task.Run(() => rig.Store.GetActiveOwnershipAsync((ulong)id)).GetAwaiter().GetResult() is null, "the claim is given back by the failed login");
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync((ulong)id, b)).Outcome); // another worker can claim now
    }

    /// <summary>
    /// Decides a fault from the claim row itself, not from counting calls: <see cref="ClaimedAsync"/> is true only while the row names <paramref name="login"/>, so a check made before the
    /// claim (the row is empty) passes and the first one after it fails. <see cref="HeldAtFault"/> keeps the owner the fault saw: a login that refused before claiming leaves it null.
    /// </summary>
    private sealed class PostClaimTrip(EfTransferDecisionStore store, CharacterOwnershipToken login)
    {
        private volatile CharacterOwnershipToken? _heldAtFault;

        public volatile int CharacterId;

        public CharacterOwnershipToken? HeldAtFault => _heldAtFault;

        /// <summary>True while the claim row names this login. Records nothing: used where the test only alters state (not a fault), so <see cref="HeldAtFault"/> stays proof from inside a fault.</summary>
        public async Task<bool> IsClaimedAsync()
            => CharacterId != 0 && await store.GetActiveOwnershipAsync((ulong)CharacterId) is { } owner && owner == login;

        /// <summary>The check a fault makes: like <see cref="IsClaimedAsync"/>, and it records the owner the fault saw.</summary>
        public async Task<bool> ClaimedAsync()
        {
            if (CharacterId == 0 || await store.GetActiveOwnershipAsync((ulong)CharacterId) is not { } owner || owner != login)
            {
                return false;
            }

            _heldAtFault = owner;
            return true;
        }

        public async Task ThrowWhileClaimedAsync(string message)
        {
            if (await ClaimedAsync())
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private sealed class PostClaimRenameStore(PostClaimTrip trip) : ICharacterRenameStore
    {
        public Task<bool> SetFlagAsync(int characterId, uint flag, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CharacterRenameResult> RenameAsync(int characterId, int accountId, string newName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<IReadOnlyDictionary<int, uint>> GetFlagsAsync(int accountId, CancellationToken cancellationToken = default)
            => await trip.ClaimedAsync() ? new Dictionary<int, uint> { [trip.CharacterId] = CharacterAtLoginFlags.Rename } : new Dictionary<int, uint>();
    }

    private sealed class PostClaimWorldData(IWorldDataStore inner, PostClaimTrip trip, bool throws) : IWorldDataStore
    {
        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default) => inner.GetStartPositionAsync(race, cls, cancellationToken);

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => inner.GetClassInfoAsync(cls, cancellationToken);

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default) => inner.IsValidRaceClassAsync(race, cls, cancellationToken);

        public async Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        {
            if (!await trip.ClaimedAsync())
            {
                return await inner.GetRaceInfoAsync(race, gender, cancellationToken);
            }

            if (throws)
            {
                throw new InvalidOperationException("world data failed after the claim");
            }

            return null;
        }
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
        await Assert.ThrowsAsync<CharacterOwnershipRejectedException>(() => arrival.CompleteAsync(bundle, stale, 0, CancellationToken.None));
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

    /// <summary>A settle poll that keeps failing backs off: with a 10 ms poll, a second of a broken store makes a handful of attempts, not the hundred a fixed interval would.</summary>
    [Fact]
    public async Task AReceivedClaimsReleasePoll_BacksOff_WhileTheStoreKeepsFailing()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Backoff");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        await rig.CommitClaimMoveAsync(character, a, b);
        var scopes = new FailingScopeFactory(rig.Services.GetRequiredService<IServiceScopeFactory>());
        var flaky = new EfTransferDecisionStore(scopes);
        using var claims = new CharacterClaimService(new StoreOverrideProvider(rig.Services, flaky)) { TransferSettlePoll = TimeSpan.FromMilliseconds(10) };

        scopes.FailNext(int.MaxValue / 2);
        claims.ScheduleReleaseOfReceivedClaim(character, b, () => false);
        await WorldTestHost.WaitForAsync(() => scopes.Failures >= 3, "the poll to fail and retry");
        int atStart = scopes.Failures;
        await Task.Delay(1000);

        Assert.True(scopes.Failures - atStart < 20, $"{scopes.Failures - atStart} failed polls in one second: the interval did not grow");
    }

    /// <summary>A cancellation that is not the service's stop (a store timeout surfaces as one) is a failure: it is recorded and the pending marker is forgotten, not swallowed as a shutdown.</summary>
    [Fact]
    public async Task AReleaseThatEndsInAStoreCancellation_ThatIsNotTheStop_IsRecordedAsAFault()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Timeout");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        var scopes = new TimingOutScopeFactory(rig.Services.GetRequiredService<IServiceScopeFactory>());
        var timingOut = new EfTransferDecisionStore(scopes);
        using var claims = new CharacterClaimService(new StoreOverrideProvider(rig.Services, timingOut)) { ReleaseRetryDelay = TimeSpan.FromMilliseconds(10), ReleaseAttempts = 3 };
        long before = ReleaseFaults();

        scopes.Arm(int.MaxValue / 2); // every attempt is cancelled
        claims.ScheduleRelease(character, a, () => false);
        await claims.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, scopes.Timeouts); // retried up to ReleaseAttempts, then gave up
        Assert.Equal(before + 1, ReleaseFaults());
        Assert.Equal(a.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope); // nothing could be released
        Assert.Equal(0, PendingReleaseMarkers(claims)); // the marker is not left behind
    }

    /// <summary>
    /// A cancellation of the release that is not the service's stop does not strand the claim: the release is retried, and once the store answers the claim is gone, so other workers' logins
    /// of the character are not refused. Nothing is recorded as a fault.
    /// </summary>
    [Fact]
    public async Task AReleaseThatEndsInAStoreCancellation_ThatIsNotTheStop_IsRetried_AndTheClaimIsReleased()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("TimeoutRetry");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        var scopes = new TimingOutScopeFactory(rig.Services.GetRequiredService<IServiceScopeFactory>());
        var timingOut = new EfTransferDecisionStore(scopes);
        using var claims = new CharacterClaimService(new StoreOverrideProvider(rig.Services, timingOut)) { ReleaseRetryDelay = TimeSpan.FromMilliseconds(10), ReleaseAttempts = 3 };
        long before = ReleaseFaults();

        scopes.Arm(1); // the first attempt is cancelled, the second goes through
        claims.ScheduleRelease(character, a, () => false);
        await claims.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, scopes.Timeouts);
        Assert.Null(await rig.Store.GetActiveOwnershipAsync((ulong)character)); // released by the retry
        Assert.Equal(before, ReleaseFaults());
        Assert.Equal(0, PendingReleaseMarkers(claims));
    }

    /// <summary>
    /// A settle poll that is cancelled without the service's stop (a store timeout) is a failed poll: the received claim's release keeps polling, and releases once the transfer completed.
    /// Ending the wait on the cancellation (the earlier filter) would strand the claim.
    /// </summary>
    [Fact]
    public async Task AReceivedClaimsReleasePoll_KeepsPolling_WhenAPollIsCancelledWithoutTheStop()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("PollTimeout");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        Guid transfer = await rig.CommitClaimMoveAsync(character, a, b);
        var scopes = new TimingOutScopeFactory(rig.Services.GetRequiredService<IServiceScopeFactory>());
        var timingOut = new EfTransferDecisionStore(scopes);
        using var claims = new CharacterClaimService(new StoreOverrideProvider(rig.Services, timingOut)) { TransferSettlePoll = TimeSpan.FromMilliseconds(20) };
        long before = ReleaseFaults();

        scopes.Arm(2); // the first two polls are cancelled
        claims.ScheduleReleaseOfReceivedClaim(character, b, () => false);
        await WorldTestHost.WaitForAsync(() => scopes.Timeouts >= 2, "the first two polls to be cancelled");
        await Task.Delay(300); // later polls answer: the release did not end on the cancellations
        Assert.False(claims.DrainAsync().IsCompleted, "the release ended on a cancelled poll");
        Assert.Equal(b.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope); // the transfer is not completed: nothing deleted

        await rig.Store.MarkCompletedAsync(transfer);
        await WorldTestHost.WaitForAsync(() => Task.Run(() => rig.Store.GetActiveOwnershipAsync((ulong)character)).GetAwaiter().GetResult() is null, "the received claim is released after the cancelled polls");
        await claims.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(before, ReleaseFaults());
    }

    /// <summary>
    /// A flush of the character's queues that fails (here a settlement barrier cancelled without the stop; the barrier reports every step failure as a not-durable result) ends the release after
    /// its attempts without a fault, the claim stays for the next login to refresh, and the pending marker is forgotten.
    /// </summary>
    [Fact]
    public async Task AReleaseWhoseFlushStepIsCancelled_GivesUp_ForgetsTheMarker_AndKeepsTheClaim()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("FlushCancelled");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        var barrier = new CharacterPersistenceBarrier(new SettlementBarrierProvider(new CancelledSettlementBarrier()));
        using var claims = new CharacterClaimService(new BarrierOverrideProvider(rig.Services, barrier)) { ReleaseRetryDelay = TimeSpan.FromMilliseconds(10), ReleaseAttempts = 2 };
        long before = ReleaseFaults();

        claims.ScheduleRelease(character, a, () => false);
        await claims.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(a.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope); // not released
        Assert.Equal(0, PendingReleaseMarkers(claims)); // the marker was forgotten, not left registered
        Assert.Equal(before, ReleaseFaults());
    }

    private static int PendingReleaseMarkers(CharacterClaimService claims)
    {
        FieldInfo field = typeof(CharacterClaimService).GetField("_pendingReleases", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.IDictionary)field.GetValue(claims)!).Count;
    }

    private sealed class CancelledSettlementBarrier : ArcaneCore.World.Characters.ICharacterSettlementBarrier
    {
        public Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromException(new StoreTimedOut());
    }

    /// <summary>A provider that holds only the given settlement barrier (the barrier under test resolves nothing else).</summary>
    private sealed class SettlementBarrierProvider(ArcaneCore.World.Characters.ICharacterSettlementBarrier barrier) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<ArcaneCore.World.Characters.ICharacterSettlementBarrier>))
                return new[] { barrier };
            // GetServices<T> requires the sequence to resolve: every other sequence is empty.
            return serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? Array.CreateInstance(serviceType.GetGenericArguments()[0], 0) : null;
        }
    }

    /// <summary>The rig's provider, except that <see cref="CharacterPersistenceBarrier"/> resolves to the given instance.</summary>
    private sealed class BarrierOverrideProvider(IServiceProvider inner, CharacterPersistenceBarrier barrier) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(CharacterPersistenceBarrier) ? barrier : inner.GetService(serviceType);
    }

    private static long ReleaseFaults() => ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Snapshot()
        .Where(f => f.Site == "persist.character-claim-release" && f.ExceptionType.Contains(nameof(StoreTimedOut), StringComparison.Ordinal))
        .Sum(f => f.Count);

    private sealed class StoreTimedOut : OperationCanceledException;

    /// <summary>A scope factory whose next <see cref="Arm"/> scope creations throw <see cref="StoreTimedOut"/>, then delegates.</summary>
    private sealed class TimingOutScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _timeouts;
        private int _remaining;

        public int Timeouts => Volatile.Read(ref _timeouts);

        public void Arm(int count) => Volatile.Write(ref _remaining, count);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Decrement(ref _remaining) >= 0)
            {
                Interlocked.Increment(ref _timeouts);
                throw new StoreTimedOut();
            }

            Interlocked.Exchange(ref _remaining, 0);
            return inner.CreateScope();
        }
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

    /// <summary>
    /// The offline arrival through the real target: the worker's token holds a committed transfer's claim, the client's session is not here, so the activation writes the position and
    /// schedules the release of the received claim. The claim stays while the transfer is not completed and is released once it is, so a login of the character on another worker
    /// claims it instead of being refused as a live owner's. (Without the target's scheduling, nothing would ever release it: no logout runs for a character nobody plays.)
    /// </summary>
    [Fact]
    public async Task TheTargetsOfflineArrival_ReleasesTheReceivedClaim_OnceTheTransferIsCompleted()
    {
        using var directory = new TempDirectory("arcanecore-claimwire-", sqlite: true);
        await using Rig rig = await Rig.CreateAsync(directory);
        int character = await rig.CreateCharacterAsync("Stranded");
        CharacterOwnershipToken a = await rig.AcquireWorkerAsync("a", 1);
        CharacterOwnershipToken b = await rig.AcquireWorkerAsync("b", 30);
        await rig.Store.TryClaimCharacterAsync((ulong)character, a);
        Guid transfer = await rig.CommitClaimMoveAsync(character, a, b); // committed to b, not yet completed
        await using WorldTestHost host = rig.StartWorld(b);
        rig.Holder.Current = b;
        CharacterClaimService claims = host.WorldServices.GetRequiredService<CharacterClaimService>();
        var target = new WorkerTransferTarget(
            NodeB, host.World, new NoShadowLoader(), new NoSessionEntry(), sessionReady: _ => false,
            offlineArrival: new CharacterStoreOfflineArrival(rig.Services.GetRequiredService<IServiceScopeFactory>()), claims: claims)
        {
            SessionOpenGrace = TimeSpan.FromMilliseconds(50),
        };
        PlayerTransferBundle bundle = PlayerTransferValidatorTests.CreateSampleValidBundle(transfer, ObjectGuid.Player((uint)character).Value, NodeA, NodeB) with
        {
            CharacterId = character,
            AccountId = 1,
            NotLeaveTransport = false,
        };

        await target.ActivateTargetPlayerAsync(transfer, bundle, b).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(host.World.IsOnline(ObjectGuid.Player((uint)character)));
        Assert.Equal(bundle.TargetMapId, (await rig.CharacterAsync(character))!.MapId); // the arrival was written
        await Task.Delay(600); // several polls: the transfer is still in flight, so nothing is deleted
        Assert.Equal(b.Scope, (await rig.Store.GetActiveOwnershipAsync((ulong)character))?.Scope);
        Assert.Equal(CharacterClaimOutcome.RefusedTransferInFlight, (await rig.Store.TryClaimCharacterAsync((ulong)character, a)).Outcome);

        await rig.Store.MarkCompletedAsync(transfer);
        await WorldTestHost.WaitForAsync(() => Task.Run(() => rig.Store.GetActiveOwnershipAsync((ulong)character)).GetAwaiter().GetResult() is null, "the received claim to be released");
        await claims.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CharacterClaimOutcome.Claimed, (await rig.Store.TryClaimCharacterAsync((ulong)character, a)).Outcome);
    }

    /// <summary>A shadow loader for a target that must never load one (the offline path builds no shadow).</summary>
    private sealed class NoShadowLoader : ITransferShadowLoader
    {
        public Task<ArcaneCore.Game.Entities.Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the offline arrival loads no shadow");

        public void Release(ArcaneCore.Game.Entities.Player shadow)
        {
        }
    }

    /// <summary>A session entry for a target that must never attach a player (the offline path has none).</summary>
    private sealed class NoSessionEntry : ITransferSessionEntry
    {
        public bool TryAttach(ArcaneCore.Game.Entities.Player player) => throw new InvalidOperationException("the offline arrival attaches no player");

        public void ResumeSaves(ArcaneCore.Game.Entities.Player player) => throw new InvalidOperationException("the offline arrival resumes no saves");
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

        /// <summary>Runs before every read of a character; what it throws is the read's failure.</summary>
        public Func<Task>? BeforeRead { get; set; }

        /// <summary>Runs before every read of a character's action buttons; what it throws is the read's failure.</summary>
        public Func<Task>? BeforeActionButtons { get; set; }

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
            if (targetMethod.Name == nameof(ICharacterStore.GetByIdAsync) && args is [int read, CancellationToken readToken] && BeforeRead is { } beforeRead)
            {
                return ReadAfterAsync(beforeRead, read, readToken);
            }

            if (targetMethod.Name == nameof(ICharacterStore.GetActionButtonsAsync) && args is [int owner, CancellationToken buttonsToken] && BeforeActionButtons is { } beforeButtons)
            {
                return ActionButtonsAfterAsync(beforeButtons, owner, buttonsToken);
            }

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

        private async Task<CharacterRecord?> ReadAfterAsync(Func<Task> before, int id, CancellationToken ct)
        {
            await before();

            // A copy, like the armed read: the in-memory store hands out its live record, and a login that keeps the record it read before the claim must not see a later change to it.
            CharacterRecord? record = await _inner.GetByIdAsync(id, ct);
            return record is null ? null : Copy(record);
        }

        private async Task<IReadOnlyList<ActionButton>> ActionButtonsAfterAsync(Func<Task> before, int id, CancellationToken ct)
        {
            await before();
            return await _inner.GetActionButtonsAsync(id, ct);
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
