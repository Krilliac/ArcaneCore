using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Transfer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

public sealed class PlayerTransferCoordinatorTests
{
    private sealed class FakeWorkerNode : ITransferSourceWorker, ITransferTargetWorker
    {
        private readonly Dictionary<ulong, PlayerTransferBundle> _activePlayers = [];
        private readonly Dictionary<ulong, PlayerTransferBundle> _suspendedPlayers = [];
        private readonly Dictionary<Guid, PlayerTransferBundle> _passiveShadows = [];
        private readonly HashSet<Guid> _reservedSlots = [];
        private readonly Dictionary<ulong, CharacterOwnershipToken> _heldTokens = [];

        public FakeWorkerNode(string id, Guid? incarnation = null)
        {
            WorkerId = new WorkerNodeId(id);
            Incarnation = incarnation ?? Guid.NewGuid();
        }

        public WorkerNodeId WorkerId { get; }

        public Guid Incarnation { get; }

        public bool RejectReservation { get; set; }

        public Action? OnDualActiveCheck { get; set; }

        public bool IsPlayerActive(ulong characterGuid) => _activePlayers.ContainsKey(characterGuid);

        public bool IsPlayerSuspended(ulong characterGuid) => _suspendedPlayers.ContainsKey(characterGuid);

        public bool HasPassiveShadow(Guid transferId) => _passiveShadows.ContainsKey(transferId);

        public bool HasReservedSlot(Guid transferId) => _reservedSlots.Contains(transferId);

        public PlayerTransferBundle? GetActiveBundle(ulong characterGuid) =>
            _activePlayers.TryGetValue(characterGuid, out PlayerTransferBundle? bundle) ? bundle : null;

        public CharacterOwnershipToken? GetHeldToken(ulong characterGuid) =>
            _heldTokens.TryGetValue(characterGuid, out CharacterOwnershipToken? token) ? token : null;

        public void SeedActivePlayer(PlayerTransferBundle initialBundle, CharacterOwnershipToken token)
        {
            _activePlayers[initialBundle.CharacterGuid] = initialBundle;
            _heldTokens[initialBundle.CharacterGuid] = token;
        }

        public void SimulateTargetProcessRestartClearingMemory()
        {
            _passiveShadows.Clear();
            _reservedSlots.Clear();
        }

        public Task<bool> TryReserveSlotAsync(
            Guid transferId,
            ulong characterGuid,
            uint targetMapId,
            uint targetInstanceId,
            CancellationToken cancellationToken = default)
        {
            if (RejectReservation)
                return Task.FromResult(false);

            _reservedSlots.Add(transferId);
            return Task.FromResult(true);
        }

        public Task<PlayerTransferBundle> SuspendAndCaptureAsync(
            Guid transferId,
            ulong characterGuid,
            WorkerNodeId targetWorkerId,
            uint targetMapId,
            uint targetInstanceId,
            TransferPosition targetPosition,
            CancellationToken cancellationToken = default)
        {
            if (!_activePlayers.Remove(characterGuid, out PlayerTransferBundle? live))
                throw new InvalidOperationException($"Character {characterGuid} is not active on source worker {WorkerId}.");

            PlayerTransferBundle captured = live with
            {
                TransferId = transferId,
                SourceWorkerId = WorkerId,
                TargetWorkerId = targetWorkerId,
                TargetMapId = targetMapId,
                TargetInstanceId = targetInstanceId,
                TargetPosition = targetPosition,
            };

            _suspendedPlayers[characterGuid] = captured;
            OnDualActiveCheck?.Invoke();
            return Task.FromResult(captured);
        }

        public Task InstallPassiveShadowAsync(
            Guid transferId,
            PlayerTransferBundle bundle,
            CancellationToken cancellationToken = default)
        {
            _reservedSlots.Add(transferId);
            _passiveShadows[transferId] = bundle;
            OnDualActiveCheck?.Invoke();
            return Task.CompletedTask;
        }

        public Task DiscardPassiveShadowAsync(
            Guid transferId,
            ulong characterGuid,
            string abortReason,
            CancellationToken cancellationToken = default)
        {
            _passiveShadows.Remove(transferId);
            _reservedSlots.Remove(transferId);
            OnDualActiveCheck?.Invoke();
            return Task.CompletedTask;
        }

        public Task ResumeAfterAbortAsync(
            Guid transferId,
            ulong characterGuid,
            CharacterOwnershipToken ownershipToken,
            string abortReason,
            CancellationToken cancellationToken = default)
        {
            if (_suspendedPlayers.Remove(characterGuid, out PlayerTransferBundle? suspended))
            {
                _activePlayers[characterGuid] = suspended;
                _heldTokens[characterGuid] = ownershipToken;
            }

            OnDualActiveCheck?.Invoke();
            return Task.CompletedTask;
        }

        public Task RetireSourcePlayerAsync(
            Guid transferId,
            ulong characterGuid,
            CharacterOwnershipToken supersededToken,
            CancellationToken cancellationToken = default)
        {
            _activePlayers.Remove(characterGuid);
            _suspendedPlayers.Remove(characterGuid);
            _heldTokens.Remove(characterGuid);
            OnDualActiveCheck?.Invoke();
            return Task.CompletedTask;
        }

        public Task ActivateTargetPlayerAsync(
            Guid transferId,
            PlayerTransferBundle bundle,
            CharacterOwnershipToken targetOwnershipToken,
            CancellationToken cancellationToken = default)
        {
            _passiveShadows.Remove(transferId);
            _reservedSlots.Remove(transferId);

            PlayerTransferBundle activated = bundle with
            {
                SourceOwnershipToken = targetOwnershipToken,
                SourceMapId = bundle.TargetMapId,
                SourceInstanceId = bundle.TargetInstanceId,
                SourcePosition = bundle.TargetPosition,
            };

            _activePlayers[bundle.CharacterGuid] = activated;
            _heldTokens[bundle.CharacterGuid] = targetOwnershipToken;
            OnDualActiveCheck?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGatewayRouter : ITransferGatewayRouter
    {
        public Dictionary<ulong, WorkerNodeId> ActiveRoutes { get; } = [];

        public HashSet<Guid> StagedWorldPorts { get; } = [];

        public Dictionary<ulong, CharacterOwnershipToken> RoutedOwnershipTokens { get; } = [];

        public Task StageWorldPortAsync(
            Guid transferId,
            ulong characterGuid,
            long sessionGeneration,
            WorkerNodeId sourceWorkerId,
            WorkerNodeId targetWorkerId,
            uint targetMapId,
            uint targetInstanceId,
            TransferPosition targetPosition,
            CancellationToken cancellationToken = default)
        {
            StagedWorldPorts.Add(transferId);
            return Task.CompletedTask;
        }

        public Task CancelStagedWorldPortAsync(
            Guid transferId,
            ulong characterGuid,
            long sessionGeneration,
            WorkerNodeId restoredWorkerId,
            string abortReason,
            CancellationToken cancellationToken = default)
        {
            StagedWorldPorts.Remove(transferId);
            ActiveRoutes[characterGuid] = restoredWorkerId;
            return Task.CompletedTask;
        }

        public Task CommitRouteSwitchAsync(
            Guid transferId,
            ulong characterGuid,
            long sessionGeneration,
            WorkerNodeId targetWorkerId,
            CharacterOwnershipToken targetOwnershipToken,
            CancellationToken cancellationToken = default)
        {
            StagedWorldPorts.Remove(transferId);
            ActiveRoutes[characterGuid] = targetWorkerId;
            RoutedOwnershipTokens[characterGuid] = targetOwnershipToken;
            return Task.CompletedTask;
        }
    }

    private static (
        ClusterMapPlacementTable MapTable,
        InMemoryTransferDecisionStore DecisionStore,
        FakeGatewayRouter Gateway,
        FakeWorkerNode SourceWorker,
        FakeWorkerNode TargetWorker,
        PlayerTransferCoordinator Coordinator,
        PlayerTransferRequest Request,
        PlayerTransferBundle SeededBundle) CreateFixture(TransferActiveBlockers? blockers = null)
    {
        var mapTable = new ClusterMapPlacementTable();
        var decisionStore = new InMemoryTransferDecisionStore();
        var gateway = new FakeGatewayRouter();
        var sourceWorker = new FakeWorkerNode("worker-ek");
        var targetWorker = new FakeWorkerNode("worker-kalimdor");

        void AssertNeverDualActive()
        {
            bool sourceActive = sourceWorker.IsPlayerActive(4242UL);
            bool targetActive = targetWorker.IsPlayerActive(4242UL);
            Assert.False(sourceActive && targetActive, "Dual-active invariant violated: player was simultaneously active on both source and target workers!");
        }

        sourceWorker.OnDualActiveCheck = AssertNeverDualActive;
        targetWorker.OnDualActiveCheck = AssertNeverDualActive;

        mapTable.RegisterWorker(sourceWorker.WorkerId, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 50, incarnation: sourceWorker.Incarnation);
        mapTable.RegisterWorker(targetWorker.WorkerId, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 40, incarnation: targetWorker.Incarnation);
        mapTable.AssignStaticMap(0, sourceWorker.WorkerId);
        mapTable.AssignStaticMap(1, targetWorker.WorkerId);

        var sourceToken = new CharacterOwnershipToken("char:4242", 7, sourceWorker.Incarnation);
        decisionStore.SeedCharacterOwnership(4242UL, sourceToken);
        gateway.ActiveRoutes[4242UL] = sourceWorker.WorkerId;
        gateway.RoutedOwnershipTokens[4242UL] = sourceToken;

        Guid transferId = Guid.NewGuid();
        PlayerTransferBundle seededBundle = PlayerTransferValidatorTests.CreateSampleValidBundle(
            transferId: transferId,
            characterGuid: 4242UL,
            sourceWorker: sourceWorker.WorkerId,
            targetWorker: targetWorker.WorkerId,
            sourceToken: sourceToken,
            blockers: blockers);

        sourceWorker.SeedActivePlayer(seededBundle, sourceToken);

        var coordinator = new PlayerTransferCoordinator(
            mapTable,
            decisionStore,
            gateway,
            [sourceWorker],
            [targetWorker]);

        var request = new PlayerTransferRequest(
            TransferId: transferId,
            CharacterGuid: 4242UL,
            AccountId: seededBundle.AccountId,
            SessionGeneration: seededBundle.SessionGeneration,
            SourceWorkerId: sourceWorker.WorkerId,
            SourceMapId: 0,
            SourceInstanceId: 0,
            TargetMapId: 1,
            TargetInstanceId: 0,
            TargetPosition: seededBundle.TargetPosition,
            SourceOwnershipToken: sourceToken);

        return (mapTable, decisionStore, gateway, sourceWorker, targetWorker, coordinator, request, seededBundle);
    }

    [Fact]
    public async Task ExecuteTransferAsync_HappyPath6Stages_PreservesFullBundle_AdvancesOwnershipEpoch_AndNeverDualActivates()
    {
        var (_, decisionStore, gateway, sourceWorker, targetWorker, coordinator, request, seededBundle) = CreateFixture();
        var visitedStages = new List<PlayerTransferStage>();

        PlayerTransferOutcome outcome = await coordinator.ExecuteTransferAsync(
            request,
            afterStageHook: (stage, record, _) =>
            {
                visitedStages.Add(stage);
                Assert.False(sourceWorker.IsPlayerActive(request.CharacterGuid) && targetWorker.IsPlayerActive(request.CharacterGuid));

                if (stage is PlayerTransferStage.SuspendAndCapture or PlayerTransferStage.InstallPassive or PlayerTransferStage.StageWorldPort)
                {
                    Assert.False(sourceWorker.IsPlayerActive(request.CharacterGuid));
                    Assert.True(sourceWorker.IsPlayerSuspended(request.CharacterGuid));
                    Assert.False(targetWorker.IsPlayerActive(request.CharacterGuid));
                }

                if (stage == PlayerTransferStage.InstallPassive)
                {
                    Assert.True(targetWorker.HasPassiveShadow(request.TransferId));
                }

                if (stage == PlayerTransferStage.CommitDecision)
                {
                    Assert.Equal(PlayerTransferDecisionState.Committed, record.DecisionState);
                    Assert.NotNull(record.TargetOwnershipToken);
                    Assert.Equal(request.SourceOwnershipToken.Epoch + 1, record.TargetOwnershipToken!.Epoch);
                }

                return Task.CompletedTask;
            });

        Assert.True(outcome.Succeeded);
        Assert.Equal(PlayerTransferStage.ActivateAndRetire, outcome.FinalStage);
        Assert.Equal(PlayerTransferDecisionState.Completed, outcome.DecisionState);
        Assert.Equal(
            [
                PlayerTransferStage.Reserve,
                PlayerTransferStage.SuspendAndCapture,
                PlayerTransferStage.InstallPassive,
                PlayerTransferStage.StageWorldPort,
                PlayerTransferStage.CommitDecision,
                PlayerTransferStage.ActivateAndRetire,
            ],
            visitedStages);

        // Source is retired; Target is active with incremented epoch
        Assert.False(sourceWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(sourceWorker.IsPlayerSuspended(request.CharacterGuid));
        Assert.True(targetWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(targetWorker.HasPassiveShadow(request.TransferId));

        CharacterOwnershipToken? activeOwner = decisionStore.GetActiveOwnership(request.CharacterGuid);
        Assert.NotNull(activeOwner);
        Assert.Equal(request.SourceOwnershipToken.Epoch + 1, activeOwner!.Epoch);
        Assert.Equal(targetWorker.Incarnation, activeOwner.Incarnation);
        Assert.Equal(targetWorker.WorkerId, gateway.ActiveRoutes[request.CharacterGuid]);
        Assert.Equal(activeOwner, gateway.RoutedOwnershipTokens[request.CharacterGuid]);

        // Stale source token is fenced out by the decision store
        Assert.Throws<CharacterOwnershipRejectedException>(() =>
            decisionStore.EnsureOwnershipActive(request.CharacterGuid, request.SourceOwnershipToken));

        // Verify full PlayerTransferBundle fidelity on the activated target worker
        PlayerTransferBundle? activated = targetWorker.GetActiveBundle(request.CharacterGuid);
        Assert.NotNull(activated);
        Assert.Equal(seededBundle.Health, activated!.Health);
        Assert.Equal(seededBundle.Powers, activated.Powers);
        Assert.Equal(seededBundle.Level, activated.Level);
        Assert.Equal(seededBundle.Xp, activated.Xp);
        Assert.Equal(seededBundle.Money, activated.Money);
        Assert.Equal(seededBundle.ComboPoints, activated.ComboPoints);
        Assert.Equal(seededBundle.ComboTargetGuid, activated.ComboTargetGuid);
        Assert.Equal(seededBundle.AckCounters, activated.AckCounters);
        Assert.Equal(seededBundle.SpeedRates, activated.SpeedRates);
        Assert.Equal(seededBundle.PvpState, activated.PvpState);
        Assert.Equal(seededBundle.RestState, activated.RestState);
        Assert.Equal(seededBundle.Items.Count, activated.Items.Count);
        Assert.Equal(seededBundle.Items[0].Enchantments[0].EnchantId, activated.Items[0].Enchantments[0].EnchantId);
        Assert.Equal(seededBundle.Quests[0].RemainingTimerMs, activated.Quests[0].RemainingTimerMs);
        Assert.Equal(seededBundle.Quests[0].ObjectiveCounters, activated.Quests[0].ObjectiveCounters);
        Assert.Equal(seededBundle.Cooldowns[0].RemainingDurationMs, activated.Cooldowns[0].RemainingDurationMs);
        Assert.Equal(seededBundle.Auras.Count, activated.Auras.Count);
        Assert.Equal(seededBundle.Auras[0].RemainingDurationMs, activated.Auras[0].RemainingDurationMs);
        Assert.Equal(seededBundle.Auras[1].BasePoints, activated.Auras[1].BasePoints);
        Assert.Equal(seededBundle.HomeBind, activated.HomeBind);
        Assert.Equal(seededBundle.TaxiNodesMask, activated.TaxiNodesMask);
        Assert.Equal(seededBundle.PresenceVersion, activated.PresenceVersion);
    }

    [Theory]
    [InlineData(PlayerTransferStage.Reserve)]
    [InlineData(PlayerTransferStage.SuspendAndCapture)]
    [InlineData(PlayerTransferStage.InstallPassive)]
    [InlineData(PlayerTransferStage.StageWorldPort)]
    public async Task PreCommitAbort_AtAnyPreCommitStage_RestoresSourceWorker_DiscardsTargetPassiveState_AndKeepsSourceEpoch(
        PlayerTransferStage abortAtStage)
    {
        var (_, decisionStore, gateway, sourceWorker, targetWorker, coordinator, request, _) = CreateFixture();

        PlayerTransferOutcome outcome = await coordinator.ExecuteTransferAsync(
            request,
            afterStageHook: async (stage, _, ct) =>
            {
                if (stage == abortAtStage)
                {
                    bool aborted = await coordinator.TryAbortTransferAsync(
                        request.TransferId,
                        $"abort-at-{abortAtStage}",
                        ct);
                    Assert.True(aborted);
                }
            });

        Assert.False(outcome.Succeeded);
        Assert.Equal(PlayerTransferStage.Aborted, outcome.FinalStage);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);

        // Player is back to active on the source worker under the original ownership token
        Assert.True(sourceWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(sourceWorker.IsPlayerSuspended(request.CharacterGuid));
        Assert.Equal(request.SourceOwnershipToken, sourceWorker.GetHeldToken(request.CharacterGuid));
        Assert.Equal(request.SourceOwnershipToken, decisionStore.GetActiveOwnership(request.CharacterGuid));

        // Target worker has no passive shadow, no reservation, and is not active
        Assert.False(targetWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(targetWorker.HasPassiveShadow(request.TransferId));
        Assert.False(targetWorker.HasReservedSlot(request.TransferId));

        // Gateway has no staged world port and routes to source worker
        Assert.DoesNotContain(request.TransferId, gateway.StagedWorldPorts);
        Assert.Equal(sourceWorker.WorkerId, gateway.ActiveRoutes[request.CharacterGuid]);
    }

    [Fact]
    public async Task PostCommitAbort_IsStrictlyRejected_AndCrashAfterCommitDecision_RollsForwardToTargetWorker()
    {
        var (_, decisionStore, gateway, sourceWorker, targetWorker, coordinator, request, _) = CreateFixture();

        // Simulate a crash immediately after Stage 5 (CommitDecision) before Stage 6 (ActivateAndRetire) runs.
        var simulatedCrash = new InvalidOperationException("simulated-worker-or-coordinator-crash-after-commit");

        InvalidOperationException caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteTransferAsync(
                request,
                afterStageHook: async (stage, record, ct) =>
                {
                    if (stage == PlayerTransferStage.CommitDecision)
                    {
                        // Verify that aborting after CommitDecision is strictly forbidden!
                        Assert.False(await coordinator.TryAbortTransferAsync(request.TransferId, "illegal-post-commit-abort", ct));
                        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                            coordinator.AbortTransferAsync(request.TransferId, "illegal-post-commit-abort", ct));
                        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                            decisionStore.AbortPreCommitAsync(request.TransferId, "illegal-post-commit-abort", ct));

                        // Also simulate target worker losing its in-memory passive shadow during the crash
                        targetWorker.SimulateTargetProcessRestartClearingMemory();
                        throw simulatedCrash;
                    }
                }));

        Assert.Same(simulatedCrash, caught);

        // Before recovery runs: decision is Committed, ownership token is already at Epoch + 1, and neither worker is dual-active.
        TransferDecisionRecord? committedRecord = await decisionStore.GetDecisionAsync(request.TransferId);
        Assert.NotNull(committedRecord);
        Assert.Equal(PlayerTransferDecisionState.Committed, committedRecord!.DecisionState);
        Assert.Equal(PlayerTransferStage.CommitDecision, committedRecord.CurrentStage);
        Assert.False(sourceWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(targetWorker.IsPlayerActive(request.CharacterGuid));

        // Now run crash recovery: MUST roll forward to target worker and retire source worker!
        TransferDecisionRecord recovered = await coordinator.RecoverTransferAsync(request.TransferId);

        Assert.Equal(PlayerTransferDecisionState.Completed, recovered.DecisionState);
        Assert.Equal(PlayerTransferStage.ActivateAndRetire, recovered.CurrentStage);
        Assert.False(sourceWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(sourceWorker.IsPlayerSuspended(request.CharacterGuid));
        Assert.True(targetWorker.IsPlayerActive(request.CharacterGuid));
        Assert.Equal(targetWorker.WorkerId, gateway.ActiveRoutes[request.CharacterGuid]);
        Assert.Equal(committedRecord.TargetOwnershipToken, targetWorker.GetHeldToken(request.CharacterGuid));

        // Post-completion abort is still rejected
        Assert.False(await coordinator.TryAbortTransferAsync(request.TransferId, "after-complete"));
    }

    [Fact]
    public async Task CrashBeforeCommitDecision_RecoverTransferAsync_RollsBackToSourceWorker()
    {
        var (_, decisionStore, gateway, sourceWorker, targetWorker, coordinator, request, _) = CreateFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteTransferAsync(
                request,
                afterStageHook: (stage, _, _) =>
                {
                    if (stage == PlayerTransferStage.StageWorldPort)
                        throw new InvalidOperationException("simulated-pre-commit-crash");
                    return Task.CompletedTask;
                }));

        TransferDecisionRecord? preRecovery = await decisionStore.GetDecisionAsync(request.TransferId);
        Assert.NotNull(preRecovery);
        Assert.Equal(PlayerTransferDecisionState.PreCommit, preRecovery!.DecisionState);
        Assert.Equal(PlayerTransferStage.StageWorldPort, preRecovery.CurrentStage);

        TransferDecisionRecord rolledBack = await coordinator.RecoverTransferAsync(request.TransferId);
        Assert.Equal(PlayerTransferDecisionState.Aborted, rolledBack.DecisionState);
        Assert.True(sourceWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(targetWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(targetWorker.HasPassiveShadow(request.TransferId));
        Assert.Equal(sourceWorker.WorkerId, gateway.ActiveRoutes[request.CharacterGuid]);
        Assert.Equal(request.SourceOwnershipToken, decisionStore.GetActiveOwnership(request.CharacterGuid));
    }

    [Fact]
    public async Task ExecuteTransferAsync_AbortsAndRestoresSource_WhenPlayerHasActiveTradeOrLootWindow()
    {
        var (_, decisionStore, _, sourceWorker, targetWorker, coordinator, request, _) = CreateFixture(
            blockers: new TransferActiveBlockers(HasActiveTradeWindow: true));

        PlayerTransferOutcome outcome = await coordinator.ExecuteTransferAsync(request);

        Assert.False(outcome.Succeeded);
        Assert.Equal(PlayerTransferValidator.ErrorActiveTradeWindow, outcome.FailureCode);
        Assert.Equal(PlayerTransferDecisionState.Aborted, outcome.DecisionState);
        Assert.True(sourceWorker.IsPlayerActive(request.CharacterGuid));
        Assert.False(targetWorker.IsPlayerActive(request.CharacterGuid));
        Assert.Equal(request.SourceOwnershipToken, decisionStore.GetActiveOwnership(request.CharacterGuid));
    }
}
