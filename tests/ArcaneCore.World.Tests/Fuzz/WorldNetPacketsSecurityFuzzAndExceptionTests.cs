using System.Net;
using System.Net.Sockets;
using ArcaneCore.Data.World.Warden;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Characters;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.AntiCheat;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Cluster.Tooling;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using ArcaneCore.World.Net;
using ArcaneCore.World.Net.Versioning;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Tests.Bans;
using ArcaneCore.World.Tests.Playerbots;
using ArcaneCore.World.Warden;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// Exception-path and deterministic seeded fuzz tests across Net, Packets, Handlers,
/// AntiCheat, Warden, Bans, Cluster, Clustering, HotCode, and Reload.
/// </summary>
public sealed class WorldNetPacketsSecurityFuzzAndExceptionTests
{
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class MapServiceProvider(params (Type Type, object Instance)[] entries) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            foreach ((Type type, object instance) in entries)
            {
                if (type == serviceType)
                {
                    return instance;
                }
            }

            return null;
        }
    }

    private sealed class StubRemoteSessionOutput : IRemoteSessionOutput
    {
        public bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload) => true;
    }

    private sealed class StubGatewaySessionAcceptor : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(
            GatewaySessionOpenRequest request,
            IGatewaySessionOutput output,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GatewaySessionAcceptResult.Refused(AuthResponseCode.Unavailable, "stub"));
    }

    private sealed class StubGatewaySessionSink : IGatewaySessionSink
    {
        public void OnOpened(GatewaySessionOpenReply reply)
        {
        }

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void OnClosed(ClusterSessionDetachReason reason)
        {
        }
    }

    private sealed class StubWorldHandoffStore : IWorldHandoffStore
    {
        private readonly InMemoryTransferDecisionStore _decisions = new();

        public Task<WorldHandoffRecord> BeginAsync(
            Guid handoffId,
            WorkerNodeId sourceWorkerId,
            WorkerNodeId targetWorkerId,
            CharacterOwnershipToken sourceOwnershipToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorldHandoffRecord(
                handoffId,
                sourceWorkerId,
                targetWorkerId,
                WorldHandoffStage.Begun,
                PlayerTransferDecisionState.PreCommit,
                sourceOwnershipToken,
                null,
                null,
                null,
                0,
                0));

        public Task<WorldHandoffRecord> AdvancePreCommitAsync(
            Guid handoffId,
            WorldHandoffStage nextStage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorldHandoffRecord(
                handoffId,
                new WorkerNodeId("w1"),
                new WorkerNodeId("w2"),
                nextStage,
                PlayerTransferDecisionState.PreCommit,
                new CharacterOwnershipToken("characters", 1, Guid.Empty),
                null,
                null,
                null,
                0,
                0));

        public Task<WorldHandoffRecord?> CommitEpochTakeoverAsync(
            Guid handoffId,
            string targetOwnerNode,
            Guid targetIncarnation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorldHandoffRecord?>(null);

        public Task<WorldHandoffRecord> MarkReroutedAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldHandoffRecord> MarkRetiredAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldHandoffRecord> AbortAsync(Guid handoffId, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorldHandoffRecord(
                handoffId,
                new WorkerNodeId("w1"),
                new WorkerNodeId("w2"),
                WorldHandoffStage.Aborted,
                PlayerTransferDecisionState.Aborted,
                new CharacterOwnershipToken("characters", 1, Guid.Empty),
                null,
                null,
                reason,
                0,
                0));

        public Task<WorldHandoffRecord?> GetAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorldHandoffRecord?>(null);

        public Task<IReadOnlyList<WorldHandoffRecord>> GetUnresolvedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorldHandoffRecord>>([]);

        public Task<IReadOnlyList<Guid>> GetTransferIdsAsync(Guid handoffId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public ITransferDecisionStore ForHandoff(Guid handoffId) => _decisions;
    }

    private sealed class StubWorldHandoffSource : IWorldHandoffSource
    {
        public WorkerNodeId WorkerId => new("w1");
        public CharacterOwnershipToken Ownership => new("characters", 1, Guid.NewGuid());
        public Task StopAdmissionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FreezeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RetireAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubWorldHandoffTarget(bool throwOnWarm) : IWorldHandoffTarget
    {
        public WorkerNodeId WorkerId => new("w2");
        public string OwnerNode => "w2";
        public Guid Incarnation { get; } = Guid.NewGuid();

        public Task WarmAsync(CancellationToken cancellationToken) =>
            throwOnWarm ? Task.FromException(new InvalidOperationException("warm-failed")) : Task.CompletedTask;

        public Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubWorldHandoffRouter : IWorldHandoffRouter
    {
        public Task RerouteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubRuntimeProbe(string envName, bool metadataSupported, Dictionary<string, string?> env) : IRuntimeProbe
    {
        public string EnvironmentName => envName;
        public bool MetadataUpdatesSupported => metadataSupported;
        public string? GetEnvironmentVariable(string name) => env.GetValueOrDefault(name);
    }

    private sealed class StubServiceScopeFactory : IServiceScopeFactory, IServiceScope
    {
        public static readonly StubServiceScopeFactory Instance = new();
        public IServiceProvider ServiceProvider => EmptyServiceProvider.Instance;
        public IServiceScope CreateScope() => this;
        public void Dispose()
        {
        }
    }

    private static WorldRuntime CreateMinimalWorld() =>
        new(new WorldRuntimeOptions(), NullSaveQueue.Instance, NullLogger<WorldRuntime>.Instance);

    private static WorldSession CreateMinimalRemoteSession(WorldRuntime? world = null, SessionRegistry? registry = null)
    {
        WorldRuntime w = world ?? CreateMinimalWorld();
        SessionRegistry r = registry ?? new SessionRegistry();
        return WorldSession.CreateRemote(
            new StubRemoteSessionOutput(),
            "127.0.0.1:12345",
            EmptyServiceProvider.Instance,
            new OpcodeTable(),
            w,
            r,
            new WorldSessionOptions(),
            NullLogger.Instance);
    }

    private static Player CreateTestPlayer(WorldSession session) =>
        new(
            new CharacterRecord
            {
                Id = 1,
                AccountId = 1,
                Name = "Tester",
                Race = 1,
                Class = 1,
                Gender = 0,
                Level = 1,
                ZoneId = 12,
                MapId = 0,
                X = -8949.95f,
                Y = -132.493f,
                Z = 83.5312f,
                Orientation = 0f,
            },
            new PlayerAppearance(
                DisplayId: 49,
                FactionTemplate: 1,
                PowerType: PowerType.Rage,
                BaseHealth: 100,
                BaseMana: 0,
                MaxHealth: 100,
                MaxPower: 1000,
                StartPower: 0,
                NextLevelXp: 400),
            session);

    private static ClusterNodeManifest CreateGatewayManifest() =>
        GatewayLinkManifest.Create(ClusterRole.Gateway, "gw-1", Guid.NewGuid(), 1, "dev", 1, 1);

    private static ClusterNodeManifest CreateWorkerManifest() =>
        GatewayLinkManifest.Create(ClusterRole.Worker, "worker-1", Guid.NewGuid(), 1, "dev", 1, 1);

    // ========================================================================
    // 1. Net, Packets & Handlers Exception Tests
    // ========================================================================

    [Fact]
    public void Net_PacketCapture_OutOfRangeMaxBytes_ThrowsArgumentOutOfRangeException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "arcanecore-pcap-test-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketCapture(tempDir, 89));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PacketCapture(tempDir, (1024L * 1024 * 1024) + 1));
    }

    [Fact]
    public void Net_SessionRegistry_IsCurrentNull_ThrowsArgumentNullException()
    {
        using var registry = new SessionRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.IsCurrent(null!));
    }

    [Fact]
    public void Net_WorldSession_RemoteAndKickAndSendGuards_ThrowExpectedExceptions()
    {
        using WorldRuntime world = CreateMinimalWorld();
        using var registry = new SessionRegistry();
        var opcodes = new OpcodeTable();
        var options = new WorldSessionOptions();
        var output = new StubRemoteSessionOutput();

        Assert.Throws<ArgumentNullException>(() => WorldSession.CreateRemote(
            null!, "127.0.0.1:5000", EmptyServiceProvider.Instance, opcodes, world, registry, options, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() => WorldSession.CreateRemote(
            output, null!, EmptyServiceProvider.Instance, opcodes, world, registry, options, NullLogger.Instance));

        Assert.Throws<ArgumentException>(() => WorldSession.CreateRemote(
            output, "   ", EmptyServiceProvider.Instance, opcodes, world, registry, options, NullLogger.Instance));

        WorldSession session = WorldSession.CreateRemote(
            output, "127.0.0.1:5000", EmptyServiceProvider.Instance, opcodes, world, registry, options, NullLogger.Instance);

        Assert.Throws<ArgumentNullException>(() => session.Kick(null!));
        Assert.Throws<ArgumentException>(() => session.Kick(""));
        Assert.Throws<ArgumentException>(() => session.Kick("   "));
        Assert.Throws<InvalidOperationException>(() => session.CloseManaged());

        byte[] oversized = new byte[WorldSession.MaxServerPayload + 1];
        Assert.Throws<ArgumentException>(() => session.Send(WorldOpcode.SmsgPong, oversized));
    }

    [Fact]
    public async Task Net_ManagedWorldSession_RefusalPaths_ThrowInvalidOperationException()
    {
        using WorldRuntime world = CreateMinimalWorld();
        using var registry = new SessionRegistry();
        var opcodes = new OpcodeTable();
        var options = new WorldSessionOptions();

        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var botStore = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var bans = new InMemoryBanStore();

        Account validOwner = await accounts.CreateAsync(new Account
        {
            Username = "ALICE",
            Salt = new byte[32],
            Verifier = new byte[32],
            Status = AccountStatus.Active,
            Security = AccountSecurity.Player,
        });
        CharacterRecord charOwned = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = validOwner.Id,
            Name = "Hero",
            Race = 1,
            Class = 1,
            Level = 1,
        });
        CharacterRecord charOther = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = validOwner.Id + 99,
            Name = "Other",
            Race = 1,
            Class = 1,
            Level = 1,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldSession.CreateManagedAsync(
            validOwner, charOwned.Id, EmptyServiceProvider.Instance, opcodes, world, registry, options, NullLogger.Instance));

        // 1. Owner not in store -> managed-owner-refused
        var emptyAccounts = new InMemoryAccountStore();
        var noAccountServices = new MapServiceProvider(
            (typeof(IAccountStore), emptyAccounts),
            (typeof(ICharacterStore), characters),
            (typeof(IManagedPlayerbotStore), botStore));
        InvalidOperationException exMissing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorldSession.CreateManagedAsync(validOwner, charOwned.Id, noAccountServices, opcodes, world, registry, options, NullLogger.Instance));
        Assert.Equal("managed-owner-refused", exMissing.Message);

        // 2. Character owned by another account -> managed-character-refused
        var baseServices = new MapServiceProvider(
            (typeof(IAccountStore), accounts),
            (typeof(ICharacterStore), characters),
            (typeof(IManagedPlayerbotStore), botStore));
        InvalidOperationException exChar = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorldSession.CreateManagedAsync(validOwner, charOther.Id, baseServices, opcodes, world, registry, options, NullLogger.Instance));
        Assert.Equal("managed-character-refused", exChar.Message);

        // 3. Unregistered bot -> managed-registration-refused
        InvalidOperationException exUnreg = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorldSession.CreateManagedAsync(validOwner, charOwned.Id, baseServices, opcodes, world, registry, options, NullLogger.Instance));
        Assert.Equal("managed-registration-refused", exUnreg.Message);

        // Register bot in store, then test active ban -> managed-owner-banned
        var botRecord = new ManagedPlayerbot(
            Guid.NewGuid(),
            validOwner.Id,
            charOwned.Id,
            validOwner.Username,
            true,
            ManagedPlayerbotState.Stopped,
            PlayerbotGoalKind.Explore,
            0,
            0,
            1,
            1,
            1);
        await botStore.CreateAsync(botRecord);
        bans.AddAccountRow(validOwner.Id, 0, 0, active: true);

        var bannedServices = new MapServiceProvider(
            (typeof(IAccountStore), accounts),
            (typeof(ICharacterStore), characters),
            (typeof(IManagedPlayerbotStore), botStore),
            (typeof(IBanStore), bans));
        InvalidOperationException exBanned = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorldSession.CreateManagedAsync(validOwner, charOwned.Id, bannedServices, opcodes, world, registry, options, NullLogger.Instance, botId: botRecord.BotId));
        Assert.Equal("managed-owner-banned", exBanned.Message);
    }

    [Fact]
    public void Packets_And_Handlers_ThrowExpectedExceptions()
    {
        // ChatPackets.BuildMessage throws ArgumentOutOfRangeException for channel/monster chat types
        ChatType[] unsupportedChatTypes =
        [
            ChatType.Channel,
            ChatType.MonsterSay,
            ChatType.MonsterYell,
            ChatType.MonsterEmote,
            ChatType.MonsterWhisper,
            ChatType.RaidBossEmote,
            ChatType.RaidBossWhisper,
        ];
        foreach (ChatType type in unsupportedChatTypes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ChatPackets.BuildMessage(type, Language.Universal, ObjectGuid.Empty, "hello", ChatTag.None));
        }

        // LoginPackets.BuildTutorialFlags throws ArgumentException unless length is 8
        Assert.Throws<ArgumentException>(() => LoginPackets.BuildTutorialFlags([]));
        Assert.Throws<ArgumentException>(() => LoginPackets.BuildTutorialFlags([1u, 2u, 3u, 4u, 5u, 6u, 7u]));
        Assert.Throws<ArgumentException>(() => LoginPackets.BuildTutorialFlags([1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u]));

        // QueryResponseCache.Get throws ArgumentNullException on null builder
        var cache = new QueryResponseCache<string>();
        Assert.Throws<ArgumentNullException>(() => cache.Get(1u, "tpl", null!));

        // InactiveQueueHandlers throws ArgumentOutOfRangeException when CmsgBattlefieldStatus has non-empty payload
        var table = new OpcodeTable();
        new InactiveQueueHandlers().Register(table);
        Assert.True(table.TryGet(WorldOpcode.CmsgBattlefieldStatus, out OpcodeHandler? bfHandler));
        Assert.NotNull(bfHandler?.World);
        using WorldRuntime world = CreateMinimalWorld();
        WorldSession session = CreateMinimalRemoteSession(world);
        Player player = CreateTestPlayer(session);
        Assert.Throws<ArgumentOutOfRangeException>(() => bfHandler!.World!(session, player, [0x01]));

        // OpcodeTable duplicate registration and sealed table registration throw InvalidOperationException
        table.OnWorld(WorldOpcode.CmsgPing, static (_, _, _) => { });
        Assert.Throws<InvalidOperationException>(() => table.OnWorld(WorldOpcode.CmsgPing, static (_, _, _) => { }));
        Assert.Throws<InvalidOperationException>(() =>
            table.OnSession(WorldOpcode.CmsgPing, SessionStates.Authenticated, static (_, _) => Task.CompletedTask));

        var replacement = new OpcodeTable();
        table.Replace(replacement);
        Assert.Throws<InvalidOperationException>(() => table.OnWorld(WorldOpcode.CmsgLogoutRequest, static (_, _, _) => { }));
        Assert.Throws<InvalidOperationException>(() => replacement.OnWorld(WorldOpcode.CmsgLogoutRequest, static (_, _, _) => { }));
    }

    // ========================================================================
    // 2. AntiCheat & Warden Exception Tests
    // ========================================================================

    [Fact]
    public void AntiCheat_NullGuards_ThrowArgumentNullException()
    {
        var feature = new AntiCheatFeature(EmptyServiceProvider.Instance, NullLogger<AntiCheatFeature>.Instance);
        using WorldRuntime world = CreateMinimalWorld();
        WorldSession session = CreateMinimalRemoteSession(world);
        Player player = CreateTestPlayer(session);

        Assert.Throws<ArgumentNullException>(() => feature.ApplyOptions(null!));
        Assert.Throws<ArgumentNullException>(() => feature.IsExempt(null!, player));
        Assert.Throws<ArgumentNullException>(() => feature.IsExempt(session, null!));
        Assert.Throws<ArgumentNullException>(() => feature.Grant(null!, MovementFlags.WaterWalking, true));
        Assert.Throws<ArgumentNullException>(() => feature.OnGameObjectTooFar(session, player, null!));
        Assert.Throws<ArgumentNullException>(() => feature.SetScore(null!, 10f));

        var logQueue = new AntiCheatLogQueue();
        var validEntry = new AntiCheatLogEntry(1, 1, 1, 5f, 5f, 1, 0, 0f, 0f, 0f, "detail", 100, 100);
        var validLogOpts = new AntiCheatLogOptions();
        Assert.Throws<ArgumentNullException>(() => logQueue.Add(null!, AntiCheatViolation.Speed, 100u, validLogOpts));
        Assert.Throws<ArgumentNullException>(() => logQueue.Add(validEntry, AntiCheatViolation.Speed, 100u, null!));

        Assert.Throws<ArgumentNullException>(() => new MovementAntiCheat(null!));
        Assert.Throws<ArgumentNullException>(() => new SpeedHackDetector(null!));

        var checks = new MovementAntiCheat(new AntiCheatOptions());
        var sample = default(MovementSample);
        Assert.Throws<ArgumentNullException>(() => checks.Observe(in sample, null!));
        Assert.Throws<ArgumentNullException>(() => checks.NotifyTimeSkip(100u, 200u, null!));
        Assert.Throws<ArgumentNullException>(() => checks.NotifyAcknowledgement(100u, 200u, null!));
    }

    [Fact]
    public void Warden_ValidationAndCryptoGuards_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => WardenCheckRows.ToOptions(null!, 5875, out _));
        Assert.Throws<NullReferenceException>(() => WardenCheckRows.Merge(null!, [], 5875));
        Assert.Throws<NullReferenceException>(() => WardenCheckRows.Merge([], null!, 5875));

        Assert.Throws<ArgumentException>(() => new WardenRc4(ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(() => new WardenCryptoContext(new byte[39]));
        Assert.Throws<ArgumentException>(() => new WardenCryptoContext(new byte[41]));
        Assert.Throws<ArgumentException>(() => WardenCryptoContext.DeriveInitialKeys(new byte[16], out _, out _));

        var crypto = new WardenCryptoContext(new byte[40]);
        Assert.Throws<ArgumentException>(() => crypto.InstallModuleKeys(ReadOnlySpan<byte>.Empty, new byte[16]));
        Assert.Throws<ArgumentException>(() => crypto.InstallModuleKeys(new byte[16], ReadOnlySpan<byte>.Empty));

        var options = new WardenOptions();
        Assert.Throws<ArgumentException>(() =>
            new WardenSession(new byte[39], options, static _ => { }, static _ => { }));
        Assert.Throws<NullReferenceException>(() =>
            new WardenSession(new byte[40], null!, static _ => { }, static _ => { }));
        Assert.Throws<NullReferenceException>(() =>
            new WardenSession(new byte[40], options, null!, static _ => { }).Start());
    }

    // ========================================================================
    // 3. Reload, HotCode, Cluster & Clustering Exception Tests
    // ========================================================================

    [Fact]
    public async Task Reload_AllContentReloadablesAndCoordinator_ThrowExpectedExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => new ReloadCoordinator(null!));
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => coordinator.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => coordinator.Register(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.ReloadAsync(null!));

        IServiceProvider empty = EmptyServiceProvider.Instance;
        CancellationToken ct = CancellationToken.None;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ChatWordFilterContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CreatureContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GameObjectContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InstanceEncounterContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ItemContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ItemEnchantProcContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SpellEnchantChargesContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AllLootContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SkillFishingBaseLevelContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CreatureLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GameObjectLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ItemLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SkinningLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReferenceLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FishingLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PickpocketingLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DisenchantLootTemplateContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GameTeleContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AreaTriggerTeleportContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NpcGossipContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NpcTextContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NpcTrainerContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NpcVendorContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PointsOfInterestContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GossipMenuContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GossipMenuOptionContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuestContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OnKillReputationReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReputationSpilloverReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReputationRewardRateReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReservedNameContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SpellContentReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SpellProcEventReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SpellThreatReloadable(empty).BuildAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TavernContentReloadable(empty).BuildAsync(ct));
    }

    [Fact]
    public void HotCode_GuardAndEventAiRespawnPolicy_ThrowExpectedExceptions()
    {
        var refusedOptions = new HotCodeOptions { Enabled = false };
        var activeProbe = new StubRuntimeProbe(
            envName: "Development",
            metadataSupported: true,
            env: new Dictionary<string, string?> { [HotCodeGuard.WatchVariable] = "1" });
        var audit = new HotCodeAudit(null);

        Assert.Throws<HotCodeRefusedException>(() => HotCodeGuard.Enforce(refusedOptions, activeProbe, audit));

        var prodEnabledOptions = new HotCodeOptions { Enabled = true };
        var prodProbe = new StubRuntimeProbe(
            envName: "Production",
            metadataSupported: false,
            env: new Dictionary<string, string?>());
        Assert.Throws<HotCodeRefusedException>(() => HotCodeGuard.Enforce(prodEnabledOptions, prodProbe, audit));

        Assert.Throws<ArgumentOutOfRangeException>(() => new EventAiRespawnPolicy(0, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventAiRespawnPolicy(4, TimeSpan.Zero));
    }

    [Fact]
    public void Clustering_CharacterFencingOptions_Validate_ThrowsInvalidOperationException()
    {
        var requiredButDisabled = new CharacterFencingOptions
        {
            Enabled = false,
            RequiredByWorkerMode = true,
        };
        Assert.Throws<InvalidOperationException>(() => requiredButDisabled.Validate());

        var blankOwner = new CharacterFencingOptions
        {
            Enabled = true,
            OwnerNode = "   ",
        };
        Assert.Throws<InvalidOperationException>(() => blankOwner.Validate());

        var negativeInterval = new CharacterFencingOptions
        {
            Enabled = true,
            OwnerNode = "worker-1",
            ClaimWatchIntervalSeconds = -1,
        };
        Assert.Throws<InvalidOperationException>(() => negativeInterval.Validate());

        var partialTakeover = new CharacterFencingOptions
        {
            Enabled = true,
            OwnerNode = "worker-1",
            TakeoverEpoch = 2,
            TakeoverIncarnation = null,
        };
        Assert.Throws<InvalidOperationException>(() => partialTakeover.Validate());

        var zeroEpochTakeover = new CharacterFencingOptions
        {
            Enabled = true,
            OwnerNode = "worker-1",
            TakeoverEpoch = 0,
            TakeoverIncarnation = Guid.NewGuid(),
        };
        Assert.Throws<InvalidOperationException>(() => zeroEpochTakeover.Validate());
    }

    [Fact]
    public async Task Cluster_GatewayLinkAndWireAndAuxiliary_ThrowExpectedExceptions()
    {
        // GatewayLinkChannel unconnected socket guard and oversized frame guard
        using var unconnectedTcp = new TcpClient();
        Assert.Throws<InvalidOperationException>(() => new GatewayLinkChannel(unconnectedTcp));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connectedClient = new TcpClient();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        await connectedClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using TcpClient acceptedServer = await acceptTask;
        await using var channel = new GatewayLinkChannel(connectedClient);
        Assert.Throws<NullReferenceException>(() => channel.TryEnqueue(null!));
        Assert.Throws<InvalidOperationException>(() =>
            channel.TryEnqueue(new byte[LoopbackGatewayWorkerHost.MaxFrameBytes + 1]));

        // GatewaySessionLinkHost secret validation and constructor guards
        Assert.Throws<ArgumentException>(() => GatewaySessionLinkHost.ThrowIfWeakSecret(null, "TestSecret"));
        Assert.Throws<ArgumentException>(() => GatewaySessionLinkHost.ThrowIfWeakSecret("short-secret", "TestSecret"));

        ClusterNodeManifest gatewayManifest = CreateGatewayManifest();
        ClusterNodeManifest workerManifest = CreateWorkerManifest();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        string strongSecret = "0123456789abcdef0123456789abcdef";

        Assert.Throws<ArgumentException>(() =>
            new GatewaySessionLinkClient(workerManifest, endpoint, strongSecret));
        Assert.Throws<ArgumentException>(() =>
            new GatewaySessionLinkClient(gatewayManifest, endpoint, "too-short"));

        await using var client = new GatewaySessionLinkClient(gatewayManifest, endpoint, strongSecret);
        var openReq = new GatewaySessionOpenRequest(1, 1, "ALICE", 5875, "127.0.0.1:5000");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.OpenAsync(openReq, new StubGatewaySessionSink(), CancellationToken.None));

        Assert.Throws<ArgumentException>(() =>
            new GatewaySessionLinkHost(gatewayManifest, new StubGatewaySessionAcceptor(), static () => 1L, "too-short", endpoint));

        // GatewaySessionWire decode & encode error paths
        Assert.Throws<ArgumentException>(() =>
            GatewaySessionWire.EncodeOpen(1, new GatewaySessionOpenRequest(1, 1, new string('A', 300), 5875, "127.0.0.1:1")));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeOpen([0x01, 0x02]));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeOpenReply([0x01, 0x02]));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeClose([0x01, 0x02]));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodePacket(new byte[] { 0x01, 0x02 }));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeNonceFrame([0x01, 0x02], "nonce"));
        Assert.Throws<ArgumentException>(() =>
            GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, new byte[15]));

        // Auxiliary cluster coordinators guards
        Assert.Throws<ArgumentNullException>(() => new RemoteBotChatServiceHost(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RemoteBotChatServiceHost(static (_, _) => ValueTask.FromResult("ok"), maxDailyProviderCalls: 0));
        Assert.Throws<ArgumentNullException>(() => new LocalOrRemoteBotChatCoordinator(null, null!));
        Assert.Throws<ArgumentNullException>(() => new ClusterBotPlannerClient(null, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FencedAuctionBotIntentProcessor(0));
    }

    [Fact]
    public async Task Cluster_HandoffAndRollbackAndReadinessAndSupervision_ThrowExpectedExceptions()
    {
        using WorldRuntime world = CreateMinimalWorld();
        ClusterNodeManifest workerManifest = CreateWorkerManifest();

        // ClusterWorkerReadinessProbe guards and stopped state
        Assert.Throws<ArgumentNullException>(() => new ClusterWorkerReadinessProbe(null!, world));
        Assert.Throws<ArgumentNullException>(() => new ClusterWorkerReadinessProbe(workerManifest, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClusterWorkerReadinessProbe(workerManifest, world, initialOwnerEpoch: 0));

        var probe = new ClusterWorkerReadinessProbe(workerManifest, world, initialOwnerEpoch: 1);
        probe.Stop();
        Assert.False(probe.IsLive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.VerifyAndMarkReadyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.BeginDrainAsync());

        // ClusterRollbackCoordinator null guard
        var rollback = new ClusterRollbackCoordinator();
        Assert.Throws<ArgumentNullException>(() => rollback.VerifySafeRollbackToLocal(null!));

        // ClusterRoleFeatureFilter guards
        var filter = new ClusterRoleFeatureFilter();
        Assert.Throws<ArgumentNullException>(() => filter.ShouldAttachFeature(null!, ClusterNodeRole.MapWorker));
        Assert.Throws<ArgumentException>(() => filter.ShouldAttachFeature(typeof(string), ClusterNodeRole.MapWorker));

        // SupervisedProcessConfig validation
        var badConfig = new SupervisedProcessConfig
        {
            Name = "   ",
            ExecutablePath = "world.exe",
        };
        Assert.Throws<InvalidOperationException>(() => badConfig.Validate());
        Assert.Throws<ArgumentNullException>(() => SupervisedOutputMultiplexer.FormatLine(null!, "line"));
        Assert.Throws<ArgumentException>(() => SupervisedOutputMultiplexer.FormatLine("   ", "line"));
        Assert.Throws<ArgumentNullException>(() => new SupervisedOutputMultiplexer((TextWriter)null!));

        // WorkerAdmission guards
        var admission = new WorkerAdmission();
        Assert.Throws<ArgumentNullException>(() => admission.Close(null!));
        Assert.Throws<ArgumentException>(() => admission.Close("   "));

        // WorldHandoffCoordinator aborts and throws WorldHandoffAbortedException when target warm fails
        var coordinator = new WorldHandoffCoordinator(
            new StubWorldHandoffStore(),
            new StubWorldHandoffSource(),
            new StubWorldHandoffTarget(throwOnWarm: true),
            new StubWorldHandoffRouter());
        await Assert.ThrowsAsync<WorldHandoffAbortedException>(() =>
            coordinator.RunAsync(Guid.NewGuid(), cancellationToken: CancellationToken.None));

        // LocalWorldHandoffSource throws InvalidOperationException when unfrozen or retired
        var saveQueue = new CharacterSaveQueue(
            StubServiceScopeFactory.Instance,
            NullLogger<CharacterSaveQueue>.Instance);
        using var source = new LocalWorldHandoffSource(new WorkerNodeId("w1"), world, saveQueue, admission, new WorldWriterGuard());
        Assert.Throws<InvalidOperationException>(() => _ = source.Ownership);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FlushAsync(CancellationToken.None));
        await source.RetireAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FreezeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ResumeAsync(CancellationToken.None));
    }

    // ========================================================================
    // 4. Deterministic Seeded Fuzz Tests
    // ========================================================================

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_ClientPackets_AuthAddonCompressionBugAndMovement_NeverCrashUnexpectedly(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 250; i++)
        {
            int len = rng.Next(0, 512);
            byte[] payload = new byte[len];
            rng.NextBytes(payload);

            // 1. AuthSessionRequest.TryParse & Build5875Protocol.Instance.Auth.TryParseAuthSession
            _ = AuthSessionRequest.TryParse(payload, out _);
            _ = Build5875Protocol.Instance.Auth.TryParseAuthSession(payload, out _);

            // 2. AddonInfo.BuildResponse (never throws on arbitrary bytes)
            byte[] addonReply = AddonInfo.BuildResponse(payload);
            Assert.NotNull(addonReply);
            Assert.True(addonReply.Length <= AddonInfo.MaxResponseBytes);

            // 3. AccountDataCompression.TryDecompress & Compress roundtrip
            int expectedSize = rng.Next(-10, AccountDataCompression.MaxDecompressedSize + 100);
            _ = AccountDataCompression.TryDecompress(payload, expectedSize, out _);
            if (i % 10 == 0)
            {
                byte[] compressed = AccountDataCompression.Compress(payload);
                if (payload.Length > 0)
                {
                    Assert.True(AccountDataCompression.TryDecompress(compressed, payload.Length, out byte[] inflated));
                    Assert.Equal(payload, inflated);
                }
            }

            // 4. MiscNoticeHandlers.TryParseBug
            _ = MiscNoticeHandlers.TryParseBug(payload, out _, out _, out _);

            // 5. WorldSession error context & classification helpers
            WorldOpcode randomOpcode = (WorldOpcode)rng.Next(0, 0x400);
            _ = WorldSession.ContextForSessionPacket(randomOpcode);
            _ = WorldSession.ContextForWorldPacket(randomOpcode, payload);
            _ = WorldSession.IsContainableWorldOpcode(randomOpcode);
            _ = UnhandledOpcodeTracker.LabelOf(randomOpcode);

            // 6. MovementInfo.Read + MovementValidator.IsValid
            try
            {
                var reader = new PacketReader(payload);
                MovementInfo info = MovementInfo.Read(ref reader);
                _ = MovementValidator.IsValid(in info, strictFiniteness: (i & 1) == 0);
            }
            catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException)
            {
                // Expected for truncated or malformed movement frames
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_WardenCodecCryptoAndSession_NeverCrashOnArbitraryPayloads(int seed)
    {
        var rng = new Random(seed);
        byte[] sessionKey = new byte[40];
        rng.NextBytes(sessionKey);

        var options = new WardenOptions
        {
            Enabled = true,
            Action = WardenAction.Log,
            ProtocolAction = WardenAction.Log,
            MaxProtocolFailures = 5,
        };

        var checkSpecs = new List<WardenCheckOptions>
        {
            new() { Id = 1, Kind = WardenCheckKind.Timing, Comment = "timing" },
            new() { Id = 2, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90909090", Comment = "mem" },
            new() { Id = 3, Kind = WardenCheckKind.Lua, Path = "DefaultServerLogin", Comment = "lua" },
            new() { Id = 4, Kind = WardenCheckKind.Mpq, Path = "Interface\\FrameXML\\UIParent.lua", Expected = "0102030405060708090A0B0C0D0E0F1011121314", Comment = "mpq" },
            new() { Id = 5, Kind = WardenCheckKind.ModuleByName, Module = "speedhack.dll", Wanted = false, Comment = "mod" },
        };

        var builtChecks = new List<WardenCheck>();
        foreach (WardenCheckOptions spec in checkSpecs)
        {
            Assert.Null(WardenCheck.TryCreate(spec, out WardenCheck? created));
            Assert.NotNull(created);
            builtChecks.Add(created!);
        }

        byte[] requestBytes = WardenCodec.CheckRequest(builtChecks, () => (uint)rng.Next());
        Assert.NotEmpty(requestBytes);

        var session = new WardenSession(
            sessionKey,
            options,
            static _ => { },
            static _ => { },
            new Random(seed),
            checkSpecs);
        session.Start();

        for (int i = 0; i < 200; i++)
        {
            int len = rng.Next(0, 256);
            byte[] buf = new byte[len];
            rng.NextBytes(buf);

            _ = WardenCodec.Checksum(buf);
            _ = WardenCodec.CheckResult(buf, builtChecks);

            session.Handle(buf);
            session.Update((uint)rng.Next(0, 5000));
            session.Flush();
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_MovementAntiCheatAndSpeedHackDetector_NeverThrowOrProduceNaN(int seed)
    {
        var rng = new Random(seed);
        var options = new AntiCheatOptions
        {
            Enabled = true,
            TerrainChecks = false,
        };
        var checks = new MovementAntiCheat(options);
        var detector = new SpeedHackDetector(options.SpeedClock);
        var findings = new List<AntiCheatFinding>();
        var scores = new AntiCheatScores();

        uint clientTime = 1000;
        uint serverTime = 1000;
        float x = -8949f;
        float y = -132f;
        float z = 83f;

        for (int i = 0; i < 250; i++)
        {
            clientTime = unchecked(clientTime + (uint)rng.Next(0, 2000));
            serverTime = unchecked(serverTime + (uint)rng.Next(0, 1500));
            x += (float)((rng.NextDouble() - 0.5) * 40.0);
            y += (float)((rng.NextDouble() - 0.5) * 40.0);
            z += (float)((rng.NextDouble() - 0.5) * 15.0);

            SpeedHackDecision decision = detector.Feed(clientTime, serverTime);
            Assert.False(double.IsNaN(decision.Ratio));

            var movement = new MovementInfo
            {
                Flags = (MovementFlags)rng.Next(0, int.MaxValue),
                Time = clientTime,
                X = x,
                Y = y,
                Z = z,
                Orientation = (float)(rng.NextDouble() * Math.Tau),
            };

            var sample = new MovementSample
            {
                Opcode = (i % 7 == 0) ? WorldOpcode.MsgMoveFallLand : WorldOpcode.MsgMoveHeartbeat,
                Movement = movement,
                ReceivedMs = serverTime,
                LatencyMs = rng.Next(0, 600),
                AllowedSpeed = 7.0f,
                Alive = (i % 5 != 0),
                Rooted = (i % 11 == 0),
                GrantedFlags = (i % 3 == 0) ? MovementFlags.WaterWalking : MovementFlags.None,
                Transport = (TransportClaim)(i % 4),
                Terrain = null,
            };

            findings.Clear();
            checks.Observe(in sample, findings);

            if (i % 13 == 0)
            {
                checks.NotifyTimeSkip((uint)rng.Next(0, 10000), serverTime, findings);
            }

            if (i % 17 == 0)
            {
                checks.NotifyAcknowledgement(unchecked(clientTime - (uint)rng.Next(0, 1000)), serverTime, findings);
            }

            if (i % 29 == 0)
            {
                checks.NotifyServerRelocation();
            }

            foreach (AntiCheatFinding finding in findings)
            {
                Assert.False(float.IsNaN(finding.Weight));
                Assert.False(float.IsInfinity(finding.Weight));
                float total = scores.Add(1, finding.Weight, serverTime, options.DecayPerSecond);
                Assert.False(float.IsNaN(total));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_BanCommandText_DurationAndArgExtractionAndIpNormalization_NeverThrow(int seed)
    {
        var rng = new Random(seed);
        const string alphabet = "0123456789dhmsDHMS -_\"'abcdefghijklmnopqrstuvwxyz.:[]/";

        for (int i = 0; i < 250; i++)
        {
            int len = rng.Next(0, 64);
            char[] chars = new char[len];
            for (int c = 0; c < len; c++)
            {
                chars[c] = alphabet[rng.Next(alphabet.Length)];
            }

            string input = new(chars);
            _ = BanCommandText.IsWellFormedDuration(input);
            _ = BanCommandText.TryTimeStringToSecs(input, out _);
            _ = BanCommandText.TimeStringToSecs(input);
            _ = BanCommandText.SecsToTimeString((ulong)rng.NextInt64(0, 10_000_000));

            string rest1 = input;
            _ = BanCommandText.ExtractQuotedOrLiteralArg(ref rest1);

            string rest2 = input;
            _ = BanCommandText.ExtractLiteralArg(ref rest2);

            _ = AccountBanEvaluator.NormalizeIp(input);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_ClusterGatewayWireAndTransferValidator_RoundtripAndRejectCorruptedFrames(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 200; i++)
        {
            int len = rng.Next(0, 256);
            byte[] corrupted = new byte[len];
            rng.NextBytes(corrupted);

            // Arbitrary bytes either decode cleanly or throw InvalidDataException
            TryDecode(() => _ = GatewaySessionWire.DecodeOpen(corrupted));
            TryDecode(() => _ = GatewaySessionWire.DecodeOpenReply(corrupted));
            TryDecode(() => _ = GatewaySessionWire.DecodeClose(corrupted));
            TryDecode(() => _ = GatewaySessionWire.DecodePacket(corrupted));
            TryDecode(() => _ = GatewaySessionWire.DecodeNonceFrame(corrupted, "fuzz"));

            // Valid roundtrip for ClusterSessionPacket
            int payloadLen = rng.Next(0, 128);
            byte[] payload = new byte[payloadLen];
            rng.NextBytes(payload);
            var pkt = new ClusterSessionPacket(
                SessionGeneration: rng.NextInt64(1, long.MaxValue),
                AccountId: rng.Next(1, int.MaxValue),
                CharacterGuid: (ulong)rng.NextInt64(0, long.MaxValue),
                OwnerEpoch: rng.NextInt64(1, long.MaxValue),
                Sequence: rng.NextInt64(1, long.MaxValue),
                Opcode: (uint)rng.Next(0, 1000),
                Payload: payload);

            byte[] frame = GatewaySessionWire.EncodePacket(GatewayWorkerFrameType.SessionClientPacket, pkt);
            ClusterSessionPacket decoded = GatewaySessionWire.DecodePacket(frame.AsMemory(1));
            Assert.Equal(pkt.SessionGeneration, decoded.SessionGeneration);
            Assert.Equal(pkt.AccountId, decoded.AccountId);
            Assert.Equal(pkt.CharacterGuid, decoded.CharacterGuid);
            Assert.Equal(pkt.OwnerEpoch, decoded.OwnerEpoch);
            Assert.Equal(pkt.Sequence, decoded.Sequence);
            Assert.Equal(pkt.Opcode, decoded.Opcode);
            Assert.Equal(payload, decoded.Payload.ToArray());

            // PlayerTransferValidator null/blocker checks
            Assert.False(PlayerTransferValidator.ValidateBundle(null).IsValid);
            Assert.True(PlayerTransferValidator.ValidateBlockers(null).IsValid);

            // ModuleAllowlist check with null/empty path vs nonexistent path
            string hex = Convert.ToHexString(corrupted[..Math.Min(corrupted.Length, 32)]);
            Assert.True(ModuleAllowlist.Check(null, hex).Allowed);
            Assert.False(ModuleAllowlist.Check(Path.Combine(Path.GetTempPath(), "missing-allowlist-" + i + ".txt"), hex).Allowed);
        }

        static void TryDecode(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
            }
        }
    }
}
