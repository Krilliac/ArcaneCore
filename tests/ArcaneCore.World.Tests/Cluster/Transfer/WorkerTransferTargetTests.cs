using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// The production target worker of a transfer over a real <see cref="WorldRuntime"/>, a real <see cref="TeleportService"/> and the world's own
/// recording save queue, with the shadow loader and the session entry replaced by recording doubles. The production loader and entry run
/// against a full test host in <see cref="WorkerTransferTargetProductionTests"/>.
/// </summary>
public sealed class WorkerTransferTargetTests : IDisposable
{
    private static readonly CharacterOwnershipToken SourceToken = new("world", 3, Guid.NewGuid());
    private static readonly CharacterOwnershipToken TargetToken = new("world", 4, Guid.NewGuid());
    private static readonly WorkerNodeId Source = new("worker-a");
    private static readonly WorkerNodeId Target = new("worker-b");

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ],
        [], [], [], []);

    private readonly WorldRuntime _world;
    private readonly RecordingSaveQueue _saveQueue = new();
    private readonly TeleportService _teleports;
    private readonly ConcurrentQueue<string> _events = new();
    private readonly RecordingSession _session = new(5, 9);
    private readonly FakeLoader _loader;
    private readonly FakeEntry _entry;

    public WorkerTransferTargetTests()
    {
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, _saveQueue, NullLogger<WorldRuntime>.Instance)
        {
            CharacterOwnership = TargetToken,
        };
        WorldMaps.Of(_world).Load(Content);
        _teleports = new TeleportService(_world, _ => _events.Enqueue("before"), _ => _events.Enqueue("after"));
        _world.PlayerLoggingOut += _teleports.Forget;
        _world.Start();
        _loader = new FakeLoader(_session);
        _entry = new FakeEntry(_events);
    }

    public void Dispose() => _world.Stop();

    private WorkerTransferTarget NewTarget(WorkerAdmission? admission = null, int maxReservations = 64)
        => new(Target, _world, _loader, _entry, admission, maxReservations);

    private static PlayerTransferBundle NewBundle(Guid id, ulong characterGuid = 7) => new()
    {
        TransferId = id,
        CharacterGuid = characterGuid,
        CharacterId = (int)characterGuid,
        AccountId = 5,
        SessionGeneration = 9,
        SourceWorkerId = Source,
        TargetWorkerId = Target,
        SourceOwnershipToken = SourceToken,
        TargetMapId = 1,
        TargetInstanceId = 0,
        TargetPosition = new TransferPosition(10f, 20f, 30f, 1f),
        Health = 100,
        Powers = [0, 0, 0, 0, 0],
        Level = 40,
    };

    private Task<bool> OnWorldAsync(Func<bool> action) => _world.InvokeAsync(action);

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await _world.InvokeAsync(condition))
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>The client finishes its loading screen: MSG_MOVE_WORLDPORT_ACK reaches the teleport service, and the player enters its map.</summary>
    private async Task AckWorldportAsync(ulong characterGuid)
    {
        Assert.True(await OnWorldAsync(() => _teleports.HandleWorldportAck(_world.FindOnlinePlayer(new ObjectGuid(characterGuid))!)));
        await WaitForAsync(() => _world.FindOnlinePlayer(new ObjectGuid(characterGuid))?.Map is not null, "the player to enter its map");
    }

    [Fact]
    public async Task Reserve_IsIdempotentPerTransfer_AndRefusesASecondTransferOfTheSameCharacter()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();

        Assert.True(await target.TryReserveSlotAsync(id, 7, 1, 0));
        Assert.True(await target.TryReserveSlotAsync(id, 7, 1, 0));
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 7, 1, 0));
        Assert.Equal(1, target.ReservedCount);
    }

    [Fact]
    public async Task Reserve_IsRefused_WhenAdmissionIsClosed_OrTheMapIsNotOwned_OrTheCharacterIsOnline()
    {
        var admission = new WorkerAdmission(WorkerAdmission.StandbyReason);
        WorkerTransferTarget target = NewTarget(admission);
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 7, 1, 0));
        admission.Open();
        Assert.True(await target.TryReserveSlotAsync(Guid.NewGuid(), 7, 1, 0));

        _world.Ownership = new MapOwnership([new MapOwnership.Range(0, 0)], allowInstanceMaps: false);
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 1, 0));
        Assert.True(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 0, 0));

        Player online = _loader.NewPlayer(9, _session);
        await _world.InvokeAsync(() =>
        {
            _world.AddPlayer(online, _world.GetMap(0));
            return true;
        });
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 9, 0, 0));
    }

    [Fact]
    public async Task ShadowLoad_LeavesThePlayerOutOfTheWorld_AndTheWorldsSaveQueueUntouched()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);

        await target.InstallPassiveShadowAsync(id, NewBundle(id));
        await target.InstallPassiveShadowAsync(id, NewBundle(id)); // repeated: the same load

        Assert.True(target.HasShadow(id));
        Assert.Equal(1, _loader.Loads);
        Assert.Empty(_saveQueue.Saved);
        Assert.False(_saveQueue.IsPending(7));
        Assert.False(_world.IsOnline(new ObjectGuid(7)));
        Assert.Equal(0, _world.OnlinePlayerCount);
        Assert.Equal(0, _teleports.PendingCount);
        Assert.Empty(_events);
        Assert.Empty(_session.Opcodes);
    }

    [Fact]
    public async Task Activate_SendsTransferPendingAndNewWorld_AndAddsThePlayerOnlyWhenTheWorldportAckArrives()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        await target.InstallPassiveShadowAsync(id, NewBundle(id));

        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        // Before the ack: the loading screen only. No create block, no login packets, no map.
        Player? player = _world.FindOnlinePlayer(new ObjectGuid(7));
        Assert.NotNull(player);
        Assert.Null(player.Map);
        Assert.Equal(new[] { WorldOpcode.SmsgTransferPending, WorldOpcode.SmsgNewWorld }, _session.Opcodes.ToArray());
        Assert.Equal(new[] { "attach" }, _events.ToArray());
        Assert.Equal(TeleportStage.Far, await _world.InvokeAsync(() => _teleports.StageOf(player)));
        Assert.True(target.WasActivated(id));
        Assert.Equal(0, target.ReservedCount);

        await AckWorldportAsync(7);

        Assert.Equal(1u, player.Map!.MapId);
        Assert.Equal((10f, 20f, 30f), (player.X, player.Y, player.Z));
        Assert.Equal(TargetToken, player.CharacterOwnership);
        Assert.Equal(new[] { "attach", "before", "resume", "after" }, _events.ToArray());
        Assert.Equal(0, await _world.InvokeAsync(() => _teleports.PendingCount));
        Assert.Equal(1, _session.Opcodes.Count(op => op == WorldOpcode.SmsgNewWorld));
    }

    [Fact]
    public async Task Activate_WithoutAnEarlierInstall_LoadsTheShadowFirst()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid(); // no reserve either: the roll-forward after a target restart

        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        Assert.True(_world.IsOnline(new ObjectGuid(7)));
        Assert.Equal(1, _loader.Loads);
    }

    [Fact]
    public async Task DuplicateActivate_IsANoOp()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);
        int newWorlds = _session.Opcodes.Count(op => op == WorldOpcode.SmsgNewWorld);

        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);
        await target.InstallPassiveShadowAsync(id, NewBundle(id));

        Assert.Equal(1, _loader.Loads);
        Assert.Equal(1, _entry.Attached);
        Assert.Equal(newWorlds, _session.Opcodes.Count(op => op == WorldOpcode.SmsgNewWorld));
        Assert.Equal(1, _world.OnlinePlayerCount);
    }

    [Fact]
    public async Task ConcurrentDuplicateActivates_AttachAndArriveOnce()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);

        await Task.WhenAll(
            target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken),
            target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken),
            target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken));

        Assert.Equal(1, _loader.Loads);
        Assert.Equal(1, _entry.Attached);
        Assert.Equal(1, _session.Opcodes.Count(op => op == WorldOpcode.SmsgNewWorld));
        Assert.Equal(1, _world.OnlinePlayerCount);
        Assert.True(target.WasActivated(id));
    }

    [Fact]
    public async Task Discard_FreesTheSlot_ReleasesTheShadow_AndBlocksALaterActivate()
    {
        WorkerTransferTarget target = NewTarget(maxReservations: 1);
        Guid id = Guid.NewGuid();
        Assert.True(await target.TryReserveSlotAsync(id, 7, 1, 0));
        await target.InstallPassiveShadowAsync(id, NewBundle(id));
        Assert.False(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 1, 0)); // full

        await target.DiscardPassiveShadowAsync(id, 7, "test");
        await target.DiscardPassiveShadowAsync(id, 7, "again"); // idempotent

        Assert.Equal(0, target.ReservedCount);
        Assert.False(target.HasShadow(id));
        Assert.Equal(1, _loader.Released);
        Assert.True(await target.TryReserveSlotAsync(Guid.NewGuid(), 8, 1, 0)); // the slot is free again
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken));
        Assert.False(_world.IsOnline(new ObjectGuid(7)));
    }

    [Fact]
    public async Task Discard_WhileTheLoadIsStillRunning_ReleasesTheShadowWhenItFinishes()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        TaskCompletionSource gate = _loader.HoldNextLoad();
        Task install = target.InstallPassiveShadowAsync(id, NewBundle(id));

        await target.DiscardPassiveShadowAsync(id, 7, "abort while loading");
        Assert.Equal(0, _loader.Released); // nothing to release yet
        gate.SetResult();
        await install; // the load itself still completes for its own caller

        await WaitForAsync(() => _loader.Released == 1, "the late shadow to be released");
        Assert.False(_world.IsOnline(new ObjectGuid(7)));
    }

    [Fact]
    public async Task ADiscardThatLandsBeforeTheActivationCommand_FailsTheActivation_AndAddsNothing()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        TaskCompletionSource gate = _loader.HoldNextLoad();
        Task activate = target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        await target.DiscardPassiveShadowAsync(id, 7, "abort racing the activation");
        gate.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => activate);
        Assert.False(_world.IsOnline(new ObjectGuid(7)));
        Assert.False(target.WasActivated(id));
        Assert.Equal(0, _entry.Attached);
        Assert.Empty(_session.Opcodes);
    }

    [Fact]
    public async Task Discard_AfterActivation_DoesNotRemoveThePlayer()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        await target.DiscardPassiveShadowAsync(id, 7, "late");

        Assert.True(_world.IsOnline(new ObjectGuid(7)));
        Assert.Equal(0, _loader.Released);
    }

    [Fact]
    public async Task Activate_WhenTheClientHasLeft_AddsNothing_AndSendsNothing()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        _entry.AttachResult = false;

        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        Assert.False(_world.IsOnline(new ObjectGuid(7)));
        Assert.Empty(_session.Opcodes);
        Assert.Equal(0, await _world.InvokeAsync(() => _teleports.PendingCount));
        Assert.Equal(1, _loader.Released); // the shadow is dropped
        Assert.True(target.WasActivated(id)); // finished: a repeat is a no-op
        Assert.Equal(0, target.ReservedCount);
    }

    [Fact]
    public async Task Activate_WhenTheArrivalCannotStart_UndoesTheRegistration_KicksTheSession_AndALaterRetrySucceeds()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        _session.ThrowOnSend = true;

        await Assert.ThrowsAnyAsync<Exception>(() => target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken));

        Assert.False(_world.IsOnline(new ObjectGuid(7)));
        Assert.Equal(0, await _world.InvokeAsync(() => _teleports.PendingCount));
        Assert.Equal(1, _session.Kicks);
        Assert.False(target.WasActivated(id));
        Assert.Equal(1, target.ReservedCount); // still the coordinator's to retry or discard

        _session.ThrowOnSend = false;
        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);

        Assert.True(_world.IsOnline(new ObjectGuid(7)));
        Assert.True(target.WasActivated(id));
    }

    /// <summary>
    /// Every step of a failed arrival's undo runs even when earlier ones fail: the arrival throws on send, forgetting the teleport throws (the observer's resume of the held saves finds the session scope closed),
    /// and the session's kick throws too. The player must still be detached (the step that follows the failing Forget), the kick attempted, and the caller sees the original failure.
    /// </summary>
    [Fact]
    public async Task AFailedArrival_RunsEveryUndoStep_EvenWhenForgetAndTheKickFail_AndRethrowsTheOriginalFailure()
    {
        var worker = new CharacterOwnershipToken("worker:b", 4, Guid.NewGuid());
        _world.CharacterOwnership = worker; // a worker-shard world: the activation issues a claim ticket for the received claim
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using var claims = new CharacterClaimService(services);
        var target = new WorkerTransferTarget(Target, _world, _loader, _entry, claims: claims);
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        _session.ThrowOnSend = true;
        _session.ThrowOnKick = true;
        _entry.ThrowOnResume = true; // Forget -> observer.Abandoned -> ResumeSaves throws, which on unguarded code skips the detach

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => target.ActivateTargetPlayerAsync(id, NewBundle(id), worker));

        Assert.Contains("send failed", failure.GetBaseException().Message, StringComparison.Ordinal); // not "kick failed"
        Assert.True(_entry.ResumeAttempts >= 1); // the Forget step did reach the failing observer
        Assert.Equal(0, LiveTicketCount(claims)); // abandon the claim ticket
        Assert.Equal(0, await _world.InvokeAsync(() => _teleports.PendingCount)); // forget the teleport
        Assert.False(_world.IsOnline(new ObjectGuid(7))); // detach the player
        Assert.Equal(1, _session.Kicks); // kick the session (attempted, and it threw)
        Assert.False(target.WasActivated(id));
    }

    /// <summary>
    /// The reconnect race: the client logs in again (a newer claim ticket for the character) while the arrival is still being started, and then the arrival fails. The failed arrival abandons
    /// only its own ticket, so the reconnect's ticket stays live and its logout release stays valid; dropping every ticket of the character would let the reconnect's claim be released under it.
    /// </summary>
    [Fact]
    public async Task AFailedArrival_AbandonsOnlyItsOwnClaimTicket_WhenTheClientReconnectedMeanwhile()
    {
        var worker = new CharacterOwnershipToken("worker:b", 4, Guid.NewGuid());
        _world.CharacterOwnership = worker;
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using var claims = new CharacterClaimService(services);
        var target = new WorkerTransferTarget(Target, _world, _loader, _entry, claims: claims);
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        long reconnectTicket = 0;
        _session.OnSend = () =>
        {
            if (reconnectTicket == 0)
                reconnectTicket = claims.IssueTicketForReceivedClaim(7, worker); // the first packet of the arrival: the reconnect lands now (once)
        };
        _session.ThrowOnSend = true;

        await Assert.ThrowsAnyAsync<Exception>(() => target.ActivateTargetPlayerAsync(id, NewBundle(id), worker));

        Assert.True(reconnectTicket > 0, "the reconnect never ran inside the arrival");
        Assert.Equal(new[] { reconnectTicket }, LiveTickets(claims)); // the failed arrival's own (older) ticket is gone, exactly the reconnect's remains
        Assert.False(target.WasActivated(id));
    }

    private static long[] LiveTickets(CharacterClaimService claims)
    {
        var live = (Dictionary<int, List<long>>)typeof(CharacterClaimService).GetField("_liveTickets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(claims)!;
        lock (typeof(CharacterClaimService).GetField("_pendingGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(claims)!)
        {
            return [.. live.Values.SelectMany(tickets => tickets).Order()];
        }
    }

    private static int LiveTicketCount(CharacterClaimService claims)
    {
        var live = (Dictionary<int, List<long>>)typeof(CharacterClaimService).GetField("_liveTickets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(claims)!;
        lock (typeof(CharacterClaimService).GetField("_pendingGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(claims)!)
        {
            return live.Values.Sum(tickets => tickets.Count);
        }
    }

    [Fact]
    public async Task ADisconnectBeforeTheWorldportAck_ResumesTheHeldSaves_AndSavesThePlayerAtItsArrivalPosition()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.ActivateTargetPlayerAsync(id, NewBundle(id), TargetToken);
        Player player = _world.FindOnlinePlayer(new ObjectGuid(7))!;

        await _world.InvokeAsync(() =>
        {
            _world.RemovePlayer(player); // the session closed while the client loaded
            return true;
        });

        Assert.Contains("resume", _events);
        Assert.Equal(0, await _world.InvokeAsync(() => _teleports.PendingCount));
        Assert.False(_world.IsOnline(new ObjectGuid(7)));
        CharacterState saved = Assert.Single(_saveQueue.Saved);
        Assert.Equal((1u, 10f, 20f, 30f), (saved.MapId, saved.X, saved.Y, saved.Z));
    }

    [Fact]
    public async Task FailedLoad_IsRetried_AndTheReservationSurvives()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();
        await target.TryReserveSlotAsync(id, 7, 1, 0);
        _loader.FailNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.InstallPassiveShadowAsync(id, NewBundle(id)));
        Assert.Equal(1, target.ReservedCount);
        await target.InstallPassiveShadowAsync(id, NewBundle(id));

        Assert.True(target.HasShadow(id));
        Assert.Equal(2, _loader.Loads);
    }

    [Fact]
    public async Task Install_ForABundleOfAnotherTransferOrWorker_Throws()
    {
        WorkerTransferTarget target = NewTarget();
        Guid id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => target.InstallPassiveShadowAsync(id, NewBundle(Guid.NewGuid())));
        await Assert.ThrowsAsync<ArgumentException>(() => target.InstallPassiveShadowAsync(id, NewBundle(id) with { TargetWorkerId = new WorkerNodeId("other") }));
    }

    private sealed class FakeLoader(RecordingSession session) : ITransferShadowLoader
    {
        private int _loads;
        private int _released;
        private volatile bool _failNext;
        private TaskCompletionSource? _hold;

        public int Loads => Volatile.Read(ref _loads);

        public int Released => Volatile.Read(ref _released);

        public bool FailNext
        {
            get => _failNext;
            set => _failNext = value;
        }

        /// <summary>The next load waits until the returned source is completed.</summary>
        public TaskCompletionSource HoldNextLoad()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _hold, gate);
            return gate;
        }

        public Player NewPlayer(int id, IPlayerSession playerSession)
        {
            var character = new CharacterRecord { Id = id, AccountId = playerSession.AccountId, Name = "Shadow" + id, Race = 1, Class = 1, Gender = 0, Level = 40, MapId = 0 };
            return new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), playerSession);
        }

        public async Task<Player> LoadAsync(PlayerTransferBundle bundle, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loads);
            if (Interlocked.Exchange(ref _hold, null) is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            if (_failNext)
            {
                _failNext = false;
                throw new InvalidOperationException("load failed");
            }

            return NewPlayer(checked((int)bundle.CharacterGuid), session);
        }

        public void Release(Player shadow) => Interlocked.Increment(ref _released);
    }

    private sealed class FakeEntry(ConcurrentQueue<string> events) : ITransferSessionEntry
    {
        private int _attached;

        public int Attached => Volatile.Read(ref _attached);

        public bool AttachResult { get; set; } = true;

        public bool TryAttach(Player player)
        {
            if (!AttachResult)
            {
                return false;
            }

            Interlocked.Increment(ref _attached);
            events.Enqueue("attach");
            return true;
        }

        private int _resumeAttempts;

        public int ResumeAttempts => Volatile.Read(ref _resumeAttempts);

        /// <summary>Makes <see cref="ResumeSaves"/> throw, the way the production one does when the session's scope has already closed.</summary>
        public volatile bool ThrowOnResume;

        public void ResumeSaves(Player player)
        {
            Interlocked.Increment(ref _resumeAttempts);
            if (ThrowOnResume)
            {
                throw new ObjectDisposedException("session scope");
            }

            events.Enqueue("resume");
        }
    }

    private sealed class RecordingSession(int accountId, long generation) : IPlayerSession
    {
        private readonly ConcurrentQueue<WorldOpcode> _opcodes = new();
        private int _kicks;

        public int AccountId { get; } = accountId;

        public long SessionGeneration { get; } = generation;

        public AccountSecurity Security => AccountSecurity.Player;

        public IReadOnlyCollection<WorldOpcode> Opcodes => _opcodes;

        public volatile bool ThrowOnSend;

        public volatile bool ThrowOnKick;

        /// <summary>Runs on the sending thread at the start of every <see cref="Send"/>, before <see cref="ThrowOnSend"/> is honoured: a client action that lands in the middle of an arrival.</summary>
        public volatile Action? OnSend;

        public int Kicks => Volatile.Read(ref _kicks);

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            OnSend?.Invoke();
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("send failed");
            }

            _opcodes.Enqueue(opcode);
        }

        public void ProcessWorldPackets(Player player) { }

        public void Kick()
        {
            Interlocked.Increment(ref _kicks);
            if (ThrowOnKick)
            {
                throw new InvalidOperationException("kick failed");
            }
        }

        public void OnLoggedOut() { }
    }
}
