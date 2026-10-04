using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// reputation_spillover_template and reputation_reward_rate (vmangos ReputationMgr.cpp:211-243,
/// Player.cpp:6325-6350, ObjectMgr.cpp:8827-9070).
/// </summary>
public sealed class ReputationSpilloverTests
{
    private const uint TeamKill = 7101;

    private readonly FakeSession _session = new();
    private readonly RecordingReputationSink _sink = new();
    private readonly Player _player;
    private readonly ReputationService _service;

    public ReputationSpilloverTests()
    {
        _player = TestWorld.CreatePlayer(1, 0, 0, _session);
        _service = new ReputationService(Factions,
        [
            new(TeamKill, Stormwind, 0, 7, true, 40, 0, false, 0, TeamDependent: false),
        ], sink: _sink, roll: () => 0.0)
        {
            Content = ReputationContent.Create(Rows, Factions),
        };
        _service.Track(_player, _service.Create(_player, new CharacterReputationData([], -1)));
        _service.BuildInitializeFactions(_player);
        _session.Clear();
    }

    private static ReputationContentRows Rows { get; } = new(
    [
        // Booty Bay spills a quarter into Stormwind, half into the class faction while it is at most Friendly, half into Pirates.
        new ReputationSpilloverTemplate(BootyBay,
            [new(Stormwind, 0.25f, 7), new(ClassBased, 0.5f, (byte)ReputationRank.Friendly), new(Pirates, 0.5f, 7)]),
        new ReputationSpilloverTemplate(AllianceParent, [new(BootyBay, 1f, 7)]),
        new ReputationSpilloverTemplate(Stormwind, [new(BootyBay, 0.5f, 7)]),
        // A source without a reputation slot still spills (vmangos applies the templates before the main change).
        new ReputationSpilloverTemplate(Defias, [new(BootyBay, 0.5f, 7)]),
    ],
    [new ReputationRewardRate(ClassBased, QuestRate: 0f, CreatureRate: 2f, SpellRate: 1f)]);

    private static Creature Victim(uint entry, byte level)
    {
        var template = new CreatureTemplate { Entry = entry, Name = "Synthetic victim", Faction = 3, MinLevel = level, MaxLevel = level };
        var spawn = new CreatureSpawn { Guid = 900200 + entry, Entry = entry, MapId = 0, X = 0, Y = 0, Z = 0 };
        return new Creature(spawn.Guid, template, spawn, CreatureContent.Empty, new Random(1));
    }

    [Fact]
    public void QuestReward_SpillsTruncatedShares_AndSendsOneStandingPacketWithTheMainFactionFirst()
    {
        _player.Level = 10;
        _service.RewardQuest(_player, 0, [new(BootyBay, 100)]);
        Assert.Equal(100, _service.GetReputation(_player, BootyBay));
        Assert.Equal(25, _service.GetReputation(_player, Stormwind));
        Assert.Equal(550, _service.GetReputation(_player, ClassBased)); // base 500 + 50
        Assert.Equal(50, _service.GetReputation(_player, Pirates));      // forced invisible still holds standing

        (WorldOpcode opcode, byte[] payload) = _session.Next();
        Assert.Equal(WorldOpcode.SmsgSetFactionVisible, opcode); // only Booty Bay was hidden
        Assert.Equal([0, 0, 0, 0], payload);
        (opcode, payload) = _session.Next();
        Assert.Equal(WorldOpcode.SmsgSetFactionStanding, opcode);
        Assert.Equal(ReputationPackets.SetFactionStanding([(0, 100), (5, 50), (6, 50), (7, 25)]), payload);
        Assert.Empty(_session.Sent);

        _service.RewardQuest(_player, 0, [new(BootyBay, 25)]);
        Assert.Equal(25 + 6, _service.GetReputation(_player, Stormwind)); // (int)(25 * 0.25f) = 6
    }

    [Fact]
    public void Spillover_IsSkippedAboveTheTemplateRank_CheckedBeforeTheMainChange()
    {
        _player.Level = 10;
        _service.SetReputation(_player, ClassBased, 9500); // Honored, past Friendly
        _service.RewardQuest(_player, 0, [new(BootyBay, 100)]);
        Assert.Equal(9500, _service.GetReputation(_player, ClassBased));
        Assert.Equal(25, _service.GetReputation(_player, Stormwind));

        // The rank is read before the change: 2990 is still Neutral, so a spill that crosses into Friendly applies.
        _service.SetReputation(_player, ClassBased, 2990);
        _service.RewardQuest(_player, 0, [new(BootyBay, 100)]);
        Assert.Equal(3040, _service.GetReputation(_player, ClassBased));
        _service.RewardQuest(_player, 0, [new(BootyBay, 100)]);
        Assert.Equal(3090, _service.GetReputation(_player, ClassBased)); // Friendly is still <= Friendly: spills
    }

    [Fact]
    public void TeamAward_DoesNotSpill_ButTheMainFactionOfTheKillDoes()
    {
        _service.RewardKill(_player, Victim(TeamKill, 1));
        Assert.Equal(40, _service.GetReputation(_player, Stormwind));
        Assert.Equal(20, _service.GetReputation(_player, AllianceParent)); // half, Player.cpp:6395 noSpillover
        Assert.Equal(20, _service.GetReputation(_player, BootyBay));        // only Stormwind's 0.5 spillover; the parent's rate 1 was skipped
    }

    [Fact]
    public void SetReputation_SpillsTheAbsoluteValueTimesTheRate_AsVmangosDoes()
    {
        _service.SetReputation(_player, BootyBay, 1000);
        Assert.Equal(1000, _service.GetReputation(_player, BootyBay));
        Assert.Equal(250, _service.GetReputation(_player, Stormwind)); // set to 1000 * 0.25, not added
    }

    [Fact]
    public void MainFactionWithoutAStanding_StillSpills_AndReportsFalse()
    {
        Assert.False(_service.ModifyReputation(_player, Defias, 100));
        Assert.Equal(50, _service.GetReputation(_player, BootyBay));
        Assert.Contains(_sink.Rows, r => r.Faction == BootyBay && r.Standing == 50);
        Assert.DoesNotContain(_session.Sent, p => p.Opcode == WorldOpcode.SmsgSetFactionStanding); // no SendState without a main state
    }

    [Fact]
    public void QuestRewardFlag_NoSpillover_AndTheOption_SuppressSpillover()
    {
        _player.Level = 10;
        _service.RewardQuest(_player, 0, [new(BootyBay, 100, NoSpillover: true)]);
        Assert.Equal(100, _service.GetReputation(_player, BootyBay));
        Assert.Equal(0, _service.GetReputation(_player, Stormwind));

        var off = new ReputationService(Factions, sink: _sink, roll: () => 0.0)
        {
            Content = ReputationContent.Create(Rows, Factions),
            SpilloverEnabled = false,
        };
        Player other = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        off.Track(other, off.Create(other, CharacterReputationData.Empty));
        off.ModifyReputation(other, BootyBay, 100);
        Assert.Equal(0, off.GetReputation(other, Stormwind));
    }

    [Fact]
    public void Stage_Publish_ReplaysTheSameGains_IncludingSpilloverRows()
    {
        _player.Level = 10;
        Assert.True(_service.TryStage(_player, 0, [new(BootyBay, 100)], out QuestReputationStage stage));
        Assert.Equal(0, _service.GetReputation(_player, Stormwind)); // staging touches nothing live
        Assert.Contains(stage.After, r => r.Faction == Stormwind && r.Standing == 25);
        Assert.Contains(stage.After, r => r.Faction == ClassBased && r.Standing == 50);
        Assert.Contains(stage.After, r => r.Faction == BootyBay && r.Standing == 100);

        Assert.True(_service.Publish(_player, stage));
        Assert.Equal(25, _service.GetReputation(_player, Stormwind));
        Assert.Equal(550, _service.GetReputation(_player, ClassBased));
    }

    [Fact]
    public void RewardRate_ScalesEachSource_AndZeroDisablesIt()
    {
        _player.Level = 20;
        Assert.Equal(0, _service.Gain(ReputationSource.Quest, _player, 100, ClassBased, 20));  // quest_rate 0
        Assert.Equal(200, _service.Gain(ReputationSource.Kill, _player, 100, ClassBased, 20)); // creature_rate 2
        Assert.Equal(100, _service.Gain(ReputationSource.Spell, _player, 100, ClassBased, 20)); // spell_rate 1
        Assert.Equal(100, _service.Gain(ReputationSource.Quest, _player, 100, BootyBay, 20));   // no row: unscaled
        Assert.Equal(-100, _service.Gain(ReputationSource.Quest, _player, -100, BootyBay, 20));

        _service.RewardQuest(_player, 20, [new(ClassBased, 100)]);
        Assert.Equal(500, _service.GetReputation(_player, ClassBased)); // disabled quest gain changes nothing
    }

    [Fact]
    public void Content_DropsAndWarnsLikeObjectMgr()
    {
        var content = ReputationContent.Create(new ReputationContentRows(
        [
            new ReputationSpilloverTemplate(UnknownFaction, [new(BootyBay, 0.5f, 7)]),            // unknown source: dropped
            new ReputationSpilloverTemplate(Stormwind, [new(UnknownFaction, 0.5f, 7)]),           // unknown target: dropped
            new ReputationSpilloverTemplate(BootyBay, [new(Defias, 0.5f, 7), new(0, 0f, 0)]),     // no slot: reported, kept
            new ReputationSpilloverTemplate(ClassBased, [new(BootyBay, 0.5f, 8)]),                // rank 8: reported, kept
        ],
        [
            new ReputationRewardRate(UnknownFaction, 1, 1, 1),
            new ReputationRewardRate(BootyBay, -1, 1, 1),
            new ReputationRewardRate(Stormwind, 1, 0.5f, 1),
        ]), Factions);

        Assert.Equal(2, content.SpilloverCount);
        Assert.Null(content.Spillover(UnknownFaction));
        Assert.Null(content.Spillover(Stormwind));
        Assert.Single(content.Spillover(BootyBay)!.Targets);
        Assert.Equal(1, content.RateCount);
        Assert.Equal(0.5f, content.Rate(Stormwind)!.Value.CreatureRate);
        Assert.Equal(6, content.Warnings.Count);
    }
}
