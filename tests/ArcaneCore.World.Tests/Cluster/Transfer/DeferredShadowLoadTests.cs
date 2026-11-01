using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
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
    private static (WorkerTransferTarget Target, CountingLoader Loader) NewTarget(Rig rig, bool deferred = true, int failFirstLoads = 0, bool offline = false)
    {
        Func<PlayerTransferBundle, WorldSession?> sessions = AdoptedSession(rig.Acceptor);
        var loader = new CountingLoader(new CharacterHooksShadowLoader(sessions), failFirstLoads);
        var target = new WorkerTransferTarget(
            Target, rig.Host.World, loader, new WorldSessionTransferEntry(), sessionReady: deferred ? bundle => sessions(bundle) is not null : null,
            offlineArrival: offline ? new CharacterStoreOfflineArrival(rig.Host.WorldServices.GetRequiredService<IServiceScopeFactory>()) : null);
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

        // After the commit, without the session: install (a no-op) and activate both return.
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(Budget);
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

        // A repeat (recovery hears of the activation late) writes nothing more and does not throw.
        int saves = rig.Host.Characters.SaveCount;
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(Budget);
        Assert.Equal(saves, rig.Host.Characters.SaveCount);
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

    private sealed class CountingLoader(ITransferShadowLoader inner, int failFirst) : ITransferShadowLoader
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
