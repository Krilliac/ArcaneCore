using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.ServerMail;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Warden;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Verification of M15.2: Realm-Wide Social &amp; Economy Authority
/// (<see cref="IRealmPresenceDirectory"/>, <see cref="IClusterSocialRouter"/>,
/// <see cref="IRealmAuthorityLeaseStore"/>, and <see cref="ClusterRoleFeatureFilter"/>).
/// </summary>
public sealed class ClusterSocialAndEconomyAuthorityTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);

    private static RealmPresenceEntry CreatePresence(
        ulong guid = 101,
        string name = "Aldric",
        int accountId = 10,
        string workerId = "worker-kalimdor",
        string gatewayId = "gateway-1",
        uint mapId = 1,
        uint zoneId = 14,
        uint areaId = 362,
        byte level = 60,
        byte @class = 1,
        byte race = 2,
        byte gender = 0,
        int guildId = 50,
        int groupId = 70,
        long presenceVersion = 1,
        long sessionGeneration = 1,
        string? guildName = "Vanguard")
        => new(
            CharacterGuid: guid,
            CharacterName: name,
            AccountId: accountId,
            WorkerId: workerId,
            GatewayId: gatewayId,
            MapId: mapId,
            ZoneId: zoneId,
            AreaId: areaId,
            Level: level,
            Class: @class,
            Race: race,
            Gender: gender,
            GuildId: guildId,
            GroupId: groupId,
            PresenceVersion: presenceVersion,
            SessionGeneration: sessionGeneration,
            UpdatedAtUtc: BaseTime,
            GuildName: guildName);

    [Fact]
    public void PresenceDirectory_EnforcesMonotonicVersionAndSessionGeneration_AndRejectsStaleWorkerRemove()
    {
        var directory = new InMemoryRealmPresenceDirectory();

        RealmPresenceEntry initial = CreatePresence(
            guid: 101,
            name: "Aldric",
            workerId: "worker-eastern",
            gatewayId: "gateway-1",
            presenceVersion: 1,
            sessionGeneration: 1);

        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(initial, out RealmPresenceEntry? current));
        Assert.Equal(initial, current);
        Assert.Equal(1, directory.Count);

        // Duplicate or lower PresenceVersion on the same session generation is rejected.
        RealmPresenceEntry staleVersion = initial with { PresenceVersion = 1, ZoneId = 999 };
        Assert.Equal(PresenceMutationResult.RejectedStaleVersion, directory.TryUpsert(staleVersion, out current));
        Assert.Equal(14u, current?.ZoneId);

        // Character transfers to worker-kalimdor with PresenceVersion = 2.
        RealmPresenceEntry transferred = initial with
        {
            WorkerId = "worker-kalimdor",
            MapId = 1,
            ZoneId = 1637,
            PresenceVersion = 2,
        };
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(transferred, out current));
        Assert.Equal("worker-kalimdor", directory.FindByGuid(101)?.WorkerId);

        // Late departure/logout notification from the old worker (version 1 or wrong workerId) cannot delete the active presence.
        Assert.Equal(
            PresenceMutationResult.RejectedStaleVersion,
            directory.TryRemove(101, expectedPresenceVersion: 1, expectedSessionGeneration: 1, expectedWorkerId: "worker-eastern"));
        Assert.Equal(
            PresenceMutationResult.RejectedStaleVersion,
            directory.TryRemove(101, expectedPresenceVersion: 2, expectedSessionGeneration: 1, expectedWorkerId: "worker-eastern"));
        Assert.Equal("worker-kalimdor", directory.FindByName("ALDRIC")?.WorkerId);

        // Player reconnects on gateway-2 with SessionGeneration = 2, PresenceVersion = 3.
        RealmPresenceEntry reconnected = transferred with
        {
            GatewayId = "gateway-2",
            SessionGeneration = 2,
            PresenceVersion = 3,
        };
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(reconnected, out _));

        // Stale upsert or remove carrying SessionGeneration = 1 is rejected even if it claims a higher PresenceVersion.
        RealmPresenceEntry staleSessionUpsert = reconnected with { SessionGeneration = 1, PresenceVersion = 99, GatewayId = "gateway-1" };
        Assert.Equal(PresenceMutationResult.RejectedStaleSessionGeneration, directory.TryUpsert(staleSessionUpsert, out _));
        Assert.Equal(
            PresenceMutationResult.RejectedStaleSessionGeneration,
            directory.TryRemove(101, expectedPresenceVersion: 3, expectedSessionGeneration: 1, expectedWorkerId: "worker-kalimdor"));
        Assert.Equal("gateway-2", directory.FindByGuid(101)?.GatewayId);

        // Matching version, generation, and worker removes cleanly and preserves high-water marks against late resurrection.
        Assert.Equal(
            PresenceMutationResult.Applied,
            directory.TryRemove(101, expectedPresenceVersion: 3, expectedSessionGeneration: 2, expectedWorkerId: "worker-kalimdor"));
        Assert.Equal(0, directory.Count);
        Assert.Null(directory.FindByGuid(101));
        Assert.Null(directory.FindByName("Aldric"));

        // A delayed packet from version 3 or generation 1 cannot resurrect the logged-out entry.
        Assert.Equal(PresenceMutationResult.RejectedStaleVersion, directory.TryUpsert(reconnected, out _));
        Assert.Equal(0, directory.Count);
    }

    [Fact]
    public void PresenceDirectory_SupportsCaseInsensitiveNameGuildGroupAndWhoQueries()
    {
        var directory = new InMemoryRealmPresenceDirectory();

        directory.TryUpsert(CreatePresence(guid: 1, name: "Thrall", accountId: 1, zoneId: 1637, level: 60, @class: 7, race: 2, guildId: 10, groupId: 5, presenceVersion: 1, guildName: "Frostwolf"), out _);
        directory.TryUpsert(CreatePresence(guid: 2, name: "Jaina", accountId: 2, zoneId: 1519, level: 60, @class: 8, race: 1, guildId: 20, groupId: 5, presenceVersion: 1, guildName: "Kirin Tor"), out _);
        directory.TryUpsert(CreatePresence(guid: 3, name: "Rexxar", accountId: 3, zoneId: 1637, level: 45, @class: 3, race: 2, guildId: 10, groupId: 0, presenceVersion: 1, guildName: "Frostwolf"), out _);

        Assert.Equal(1ul, directory.FindByName("thrall")?.CharacterGuid);
        Assert.Equal(2ul, directory.FindByName("JAINA")?.CharacterGuid);
        Assert.Equal([1ul, 3ul], directory.FindByGuild(10).Select(e => e.CharacterGuid));
        Assert.Equal([1ul, 2ul], directory.FindByGroup(5).Select(e => e.CharacterGuid));
        Assert.Equal([1ul], directory.FindByAccount(1).Select(e => e.CharacterGuid));

        // /who filter by zone + guild search term + level range.
        IReadOnlyList<RealmPresenceEntry> orgrimmarFrostwolves = directory.QueryWho(new RealmWhoFilter(
            MinLevel: 50,
            MaxLevel: 60,
            ZoneIds: [1637],
            SearchTerms: ["frost"]));
        Assert.Equal([1ul], orgrimmarFrostwolves.Select(e => e.CharacterGuid));
    }

    [Fact]
    public void SocialRouter_RoutesWhisperGroupGuildAndChannelAcrossWorkers_AndEnforcesAuthAndDedup()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var router = new InMemoryClusterSocialRouter(directory);

        // Player 1 on worker-a / gateway-1; Player 2 on worker-b / gateway-2; both in group 70 and guild 50.
        directory.TryUpsert(CreatePresence(guid: 101, name: "Aldric", accountId: 10, workerId: "worker-a", gatewayId: "gateway-1", guildId: 50, groupId: 70, presenceVersion: 1, sessionGeneration: 1), out _);
        directory.TryUpsert(CreatePresence(guid: 202, name: "Brienne", accountId: 20, workerId: "worker-b", gatewayId: "gateway-2", guildId: 50, groupId: 70, presenceVersion: 1, sessionGeneration: 2), out _);

        // 1. Cross-worker whisper by case-insensitive name.
        var whisper = new ClusterSocialMessage(
            MessageId: Guid.NewGuid(),
            Channel: SocialRouteChannel.Whisper,
            SenderGuid: 101,
            SenderName: "Aldric",
            SenderAccountId: 10,
            SenderSessionGeneration: 1,
            OriginWorkerId: "worker-a",
            OriginGatewayId: "gateway-1",
            SenderSequence: 1,
            PayloadText: "Meet at the instance portal",
            TargetCharacterName: "brienne");

        SocialRouteResult whisperResult = router.Route(whisper);
        Assert.Equal(SocialRouteStatus.Delivered, whisperResult.Status);
        RoutedSocialDelivery whisperDelivery = Assert.Single(whisperResult.Deliveries);
        Assert.Equal(202ul, whisperDelivery.RecipientGuid);
        Assert.Equal(2, whisperDelivery.RecipientSessionGeneration);
        Assert.Equal("worker-b", whisperDelivery.TargetWorkerId);
        Assert.Equal("gateway-2", whisperDelivery.TargetGatewayId);

        // Retrying the same MessageId or (SenderGuid, SessionGeneration, SenderSequence) is deduplicated without re-queueing.
        SocialRouteResult duplicateById = router.Route(whisper);
        Assert.Equal(SocialRouteStatus.DuplicateSuppressed, duplicateById.Status);
        SocialRouteResult duplicateBySeq = router.Route(whisper with { MessageId = Guid.NewGuid() });
        Assert.Equal(SocialRouteStatus.DuplicateSuppressed, duplicateBySeq.Status);
        Assert.Single(router.DrainGatewayDeliveries("gateway-2"));
        Assert.Empty(router.DrainGatewayDeliveries("gateway-2"));

        // 2. Spoofed sender generation or wrong origin worker is rejected.
        SocialRouteResult spoofedGeneration = router.Route(whisper with
        {
            MessageId = Guid.NewGuid(),
            SenderSequence = 2,
            SenderSessionGeneration = 999,
        });
        Assert.Equal(SocialRouteStatus.RejectedUnauthorizedSender, spoofedGeneration.Status);

        SocialRouteResult spoofedWorker = router.Route(whisper with
        {
            MessageId = Guid.NewGuid(),
            SenderSequence = 3,
            OriginWorkerId = "worker-b",
        });
        Assert.Equal(SocialRouteStatus.RejectedUnauthorizedSender, spoofedWorker.Status);

        // 3. Group and Guild chat fan out across both workers with monotonic DeliverySequence.
        SocialRouteResult groupResult = router.Route(new ClusterSocialMessage(
            MessageId: Guid.NewGuid(),
            Channel: SocialRouteChannel.GroupChat,
            SenderGuid: 101,
            SenderName: "Aldric",
            SenderAccountId: 10,
            SenderSessionGeneration: 1,
            OriginWorkerId: "worker-a",
            OriginGatewayId: "gateway-1",
            SenderSequence: 4,
            PayloadText: "Pulling in 5",
            TargetGroupId: 70));
        Assert.Equal(SocialRouteStatus.Delivered, groupResult.Status);
        Assert.Equal([101ul, 202ul], groupResult.Deliveries.Select(d => d.RecipientGuid));

        SocialRouteResult guildResult = router.Route(new ClusterSocialMessage(
            MessageId: Guid.NewGuid(),
            Channel: SocialRouteChannel.GuildChat,
            SenderGuid: 202,
            SenderName: "Brienne",
            SenderAccountId: 20,
            SenderSessionGeneration: 2,
            OriginWorkerId: "worker-b",
            OriginGatewayId: "gateway-2",
            SenderSequence: 1,
            PayloadText: "On my way",
            TargetGuildId: 50));
        Assert.Equal(SocialRouteStatus.Delivered, guildResult.Status);
        Assert.Equal([101ul, 202ul], guildResult.Deliveries.Select(d => d.RecipientGuid));

        // 4. Custom channel requires active membership at the current session generation.
        Assert.True(router.JoinChannel("WorldDefense", 101, sessionGeneration: 1));
        Assert.True(router.JoinChannel("WorldDefense", 202, sessionGeneration: 2));

        SocialRouteResult channelResult = router.Route(new ClusterSocialMessage(
            MessageId: Guid.NewGuid(),
            Channel: SocialRouteChannel.CustomChannel,
            SenderGuid: 101,
            SenderName: "Aldric",
            SenderAccountId: 10,
            SenderSessionGeneration: 1,
            OriginWorkerId: "worker-a",
            OriginGatewayId: "gateway-1",
            SenderSequence: 5,
            PayloadText: "Crossroads under attack",
            TargetChannelName: "worlddefense"));
        Assert.Equal(SocialRouteStatus.Delivered, channelResult.Status);
        Assert.Equal(2, channelResult.Deliveries.Count);
    }

    [Fact]
    public void SocialRouter_LogoutWithOrderedFlush_DrainsPendingDeliveriesBeforeRemovingPresence()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        var router = new InMemoryClusterSocialRouter(directory);

        directory.TryUpsert(CreatePresence(guid: 101, name: "Aldric", accountId: 10, workerId: "worker-a", gatewayId: "gateway-1", presenceVersion: 1, sessionGeneration: 1), out _);
        directory.TryUpsert(CreatePresence(guid: 202, name: "Brienne", accountId: 20, workerId: "worker-b", gatewayId: "gateway-2", presenceVersion: 1, sessionGeneration: 1), out _);
        Assert.True(router.JoinChannel("General", 101, sessionGeneration: 1));
        Assert.True(router.JoinChannel("General", 202, sessionGeneration: 1));

        // Brienne whispers Aldric right before Aldric logs out.
        SocialRouteResult preLogoutWhisper = router.Route(new ClusterSocialMessage(
            MessageId: Guid.NewGuid(),
            Channel: SocialRouteChannel.Whisper,
            SenderGuid: 202,
            SenderName: "Brienne",
            SenderAccountId: 20,
            SenderSessionGeneration: 1,
            OriginWorkerId: "worker-b",
            OriginGatewayId: "gateway-2",
            SenderSequence: 1,
            PayloadText: "Safe travels!",
            TargetCharacterGuid: 101));
        Assert.Equal(SocialRouteStatus.Delivered, preLogoutWhisper.Status);

        // Stale worker logout attempt does not flush or remove Aldric.
        Assert.Equal(
            PresenceMutationResult.RejectedStaleVersion,
            router.LogoutWithOrderedFlush(101, expectedPresenceVersion: 1, expectedSessionGeneration: 1, expectedWorkerId: "worker-wrong", out IReadOnlyList<RoutedSocialDelivery> wrongFlush));
        Assert.Empty(wrongFlush);
        Assert.NotNull(directory.FindByGuid(101));

        // Authoritative logout flushes Aldric's gateway queue in causal order and removes presence + channel membership.
        Assert.Equal(
            PresenceMutationResult.Applied,
            router.LogoutWithOrderedFlush(101, expectedPresenceVersion: 1, expectedSessionGeneration: 1, expectedWorkerId: "worker-a", out IReadOnlyList<RoutedSocialDelivery> flushed));
        RoutedSocialDelivery deliveredBeforeLogout = Assert.Single(flushed);
        Assert.Equal("Safe travels!", deliveredBeforeLogout.PayloadText);
        Assert.Null(directory.FindByGuid(101));

        // Subsequent whispers to Aldric fail cleanly as offline.
        SocialRouteResult postLogoutWhisper = router.Route(new ClusterSocialMessage(
            MessageId: Guid.NewGuid(),
            Channel: SocialRouteChannel.Whisper,
            SenderGuid: 202,
            SenderName: "Brienne",
            SenderAccountId: 20,
            SenderSessionGeneration: 1,
            OriginWorkerId: "worker-b",
            OriginGatewayId: "gateway-2",
            SenderSequence: 2,
            PayloadText: "Are you still there?",
            TargetCharacterGuid: 101));
        Assert.Equal(SocialRouteStatus.RejectedUnknownTarget, postLogoutWhisper.Status);
    }

    [Fact]
    public void AuthorityLeaseStore_EnforcesSingleEconomyOwner_AndMonotonicFencingTokensAcrossTakeover()
    {
        var store = new InMemoryRealmAuthorityLeaseStore();
        var nodeAIncarnation = Guid.NewGuid();
        var nodeBIncarnation = Guid.NewGuid();
        TimeSpan ttl = TimeSpan.FromSeconds(30);

        // Node A acquires the economy lease (fencing token 1).
        AuthorityLeaseResult acquireA = store.TryAcquire(RealmAuthorityScopes.Economy, "econ-node-a", nodeAIncarnation, ttl, BaseTime);
        Assert.Equal(AuthorityLeaseOutcome.Granted, acquireA.Outcome);
        Assert.NotNull(acquireA.Lease);
        Assert.Equal(1, acquireA.Lease.FencingToken);
        Assert.True(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "econ-node-a", 1, BaseTime.AddSeconds(10)));

        // Node B cannot acquire while Node A's lease is unexpired.
        AuthorityLeaseResult competingB = store.TryAcquire(RealmAuthorityScopes.Economy, "econ-node-b", nodeBIncarnation, ttl, BaseTime.AddSeconds(10));
        Assert.Equal(AuthorityLeaseOutcome.RejectedHeldByOther, competingB.Outcome);
        Assert.Equal("econ-node-a", competingB.Lease?.OwnerNodeId);

        // Node A renews in place before expiry (keeps token 1, extends expiry).
        DateTimeOffset renewTime = BaseTime.AddSeconds(20);
        AuthorityLeaseResult renewA = store.TryRenew(RealmAuthorityScopes.Economy, "econ-node-a", nodeAIncarnation, fencingToken: 1, ttl, renewTime);
        Assert.Equal(AuthorityLeaseOutcome.Renewed, renewA.Outcome);
        Assert.Equal(1, renewA.Lease?.FencingToken);
        Assert.Equal(renewTime + ttl, renewA.Lease?.ExpiresAtUtc);

        // After Node A's renewed lease expires, Node B takes over and receives strictly higher fencing token 2.
        DateTimeOffset afterExpiry = renewTime + ttl + TimeSpan.FromSeconds(1);
        AuthorityLeaseResult takeoverB = store.TryAcquire(RealmAuthorityScopes.Economy, "econ-node-b", nodeBIncarnation, ttl, afterExpiry);
        Assert.Equal(AuthorityLeaseOutcome.Granted, takeoverB.Outcome);
        Assert.Equal(2, takeoverB.Lease?.FencingToken);

        // Partitioned Node A attempting to validate, renew, or release with stale token 1 is fenced off.
        Assert.False(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "econ-node-a", fencingToken: 1, afterExpiry));
        AuthorityLeaseResult lateRenewA = store.TryRenew(RealmAuthorityScopes.Economy, "econ-node-a", nodeAIncarnation, fencingToken: 1, ttl, afterExpiry);
        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken, lateRenewA.Outcome);
        AuthorityLeaseResult lateReleaseA = store.TryRelease(RealmAuthorityScopes.Economy, "econ-node-a", nodeAIncarnation, fencingToken: 1);
        Assert.Equal(AuthorityLeaseOutcome.RejectedStaleToken, lateReleaseA.Outcome);

        // Node B's token 2 remains authoritative.
        Assert.True(store.ValidateFencingToken(RealmAuthorityScopes.Economy, "econ-node-b", fencingToken: 2, afterExpiry));
    }

    [Fact]
    public void ClusterRoleFeatureFilter_ScopesSingletonFeaturesAndSchedulersByRoleAndFencingToken()
    {
        var leases = new InMemoryRealmAuthorityLeaseStore();
        var filter = new ClusterRoleFeatureFilter(leases);
        TimeSpan ttl = TimeSpan.FromSeconds(30);

        AuthorityLeaseResult socialLease = leases.TryAcquire(RealmAuthorityScopes.Social, "social-1", Guid.NewGuid(), ttl, BaseTime);
        AuthorityLeaseResult econLease = leases.TryAcquire(RealmAuthorityScopes.Economy, "econ-1", Guid.NewGuid(), ttl, BaseTime);

        // 1. Combined mode admits all features without requiring cluster role separation.
        Assert.True(filter.ShouldAttachFeature(typeof(AutoBroadcastFeature), ClusterNodeRole.Combined));
        Assert.True(filter.ShouldAttachFeature(typeof(AuctionBotFeature), ClusterNodeRole.Combined));
        Assert.True(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.Combined));
        Assert.True(filter.ShouldAttachFeature(typeof(SpellFeature), ClusterNodeRole.Combined));
        Assert.True(filter.ShouldAttachFeature(typeof(ServerMailFeature), ClusterNodeRole.Combined));

        // 2. MapWorker runs gameplay/map features (SpellFeature, ServerMailFeature) and Warden, and AutoBroadcast/AuctionBot only with their lease.
        Assert.False(filter.ShouldAttachFeature(typeof(AutoBroadcastFeature), ClusterNodeRole.MapWorker, "worker-1"));
        Assert.False(filter.ShouldAttachFeature(typeof(AuctionBotFeature), ClusterNodeRole.MapWorker, "worker-1"));
        // Warden stays on a map worker: the gateway passes the client stream through to it (Tier 3a), so the worker owns the Warden session.
        Assert.True(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.MapWorker, "worker-1"));
        Assert.True(filter.ShouldAttachFeature(typeof(SpellFeature), ClusterNodeRole.MapWorker, "worker-1"));
        Assert.True(filter.ShouldAttachFeature(typeof(ServerMailFeature), ClusterNodeRole.MapWorker, "worker-1"));
        Assert.False(filter.CanExecuteEconomyAuthorityTick(ClusterNodeRole.MapWorker, "worker-1", econLease.Lease!.FencingToken, BaseTime));

        // 3. Gateway runs Warden (connection owner), NOT map gameplay or realm-singleton timers.
        Assert.True(filter.ShouldAttachFeature(typeof(WardenFeature), ClusterNodeRole.Gateway, "gateway-1"));
        Assert.False(filter.ShouldAttachFeature(typeof(AutoBroadcastFeature), ClusterNodeRole.Gateway, "gateway-1"));
        Assert.False(filter.ShouldAttachFeature(typeof(AuctionBotFeature), ClusterNodeRole.Gateway, "gateway-1"));
        Assert.False(filter.ShouldAttachFeature(typeof(SpellFeature), ClusterNodeRole.Gateway, "gateway-1"));

        // 4. Social role runs AutoBroadcast only while holding the active social fencing token.
        Assert.True(filter.ShouldAttachFeature(typeof(AutoBroadcastFeature), ClusterNodeRole.Social, "social-1", socialLease.Lease!.FencingToken, BaseTime));
        Assert.False(filter.ShouldAttachFeature(typeof(AutoBroadcastFeature), ClusterNodeRole.Social, "social-standby", socialLease.Lease.FencingToken, BaseTime));

        // 5. Economy role runs AuctionBotFeature and economy authority ticks only while holding the current economy fencing token.
        Assert.True(filter.ShouldAttachFeature(typeof(AuctionBotFeature), ClusterNodeRole.Economy, "econ-1", econLease.Lease.FencingToken, BaseTime));
        Assert.True(filter.CanExecuteEconomyAuthorityTick(ClusterNodeRole.Economy, "econ-1", econLease.Lease.FencingToken, BaseTime));

        // After lease expiry and takeover by econ-2 (token 2), econ-1 with stale token 1 is immediately denied.
        DateTimeOffset afterExpiry = BaseTime + ttl + TimeSpan.FromSeconds(1);
        AuthorityLeaseResult takeover = leases.TryAcquire(RealmAuthorityScopes.Economy, "econ-2", Guid.NewGuid(), ttl, afterExpiry);
        Assert.Equal(2, takeover.Lease?.FencingToken);
        Assert.False(filter.CanExecuteEconomyAuthorityTick(ClusterNodeRole.Economy, "econ-1", fencingToken: 1, afterExpiry));
        Assert.True(filter.CanExecuteEconomyAuthorityTick(ClusterNodeRole.Economy, "econ-2", fencingToken: 2, afterExpiry));
    }
}
