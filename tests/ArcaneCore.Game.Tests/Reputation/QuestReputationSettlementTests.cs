using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// Quest reward reputation is part of the reward transaction: while a character is held by a
/// pending settlement no other path may change standings or flags, because a queued write
/// carries full standing rows that would revert the committed gain.
/// </summary>
public sealed class QuestReputationSettlementTests
{
    private readonly FakeSession _session = new();
    private readonly RecordingReputationSink _sink = new();
    private readonly Player _player;
    private readonly ReputationService _service;

    public QuestReputationSettlementTests()
    {
        _player = TestWorld.CreatePlayer(1, 0, 0, _session);
        _service = new ReputationService(Factions, sink: _sink, roll: () => 0.999);
        _service.Track(_player, _service.Create(_player, new CharacterReputationData([], 7)));
        _service.BuildInitializeFactions(_player);
        _session.Clear();
    }

    [Fact]
    public void ModifyReputation_RefusedWhileQuestSettlementHeld()
    {
        Guid operation = Guid.NewGuid();
        Assert.True(_player.BeginQuestSettlement(operation));
        Assert.False(_service.ModifyReputation(_player, BootyBay, 100));
        Assert.False(_service.SetReputation(_player, BootyBay, 100));
        Assert.Equal(0, _service.GetReputation(_player, BootyBay));
        Assert.Empty(_sink.Rows);
        Assert.Empty(_session.Sent);

        Assert.True(_player.EndQuestSettlement(operation));
        Assert.True(_service.ModifyReputation(_player, BootyBay, 100));
        Assert.Equal(100, _service.GetReputation(_player, BootyBay));
    }

    [Fact]
    public void SetAtWar_RefusedWhileHeld_SetWatchedFaction_Allowed()
    {
        Assert.True(_service.ModifyReputation(_player, BootyBay, 100)); // makes Booty Bay (list 0) visible
        _sink.Rows.Clear();
        _session.Clear();
        Guid operation = Guid.NewGuid();
        Assert.True(_player.BeginQuestSettlement(operation));

        Assert.False(_service.SetAtWar(_player, 0, true));
        Assert.False(_service.SetInactive(_player, 0, true));
        Assert.Empty(_sink.Rows);
        // The watched bar lives in its own table that the reward transaction never touches.
        Assert.True(_service.SetWatchedFaction(_player, 0));

        Assert.True(_player.EndQuestSettlement(operation));
        Assert.True(_service.SetAtWar(_player, 0, true));
        Assert.True(_service.SetInactive(_player, 0, true));
    }

    [Fact]
    public void PublicationScope_MayStillChangeReputation()
    {
        Guid operation = Guid.NewGuid();
        Assert.True(_player.BeginQuestSettlement(operation));
        using (_player.BeginQuestSettlementPublication(operation))
        {
            Assert.True(_service.ModifyReputation(_player, BootyBay, 100));
        }

        Assert.Equal(100, _service.GetReputation(_player, BootyBay));
    }

    private static readonly QuestReputationReward[] Rewards =
    [
        new(BootyBay, 100), new(Stormwind, 50),
        new(0, 10),           // no faction: skipped
        new(Defias, 5),       // faction without reputation: skipped
        new(BootyBay, 0),     // zero value: skipped
        new(UnknownFaction, 5), // not in Faction.dbc: skipped
    ];

    [Fact]
    public void TryStage_IsSideEffectFree_AndPublishMatchesRewardQuestPacketsAndRows()
    {
        // Reference: the ordinary post-commit path on an identical player.
        var referenceSession = new FakeSession();
        var referenceSink = new RecordingReputationSink();
        Player reference = TestWorld.CreatePlayer(1, 0, 0, referenceSession);
        var referenceService = new ReputationService(Factions, sink: referenceSink, roll: () => 0.999);
        referenceService.Track(reference, referenceService.Create(reference, new CharacterReputationData([], 7)));
        referenceService.BuildInitializeFactions(reference);
        referenceSession.Clear();
        referenceService.RewardQuest(reference, 0, Rewards);

        Assert.True(_service.TryStage(_player, 0, Rewards, out QuestReputationStage stage));
        Assert.Empty(_session.Sent);
        Assert.Empty(_sink.Rows);
        Assert.Equal(0, _service.GetReputation(_player, BootyBay));
        Assert.Empty(_service.For(_player)!.TakeDirty(1));
        Assert.Equal(referenceSink.Rows.GroupBy(r => r.Faction).Select(g => g.Last()).OrderBy(r => r.Faction),
            stage.After.OrderBy(r => r.Faction)); // the rows the transaction writes

        Assert.True(_service.Publish(_player, stage));
        Assert.Equal(referenceSession.Sent.Select(p => (p.Opcode, p.Payload)), _session.Sent.Select(p => (p.Opcode, p.Payload)));
        Assert.Empty(_sink.Rows); // the transaction persisted them: Publish never queues a write
        Assert.Equal(referenceService.GetReputation(reference, BootyBay), _service.GetReputation(_player, BootyBay));
        Assert.Equal(referenceService.GetReputation(reference, Stormwind), _service.GetReputation(_player, Stormwind));
    }

    [Fact]
    public void TryStage_FreezesTheDitherRoll_SoPublicationReplaysTheSameGains()
    {
        double roll = 0.0;
        var service = new ReputationService(Factions, sink: _sink, roll: () => roll);
        Player player = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        service.Track(player, service.Create(player, CharacterReputationData.Empty));
        Assert.True(service.TryStage(player, 0, [new(BootyBay, 3)], out QuestReputationStage stage));
        int staged = stage.After.Single().Standing;

        roll = 0.999; // a fresh roll could now round the other way
        Assert.True(service.Publish(player, stage));
        Assert.Equal(staged, service.GetReputation(player, BootyBay));
    }

    [Fact]
    public void TryStage_NeedsLoadedStandings_OnlyWhenSomethingWouldBeRewarded()
    {
        Player stranger = TestWorld.CreatePlayer(3, 0, 0, new FakeSession());
        Assert.False(_service.TryStage(stranger, 0, [new(BootyBay, 100)], out _));
        Assert.True(_service.TryStage(stranger, 0, [new(0, 100), new(BootyBay, 0)], out QuestReputationStage stage));
        Assert.Empty(stage.After);
        Assert.Empty(stage.Gains);
    }

    [Fact]
    public void Publish_WhenLiveStateDiverged_QueuesTheLiveRowsAndReportsIt()
    {
        Assert.True(_service.TryStage(_player, 0, [new(BootyBay, 100)], out QuestReputationStage stage));
        Assert.True(_service.ModifyReputation(_player, BootyBay, 7)); // an unguarded write slipped in before the commit
        _sink.Rows.Clear();

        Assert.False(_service.Publish(_player, stage));
        CharacterReputationRow live = Assert.Single(_sink.Rows);
        Assert.Equal(BootyBay, live.Faction);
        Assert.Equal(_service.GetReputation(_player, BootyBay), live.Standing);
        Assert.NotEqual(stage.After.Single().Standing, live.Standing);
    }
}
