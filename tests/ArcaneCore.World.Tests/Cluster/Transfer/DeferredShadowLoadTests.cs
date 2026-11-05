using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// Design _wf/handoff-control-DESIGN.md section 4.4 (commit C9): the coordinator installs the passive shadow (stage 3) before the gateway opens the
/// client's session on the target (stage 4), and the production shadow loader needs that session. The target here is the production
/// <see cref="WorkerTransferTarget"/> with the production <see cref="CharacterHooksShadowLoader"/> over a full test host, and the session is opened through the
/// real <see cref="WorldGatewaySessionAcceptor"/> exactly as the gateway link host does it. Before C9 the stage 3 install failed with "no adopted session"
/// (see <see cref="WithoutTheDeferral_TheStageThreeInstallFailsBecauseTheSessionIsNotOpenYet"/>); with it the transfer completes.
/// </summary>
[Collection(SharedFaultLedgerCollection.Name)] // reads the process-wide fault ledger before and after: nothing may reset it meanwhile
public sealed class DeferredShadowLoadTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private static readonly WorkerNodeId Source = new("worker-a");
    private static readonly WorkerNodeId Target = new("worker-b");
    private static readonly CharacterOwnershipToken TargetToken = new("world", 4, Guid.NewGuid());
    private static long _generation = 100;

    private sealed class Rig(WorldTestHost host, WorldGatewaySessionAcceptor acceptor) : IAsyncDisposable
    {
        public WorldTestHost Host { get; } = host;

        public WorldGatewaySessionAcceptor Acceptor { get; } = acceptor;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Acceptor.StopAsync().WaitAsync(Budget);
            }
            finally
            {
                await Host.DisposeAsync();
            }
        }
    }

    private static Rig NewRig()
    {
        WorldTestHost host = WorldTestHost.Start();
        var acceptor = new WorldGatewaySessionAcceptor(
            host.WorldServices.GetRequiredService<IServiceScopeFactory>(), host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLoggerFactory.Instance);
        return new Rig(host, acceptor);
    }

    private static async Task<(int AccountId, string Account, CharacterRecord Character)> NewCharacterAsync(WorldTestHost host, string account, string name)
    {
        await host.AddAccountAsync(account);
        int accountId = (await host.Accounts.FindByUsernameAsync(account))!.Id;
        CharacterRecord record = await host.Characters.CreateAsync(new CharacterRecord
        {
            AccountId = accountId,
            Name = name,
            Race = 1,
            Class = 1,
            Gender = 0,
            Level = 40,
            Money = 4321,
            MapId = 0,
            ZoneId = 12,
            X = 100f,
            Y = 2f,
            Z = 3f,
            Orientation = 0f,
            HomeMapId = 0,
            HomeZoneId = 12,
            HomeX = -8949.95f,
            HomeY = -132.493f,
            HomeZ = 83.5312f,
        });
        return (accountId, account, record);
    }

    private static PlayerTransferBundle NewBundle(Guid id, int accountId, CharacterRecord character) => new()
    {
        TransferId = id,
        CharacterGuid = ObjectGuid.Player((uint)character.Id).Value,
        CharacterId = character.Id,
        AccountId = accountId,
        SessionGeneration = 1,
        SourceWorkerId = Source,
        TargetWorkerId = Target,
        SourceOwnershipToken = new CharacterOwnershipToken("world", 3, Guid.NewGuid()),
        TargetMapId = 0,
        TargetInstanceId = 0,
        TargetPosition = new TransferPosition(-8949f, -132f, 83f, 0f),
        Health = 100,
        Powers = [0, 0, 0, 0, 0],
        Level = 40,
    };

    /// <summary>The production lookup of <c>WorkerTransferHosting</c>: the open gateway session of the bundle's account on this worker.</summary>
    private static Func<PlayerTransferBundle, WorldSession?> AdoptedSession(WorldGatewaySessionAcceptor acceptor) => bundle =>
    {
        foreach (KeyValuePair<long, RemoteWorldSession> open in acceptor.Sessions)
        {
            if (open.Value.Session.AccountId == bundle.AccountId)
            {
                return open.Value.Session;
            }
        }

        return null;
    };

    /// <summary>The target as <c>WorkerTransferHosting</c> builds it: the production loader and entry, the session probe, and the acceptor's session-opened wiring.</summary>
    private static (WorkerTransferTarget Target, CountingLoader Loader) NewTarget(
        Rig rig, bool deferred = true, int failFirstLoads = 0, bool offline = false, TimeSpan? sessionOpenGrace = null, Func<Exception?>? scripted = null,
        ILogger<WorkerTransferTarget>? logger = null)
    {
        Func<PlayerTransferBundle, WorldSession?> sessions = AdoptedSession(rig.Acceptor);
        var loader = new CountingLoader(new CharacterHooksShadowLoader(sessions, rig.Host.WorldServices), failFirstLoads, scripted);
        var target = new WorkerTransferTarget(
            Target, rig.Host.World, loader, new WorldSessionTransferEntry(rig.Host.WorldServices), logger: logger, sessionReady: deferred ? bundle => sessions(bundle) is not null : null,
            offlineArrival: offline ? new CharacterStoreOfflineArrival(rig.Host.WorldServices.GetRequiredService<IServiceScopeFactory>()) : null)
        {
            SessionOpenGrace = sessionOpenGrace ?? TimeSpan.FromMilliseconds(200),
        };
        rig.Acceptor.SessionOpened += opened => target.OnSessionOpened(opened.AccountId);
        return (target, loader);
    }

    /// <summary>What the gateway's stage 4 does on the target: opens the account's session through the acceptor and starts it.</summary>
    private static async Task OpenSessionAsync(Rig rig, int accountId, string account)
    {
        GatewaySessionAcceptResult result = await rig.Acceptor.OpenAsync(
            new GatewaySessionOpenRequest(Interlocked.Increment(ref _generation), accountId, account, 5875, "127.0.0.1:41005"), new GatewayOutput(), CancellationToken.None)
            .AsTask().WaitAsync(Budget);
        Assert.Equal(AuthResponseCode.Ok, result.Code);
        result.Session!.Start();
    }

    [Fact]
    public async Task WithoutTheDeferral_TheStageThreeInstallFailsBecauseTheSessionIsNotOpenYet()
    {
        await using Rig rig = NewRig();
        (int accountId, _, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED0", "Deferzero");
        (WorkerTransferTarget target, _) = NewTarget(rig, deferred: false);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget));

        Assert.Contains("no adopted session", error.Message);
        Assert.False(target.HasShadow(id));
    }

    [Fact]
    public async Task StageThreeInstall_ReturnsAtOnce_TheLoadStartsWhenStageFourOpensTheSession_AndTheTransferCompletes()
    {
        await using Rig rig = NewRig();
        (int accountId, string account, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED1", "Deferone");
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        ObjectGuid guid = new(bundle.CharacterGuid);

        // Stage 1 and stage 3 (the client's session is not open here yet): the install answers and loads nothing.
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);
        Assert.Equal(0, loader.Calls);
        Assert.False(target.HasShadow(id));
        Assert.Equal(1, target.ReservedCount);

        // Stage 4: the gateway opens the session; the target starts the load by itself.
        await OpenSessionAsync(rig, accountId, account);
        await WorldTestHost.WaitForAsync(() => target.HasShadow(id), "the deferred shadow load to finish");
        Assert.Equal(1, loader.Calls);
        Assert.Equal(0, rig.Host.Characters.SaveCount);
        Assert.False(rig.Host.World.IsOnline(guid));

        // After the commit: install again (joins the finished load), then activate.
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);
        Assert.Equal(1, loader.Calls);
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(Budget);

        Assert.True(target.WasActivated(id));
        Assert.Equal(1, loader.Calls);
        Assert.NotNull(await rig.Host.World.InvokeAsync(() => rig.Host.World.FindOnlinePlayer(guid)));
    }

    [Fact]
    public async Task AnInstallAfterTheSessionIsOpen_LoadsAtOnce()
    {
        await using Rig rig = NewRig();
        (int accountId, string account, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED2", "Defertwo");
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await OpenSessionAsync(rig, accountId, account);

        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);

        Assert.True(target.HasShadow(id));
        Assert.Equal(1, loader.Calls);
    }

    [Fact]
    public async Task ASessionOfAnotherAccount_DoesNotStartTheLoad()
    {
        await using Rig rig = NewRig();
        (int accountId, _, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED3", "Deferthree");
        (int otherId, string other, _) = await NewCharacterAsync(rig.Host, "DEFERRED3B", "Deferthreeb");
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);

        await OpenSessionAsync(rig, otherId, other);
        await Task.Delay(200);

        Assert.Equal(0, loader.Calls);
        Assert.False(target.HasShadow(id));
    }

    private static long TargetFaults() => FaultLedger.Shared.Snapshot().Where(f => f.Site == "cluster.transfer-target").Sum(f => f.Count);

    [Fact]
    public async Task ADeferredLoadThatFails_IsRepeatedByTheInstallAfterTheCommit()
    {
        await using Rig rig = NewRig();
        (int accountId, string account, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED4", "Deferfour");
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig, failFirstLoads: 1);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);

        long faultsBefore = TargetFaults();
        await OpenSessionAsync(rig, accountId, account);
        await WorldTestHost.WaitForAsync(() => loader.Calls >= 1, "the deferred load to run");
        // The target records the fault after it cleared the failed load, so the fault count is the signal that the failure is forgotten (no shadow and one reservation are true while the load is still running).
        await WorldTestHost.WaitForAsync(() => TargetFaults() > faultsBefore && !target.HasShadow(id) && target.ReservedCount == 1, "the failed load to be forgotten");

        // The second install (after the commit) loads again and now succeeds; the reservation stayed.
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);

        Assert.True(target.HasShadow(id));
        Assert.Equal(2, loader.Calls);
    }

    private static long TargetFaultsOf(string exceptionType) => FaultLedger.Shared.Snapshot()
        .Where(f => f.Site == "cluster.transfer-target" && f.ExceptionType.Contains(exceptionType, StringComparison.Ordinal))
        .Sum(f => f.Count);

    private sealed class DeferredLoadBroke : InvalidOperationException;

    /// <summary>
    /// A deferred shadow load (started by the session opening, observed by the continuation of OnSessionOpened) that ends because the client closed is the client leaving: it is logged at
    /// information level and adds nothing to the fault ledger. The control in the same test is a deferred load that fails otherwise: it is counted, and warned about. The log line of either
    /// branch is the sync point (the fault is recorded before the warning, the information line is written instead of it), so the counts are read after the continuation ran, not by timing.
    /// </summary>
    [Fact]
    public async Task ADeferredLoadThatFoundTheClientClosed_IsNotCountedAsAFault_WhileAnyOtherDeferredFailureIs()
    {
        await using Rig rig = NewRig();
        (int accountId, string account, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED9", "Defernine");
        var logs = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([logs]);
        Func<Exception> next = () => new TransferClientClosedException("the client closed", new ObjectDisposedException("scope"));
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig, scripted: () => next(), logger: factory.CreateLogger<WorkerTransferTarget>());
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);
        long closedBefore = TargetFaultsOf(nameof(TransferClientClosedException));

        await OpenSessionAsync(rig, accountId, account);
        await WorldTestHost.WaitForAsync(() => Deferred(logs).Length >= 1, "the deferred load to be observed");

        Assert.Equal(1, loader.Calls);
        Assert.DoesNotContain(Deferred(logs), e => e.Level >= LogLevel.Warning); // the closed client is not warned about
        Assert.Equal(closedBefore, TargetFaultsOf(nameof(TransferClientClosedException)));
        Assert.False(target.HasShadow(id));
        Assert.Equal(1, target.ReservedCount); // the failed load was forgotten: the install after the commit repeats it

        // Control: a second transfer (own account, own deferred install) whose session opens with a script that fails otherwise. The same continuation (ObserveDeferredLoad) must record it
        // and warn, so the zero above can fail. A re-run of OnSessionOpened for the first account would start nothing: its install request was consumed by the first load.
        (int otherId, string otherAccount, CharacterRecord otherCharacter) = await NewCharacterAsync(rig.Host, "DEFERRED9B", "Defernineb");
        Guid otherTransfer = Guid.NewGuid();
        PlayerTransferBundle otherBundle = NewBundle(otherTransfer, otherId, otherCharacter);
        Assert.True(await target.TryReserveSlotAsync(otherTransfer, otherBundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(otherTransfer, otherBundle).WaitAsync(Budget);
        Assert.Equal(1, loader.Calls);
        next = () => new DeferredLoadBroke();
        long brokeBefore = TargetFaultsOf(nameof(DeferredLoadBroke));
        await OpenSessionAsync(rig, otherId, otherAccount);
        await WorldTestHost.WaitForAsync(() => Deferred(logs).Any(e => e.Level == LogLevel.Warning), "the failed deferred load to be warned about");

        Assert.Equal(2, loader.Calls);
        Assert.Equal(brokeBefore + 1, TargetFaultsOf(nameof(DeferredLoadBroke)));
        Assert.Equal(closedBefore, TargetFaultsOf(nameof(TransferClientClosedException)));
    }

    private static CapturedCategoryLog[] Deferred(CapturingLoggerProvider logs) => [.. logs.Entries.Where(e => e.Message.Contains("deferred shadow load", StringComparison.Ordinal))];

    /// <summary>
    /// The production loader on a session whose client left (the session closed and its scope was disposed): the disposed scope surfaces as <see cref="TransferClientClosedException"/> with the
    /// disposed-scope error inside, not as a bare ObjectDisposedException that the coordinator would count as a fault.
    /// </summary>
    [Fact]
    public async Task TheProductionLoader_OnASessionWhoseClientLeft_ThrowsTheClientClosedException()
    {
        await using Rig rig = NewRig();
        (int accountId, string account, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED10", "Deferten");
        await OpenSessionAsync(rig, accountId, account);
        RemoteWorldSession remote = rig.Acceptor.Sessions.Values.Single();
        WorldSession session = remote.Session;
        Assert.False(session.IsDisconnected);

        remote.CloseFromGateway(ClusterSessionDetachReason.ClientDisconnect);
        await WorldTestHost.WaitForAsync(() => rig.Acceptor.Sessions.IsEmpty, "the session task to end (it disposes the session scope last)");
        Assert.True(session.IsDisconnected);

        var loader = new CharacterHooksShadowLoader(_ => session, rig.Host.WorldServices);
        PlayerTransferBundle bundle = NewBundle(Guid.NewGuid(), accountId, character);
        TransferClientClosedException closed = await Assert.ThrowsAsync<TransferClientClosedException>(() => loader.LoadAsync(bundle, CancellationToken.None).WaitAsync(Budget));

        Assert.IsType<ObjectDisposedException>(closed.InnerException);
    }

    [Fact]
    public async Task ADiscardedDeferredInstall_NeverLoads_WhenTheSessionOpensLater()
    {
        await using Rig rig = NewRig();
        (int accountId, string account, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED5", "Deferfive");
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);

        await target.DiscardPassiveShadowAsync(id, bundle.CharacterGuid, "test abort").WaitAsync(Budget);
        await OpenSessionAsync(rig, accountId, account);
        await Task.Delay(200);

        Assert.Equal(0, loader.Calls);
        Assert.Equal(0, target.ReservedCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget));
    }

    /// <summary>
    /// Design section 6 ("ActivateTargetPlayerAsync with no client must return without throwing"): the client's session never opened here (or closed) and the transfer is
    /// committed. Before the offline arrival the activation failed with "no adopted session" on every recovery pass, forever; now the character arrives offline at the target.
    /// </summary>
    [Fact]
    public async Task ACommittedTransferWhoseClientIsGone_ArrivesOffline_AtTheTargetPosition()
    {
        await using Rig rig = NewRig();
        (int accountId, _, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED6", "Defersix");
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig, offline: true);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        ObjectGuid guid = new(bundle.CharacterGuid);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);

        // The worker writes as TargetToken. A commit that names another incarnation of its scope (a token this process does not hold) writes nothing and fails the activation.
        rig.Host.World.CharacterOwnership = TargetToken;
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);
        var otherIncarnation = new CharacterOwnershipToken(TargetToken.Scope, TargetToken.Epoch, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.ActivateTargetPlayerAsync(id, bundle, otherIncarnation).WaitAsync(Budget));
        Assert.Equal(0, rig.Host.Characters.SaveCount);
        Assert.False(target.WasActivated(id));
        Assert.Equal(100f, (await rig.Host.Characters.GetByIdAsync(character.Id))!.X);

        // After the commit, without the session: install (a no-op) and activate both return.
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(Budget);

        Assert.True(target.WasActivated(id));
        Assert.Equal(0, target.ReservedCount);
        Assert.Equal(0, loader.Calls);
        Assert.False(rig.Host.World.IsOnline(guid));
        CharacterRecord stored = (await rig.Host.Characters.GetByIdAsync(character.Id))!;
        Assert.Equal(bundle.TargetMapId, stored.MapId);
        Assert.Equal(bundle.TargetPosition.X, stored.X);
        Assert.Equal(bundle.TargetPosition.Y, stored.Y);
        Assert.Equal(bundle.TargetPosition.Z, stored.Z);
        Assert.Equal(character.Level, stored.Level);
        Assert.Equal(12u, stored.ZoneId); // no terrain here: the zone is unknown, so the stored one is kept (a login recomputes it)

        // Only the position moved: the money is the stored money, and no inventory, action bar, bind point or life was written (null leaves the stored rows alone).
        Assert.Equal(4321u, stored.Money);
        CharacterState written = Assert.IsType<CharacterState>(rig.Host.Characters.LastState);
        Assert.Equal(4321u, written.Money);
        Assert.Null(written.Inventory);
        Assert.Null(written.ActionButtons);
        Assert.Null(written.Home);
        Assert.Null(written.Life);
        Assert.Equal(TargetToken, written.Ownership);

        // A repeat (recovery hears of the activation late) writes nothing more and does not throw.
        int saves = rig.Host.Characters.SaveCount;
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(Budget);
        Assert.Equal(saves, rig.Host.Characters.SaveCount);
    }

    /// <summary>
    /// The worker's open reply reaches the gateway before the accepted session is started and entered in the acceptor, so the activation that follows the gateway's ack can
    /// arrive while the session is not adopted yet. That is "not yet", not "gone": the activation waits for the session and activates the player online, it does not take the
    /// character offline under a client that is about to play it.
    /// </summary>
    [Fact]
    public async Task AnActivationThatArrivesBeforeTheSessionIsStarted_WaitsForIt_AndActivatesOnline()
    {
        await using Rig rig = NewRig();
        (int accountId, string account, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED8", "Defereight");
        (WorkerTransferTarget target, CountingLoader loader) = NewTarget(rig, offline: true, sessionOpenGrace: TimeSpan.FromSeconds(20));
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        ObjectGuid guid = new(bundle.CharacterGuid);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);

        Task activation = target.ActivateTargetPlayerAsync(id, bundle, TargetToken);
        await Task.Delay(300);
        Assert.False(activation.IsCompleted, "the activation must wait for the session, not take the character offline");
        Assert.Equal(0, rig.Host.Characters.SaveCount);

        await OpenSessionAsync(rig, accountId, account);
        await activation.WaitAsync(Budget);

        Assert.True(target.WasActivated(id));
        Assert.Equal(1, loader.Calls);
        Assert.NotNull(await rig.Host.World.InvokeAsync(() => rig.Host.World.FindOnlinePlayer(guid)));
        Assert.Equal(0, rig.Host.Characters.SaveCount); // nothing was written as an offline arrival
    }

    [Fact]
    public async Task WithoutTheOfflineArrival_AnActivationWithoutTheClient_StillFails()
    {
        await using Rig rig = NewRig();
        (int accountId, _, CharacterRecord character) = await NewCharacterAsync(rig.Host, "DEFERRED7", "Deferseven");
        (WorkerTransferTarget target, _) = NewTarget(rig, offline: false);
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(Budget));

        Assert.Contains("no adopted session", error.Message);
        Assert.False(target.WasActivated(id));
        Assert.Equal(1, target.ReservedCount);
    }

    private sealed class CountingLoader(ITransferShadowLoader inner, int failFirst, Func<Exception?>? scripted = null) : ITransferShadowLoader
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call <= failFirst)
            {
                throw new InvalidOperationException("injected load failure " + call);
            }

            if (scripted?.Invoke() is { } scriptedFailure)
            {
                throw scriptedFailure;
            }

            return await inner.LoadAsync(bundle, cancellationToken).ConfigureAwait(false);
        }

        public void Release(Player shadow) => inner.Release(shadow);
    }

    private sealed class GatewayOutput : IGatewaySessionOutput
    {
        public bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload) => true;

        public void Close(ClusterSessionDetachReason reason)
        {
        }
    }
}
