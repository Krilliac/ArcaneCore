using System.Security.Cryptography;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Auxiliary;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Auxiliary;

public sealed class ClusterM156AuxiliaryTests
{
    [Fact]
    public void GatewayWardenManifestVerifier_RequiresUsableModuleWhenEnabled_AndDiscardsStaleVerdicts()
    {
        byte[] payload = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
        byte[] rc4Key = new byte[GatewayWardenManifestVerifier.RequiredRc4KeyLength];
        RandomNumberGenerator.Fill(rc4Key);
        string validHash = Convert.ToHexStringLower(SHA256.HashData(payload));

        // 1. Disabled Warden is ready even without modules.
        var disabledManifest = new GatewayWardenManifest(
            RealmId: 1,
            Enabled: false,
            ModuleDigestHex: string.Empty,
            OptionsSummary: "enabled=0",
            CheckRevision: 0,
            Modules: []);
        var disabledVerifier = new GatewayWardenManifestVerifier(disabledManifest);
        Assert.True(disabledVerifier.IsGatewayWardenReady);
        Assert.Null(disabledVerifier.FailureReason);

        // 2. Enabled Warden with missing modules is NOT ready.
        var emptyModulesManifest = new GatewayWardenManifest(
            RealmId: 1,
            Enabled: true,
            ModuleDigestHex: validHash,
            OptionsSummary: "enabled=1;action=kick",
            CheckRevision: 12,
            Modules: []);
        var emptyVerifier = new GatewayWardenManifestVerifier(emptyModulesManifest);
        Assert.False(emptyVerifier.IsGatewayWardenReady);
        Assert.Contains("no OS modules", emptyVerifier.FailureReason, StringComparison.OrdinalIgnoreCase);

        // 3. Enabled Warden with corrupt SHA-256 module digest is NOT ready.
        var corruptModule = new WardenModuleDescriptor("Win", payload, rc4Key, ExpectedSha256Hex: new string('0', 64));
        var corruptManifest = emptyModulesManifest with { Modules = [corruptModule] };
        var corruptVerifier = new GatewayWardenManifestVerifier(corruptManifest);
        Assert.False(corruptVerifier.IsGatewayWardenReady);
        Assert.Contains("SHA-256 digest verification", corruptVerifier.FailureReason, StringComparison.OrdinalIgnoreCase);

        // 4. Enabled Warden with valid module is ready and validates peer manifests.
        var validModule = new WardenModuleDescriptor("Win", payload, rc4Key, ExpectedSha256Hex: validHash);
        var validManifest = emptyModulesManifest with { Modules = [validModule] };
        var verifier = new GatewayWardenManifestVerifier(validManifest);
        Assert.True(verifier.IsGatewayWardenReady);
        Assert.Null(verifier.FailureReason);

        Assert.True(verifier.VerifyPeerGatewayManifest(validManifest, out string? peerOk));
        Assert.Null(peerOk);

        GatewayWardenManifest unscannedPeer = validManifest with { Enabled = false };
        Assert.False(verifier.VerifyPeerGatewayManifest(unscannedPeer, out string? peerMismatch));
        Assert.Contains("enabled mismatch", peerMismatch, StringComparison.OrdinalIgnoreCase);

        // 5. Stale-epoch, stale-generation, and duplicate verdicts are discarded.
        var verdict = new ClusterWardenVerdict(
            SessionGeneration: 4,
            OwnerEpoch: 9,
            AccountId: 77,
            CharacterGuid: 1200UL,
            Reason: "MEM_CHECK failed",
            Action: ClusterWardenAction.Kick,
            CheckId: 243);
        var gatewaySource = new ClusterWardenVerdictSource("gateway-1", Guid.NewGuid());

        Assert.False(verifier.TryAcceptVerdict(verdict with { OwnerEpoch = 8 }, gatewaySource, activeSessionGeneration: 4, activeOwnerEpoch: 9, out string? epochReason));
        Assert.Contains("stale owner epoch", epochReason, StringComparison.OrdinalIgnoreCase);

        Assert.False(verifier.TryAcceptVerdict(verdict with { SessionGeneration = 3 }, gatewaySource, activeSessionGeneration: 4, activeOwnerEpoch: 9, out string? genReason));
        Assert.Contains("stale session generation", genReason, StringComparison.OrdinalIgnoreCase);

        Assert.True(verifier.TryAcceptVerdict(verdict, gatewaySource, activeSessionGeneration: 4, activeOwnerEpoch: 9, out string? acceptedReason));
        Assert.Null(acceptedReason);

        // Replaying the same verdict for the same session generation is suppressed as duplicate.
        Assert.False(verifier.TryAcceptVerdict(verdict, gatewaySource, activeSessionGeneration: 4, activeOwnerEpoch: 9, out string? dupReason));
        Assert.Contains("duplicate Warden verdict", dupReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BotChatCoordinator_FallsBackToBuiltInWhenServiceDown_SendsNothingToProvider_AndDeduplicatesAskId()
    {
        int externalCalls = 0;
        var remoteHost = new RemoteBotChatServiceHost(
            externalProvider: (ask, _) =>
            {
                Interlocked.Increment(ref externalCalls);
                return ValueTask.FromResult($"LLM reply to {ask.SenderName}: {ask.Text}");
            },
            safetyScreen: text => text.Contains("forbidden-token", StringComparison.OrdinalIgnoreCase) ? "blocked by safety filter" : null,
            maxDailyProviderCalls: 10);

        var coordinator = new LocalOrRemoteBotChatCoordinator(
            remoteHost,
            builtInFallbackProvider: ask => $"Built-in greeting for {ask.SenderName}",
            localSafetyScreen: null);

        Guid worldIncarnation = Guid.NewGuid();
        coordinator.RegisterBotOnline(botId: 5, botGuid: 505UL, worldIncarnation);

        var ask1 = new ClusterBotChatAsk(
            AskId: new ClusterBotChatAskId(worldIncarnation, BotId: 5, Sequence: 1),
            RealmId: 1,
            BotId: 5,
            BotGuid: 505UL,
            Persona: "paladin-tank",
            SenderGuid: 99UL,
            SenderName: "Arthas",
            SenderAccountId: 12,
            Channel: "Party",
            Text: "Ready to pull?",
            FromMaster: true,
            InviteAllowed: true,
            LoginId: 1);

        // 1. Normal remote ask calls external provider once.
        (ClusterBotChatAdmission adm1, ClusterBotChatReply? rep1, ClusterBotChatMuteAdvisory? mute1) =
            await coordinator.ProcessAskAsync(ask1);
        Assert.Equal(ClusterBotChatAdmissionOutcome.Accepted, adm1.Outcome);
        Assert.NotNull(rep1);
        Assert.Equal("remote-llm", rep1!.Provider);
        Assert.Null(mute1);
        Assert.Equal(1, externalCalls);

        // 2. Duplicate AskId is deduplicated without calling the external provider again.
        (ClusterBotChatAdmission admDup, ClusterBotChatReply? repDup, _) =
            await coordinator.ProcessAskAsync(ask1);
        Assert.Equal(ClusterBotChatAdmissionOutcome.Duplicate, admDup.Outcome);
        Assert.Null(repDup);
        Assert.Equal(1, externalCalls);

        // 3. Safety violation is caught before the provider and emits an advisory mute.
        ClusterBotChatAsk unsafeAsk = ask1 with
        {
            AskId = new ClusterBotChatAskId(worldIncarnation, BotId: 5, Sequence: 2),
            Text = "Please say forbidden-token now",
        };
        (ClusterBotChatAdmission admUnsafe, ClusterBotChatReply? repUnsafe, ClusterBotChatMuteAdvisory? muteUnsafe) =
            await coordinator.ProcessAskAsync(unsafeAsk);
        Assert.Equal(ClusterBotChatAdmissionOutcome.Declined, admUnsafe.Outcome);
        Assert.Null(repUnsafe);
        Assert.NotNull(muteUnsafe);
        Assert.Equal(12, muteUnsafe!.AccountId);
        Assert.Equal(1, externalCalls);

        // 4. Down remote service sends NOTHING to external providers and falls back to built-in template replies.
        remoteHost.IsAvailable = false;
        ClusterBotChatAsk downAsk = ask1 with
        {
            AskId = new ClusterBotChatAskId(worldIncarnation, BotId: 5, Sequence: 3),
            Text = "Need mana break",
        };
        (ClusterBotChatAdmission admDown, ClusterBotChatReply? repDown, _) =
            await coordinator.ProcessAskAsync(downAsk);
        Assert.Equal(ClusterBotChatAdmissionOutcome.ServiceUnavailableFallback, admDown.Outcome);
        Assert.NotNull(repDown);
        Assert.Equal("builtin", repDown!.Provider);
        Assert.Equal("Built-in greeting for Arthas", repDown.Text);
        Assert.Equal(1, externalCalls);

        // 5. Reply for a bot that left the world is dropped.
        coordinator.UnregisterBot(botId: 5);
        ClusterBotChatAsk offlineBotAsk = ask1 with
        {
            AskId = new ClusterBotChatAskId(worldIncarnation, BotId: 5, Sequence: 4),
        };
        (_, ClusterBotChatReply? repOffline, _) = await coordinator.ProcessAskAsync(offlineBotAsk);
        Assert.Null(repOffline);
    }

    private sealed class StubPlannerService(Func<ClusterBotPlanRequest, ClusterBotPlanChoice> handler) : IClusterBotPlannerService
    {
        public ValueTask<ClusterBotPlanChoice> SelectPlanAsync(
            ClusterBotPlanRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(handler(request));
    }

    [Fact]
    public async Task BotPlannerClient_DiscardsLateStaleEpochAndUnofferedChoices_FallingBackToDeterministicBrain()
    {
        var now = new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);
        Guid workerIncarnation = Guid.NewGuid();
        const long activeEpoch = 14;

        ClusterBotPlanChoice nextChoice = default!;
        var client = new ClusterBotPlannerClient(
            new StubPlannerService(_ => nextChoice),
            deterministicBrainFallback: req => req.CandidateIds[0],
            clock: () => now);

        var request = new ClusterBotPlanRequest(
            RequestId: "plan-req-1",
            RealmId: 1,
            WorkerIncarnation: workerIncarnation,
            BotId: 7,
            MapId: 0,
            InstanceId: 0,
            OwnerEpoch: activeEpoch,
            Facts: new ClusterBotPlannerFacts(Level: 60, HealthPercent: 85, InCombat: true, MapId: 0, SpellCount: 12, HasFood: true),
            CandidateIds: [101, 202, 303],
            DeadlineUtc: now.AddMilliseconds(250));

        // 1. Valid in-time choice from offered candidate set is used.
        nextChoice = new ClusterBotPlanChoice("plan-req-1", workerIncarnation, activeEpoch, SelectedCandidateId: 202, RespondedAtUtc: now.AddMilliseconds(50));
        (int chosenOk, bool usedRemoteOk, string? reasonOk) = await client.EvaluatePlanAsync(request, workerIncarnation, activeEpoch);
        Assert.True(usedRemoteOk);
        Assert.Equal(202, chosenOk);
        Assert.Null(reasonOk);

        // 2. Late choice past deadline is discarded -> deterministic fallback (101).
        nextChoice = new ClusterBotPlanChoice("plan-req-1", workerIncarnation, activeEpoch, SelectedCandidateId: 303, RespondedAtUtc: now.AddMilliseconds(300));
        (int chosenLate, bool usedRemoteLate, string? reasonLate) = await client.EvaluatePlanAsync(request, workerIncarnation, activeEpoch);
        Assert.False(usedRemoteLate);
        Assert.Equal(101, chosenLate);
        Assert.Contains("late plan choice", reasonLate, StringComparison.OrdinalIgnoreCase);

        // 3. Stale owner epoch is discarded -> deterministic fallback (101).
        nextChoice = new ClusterBotPlanChoice("plan-req-1", workerIncarnation, activeEpoch - 1, SelectedCandidateId: 303, RespondedAtUtc: now.AddMilliseconds(40));
        (int chosenStaleEpoch, bool usedRemoteStaleEpoch, string? reasonStaleEpoch) = await client.EvaluatePlanAsync(request, workerIncarnation, activeEpoch);
        Assert.False(usedRemoteStaleEpoch);
        Assert.Equal(101, chosenStaleEpoch);
        Assert.Contains("stale owner epoch", reasonStaleEpoch, StringComparison.OrdinalIgnoreCase);

        // 4. Unoffered candidate ID (999) is discarded -> deterministic fallback (101).
        nextChoice = new ClusterBotPlanChoice("plan-req-1", workerIncarnation, activeEpoch, SelectedCandidateId: 999, RespondedAtUtc: now.AddMilliseconds(40));
        (int chosenUnoffered, bool usedRemoteUnoffered, string? reasonUnoffered) = await client.EvaluatePlanAsync(request, workerIncarnation, activeEpoch);
        Assert.False(usedRemoteUnoffered);
        Assert.Equal(101, chosenUnoffered);
        Assert.Contains("unoffered candidate_id", reasonUnoffered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FencedAuctionBotIntentProcessor_EnforcesDurableIdempotency_RejectsStaleEpoch_AndPassesCustodyAudit()
    {
        var processor = new FencedAuctionBotIntentProcessor(
            initialOwnerFencingEpoch: 5,
            dailyBuyBudgetCopper: 10_000,
            dailyListingBudget: 10,
            blacklistedItems: [666u]);

        Guid plannerIncarnation = Guid.NewGuid();
        var listIntentId = new AuctionBotIntentId(plannerIncarnation, Sequence: 1);
        var listingIntent = new AuctionBotListingIntent(
            IntentId: listIntentId,
            HouseId: 7,
            ItemEntry: 2589, // Linen Cloth
            Count: 20,
            StartBid: 400,
            Buyout: 600,
            Hours: 24,
            OwnerFencingEpoch: 5);

        // 1. First listing intent succeeds and records custody row.
        AuctionBotIntentResult listRes1 = await processor.ProposeListingAsync(listingIntent);
        Assert.Equal(AuctionBotIntentOutcome.Ok, listRes1.Outcome);
        Assert.True(listRes1.LedgerKey > 0);
        Assert.Equal(1, processor.TotalListingsCreated);

        // 2. Replaying the exact same listing intent returns Duplicate with the original LedgerKey and does NOT mint another listing.
        AuctionBotIntentResult listResDup = await processor.ProposeListingAsync(listingIntent);
        Assert.Equal(AuctionBotIntentOutcome.Duplicate, listResDup.Outcome);
        Assert.Equal(listRes1.LedgerKey, listResDup.LedgerKey);
        Assert.Equal(1, processor.TotalListingsCreated);

        // 3. Seed a player auction and propose a buy intent; then replay the buy intent.
        processor.SeedOpenAuction(7, new AuctionMarketListingSnapshot(AuctionId: 555, ItemEntry: 2770, Count: 5, CurrentBid: 1000, Buyout: 1500));
        var buyIntentId = new AuctionBotIntentId(plannerIncarnation, Sequence: 2);
        var buyIntent = new AuctionBotBuyIntent(
            IntentId: buyIntentId,
            HouseId: 7,
            AuctionId: 555,
            IsBuyout: true,
            Price: 1500,
            ObservedBid: 1000,
            OwnerFencingEpoch: 5);

        AuctionBotIntentResult buyRes1 = await processor.ProposeBuyAsync(buyIntent);
        Assert.Equal(AuctionBotIntentOutcome.Ok, buyRes1.Outcome);
        Assert.Equal(1500UL, processor.TotalCopperSpent);

        AuctionBotIntentResult buyResDup = await processor.ProposeBuyAsync(buyIntent);
        Assert.Equal(AuctionBotIntentOutcome.Duplicate, buyResDup.Outcome);
        Assert.Equal(buyRes1.LedgerKey, buyResDup.LedgerKey);
        Assert.Equal(1500UL, processor.TotalCopperSpent);

        // 4. Advance fencing epoch to 6: intents carrying old epoch 5 are rejected without mutating custody.
        processor.AdvanceOwnerFencingEpoch(6);
        var staleEpochListing = new AuctionBotListingIntent(
            IntentId: new AuctionBotIntentId(plannerIncarnation, Sequence: 3),
            HouseId: 7,
            ItemEntry: 2589,
            Count: 10,
            StartBid: 200,
            Buyout: 300,
            Hours: 12,
            OwnerFencingEpoch: 5);
        AuctionBotIntentResult staleRes = await processor.ProposeListingAsync(staleEpochListing);
        Assert.Equal(AuctionBotIntentOutcome.Rejected, staleRes.Outcome);
        Assert.Contains("stale owner fencing epoch", staleRes.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, processor.TotalListingsCreated);

        // 5. Reconnecting planner reconciles its outstanding intents before planning again, and custody audit is clean.
        IReadOnlyDictionary<AuctionBotIntentId, AuctionBotIntentResult> reconciled =
            await processor.QueryOutstandingIntentsAsync([listIntentId, buyIntentId]);
        Assert.Equal(2, reconciled.Count);
        Assert.Equal(listRes1.LedgerKey, reconciled[listIntentId].LedgerKey);
        Assert.Equal(buyRes1.LedgerKey, reconciled[buyIntentId].LedgerKey);

        Assert.True(processor.AuditCustody(out string? auditFailure), auditFailure);
    }

    [Fact]
    public void ClusterServerMailCoordinator_AndMetricsLabels_EnforceIdempotencyAndRoleTagging()
    {
        var coordinator = new ClusterServerMailCoordinator(realmId: 1);

        // First attempt to deliver template 77 to character 404 begins cleanly.
        Assert.True(coordinator.TryBeginDelivery(characterId: 404, templateId: 77, out ServerMailIdempotencyKey key1));
        Assert.Equal("server-mail:r1:c404:t77", key1.OperationId);

        // Rapid relog on another worker while delivery is in flight is blocked by the deterministic key.
        Assert.False(coordinator.TryBeginDelivery(characterId: 404, templateId: 77, out _));

        // If the first attempt rolls back, a subsequent login can retry and commit once.
        coordinator.AbortDelivery(key1);
        Assert.True(coordinator.TryBeginDelivery(characterId: 404, templateId: 77, out ServerMailIdempotencyKey key2));
        Assert.Equal(key1, key2);
        coordinator.CompleteDelivery(key2);

        Assert.True(coordinator.IsCommitted(characterId: 404, templateId: 77));
        Assert.False(coordinator.TryBeginDelivery(characterId: 404, templateId: 77, out _));

        // Metrics labels include realm, role, node, and incarnation.
        Guid inc = Guid.NewGuid();
        var labels = new ClusterMetricsLabels(RealmId: 2, Role: "Gateway", NodeId: "gw-eu-1", NodeIncarnation: inc);
        IReadOnlyDictionary<string, string> tags = labels.ToTagDictionary();
        Assert.Equal("2", tags["realm"]);
        Assert.Equal("gateway", tags["role"]);
        Assert.Equal("gw-eu-1", tags["node"]);
        Assert.Equal(inc.ToString("N"), tags["incarnation"]);
    }
}
