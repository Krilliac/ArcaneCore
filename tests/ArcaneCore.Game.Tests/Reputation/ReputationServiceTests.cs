using System.Buffers.Binary;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>The world-thread owner: notifications, persistence, kill and quest rewards, NPC seams.</summary>
public sealed class ReputationServiceTests
{
    private const uint KillEntry = 900100;
    private const uint TeamAwardEntry = 900101;
    private const uint CappedEntry = 900102;

    private readonly FakeSession _session = new();
    private readonly RecordingReputationSink _sink = new();
    private readonly Player _player;
    private readonly ReputationService _service;
    private double _roll = 0.0; // frand(0,1) = 0: fractions floor

    public ReputationServiceTests()
    {
        _player = TestWorld.CreatePlayer(1, 0, 0, _session);
        _service = new ReputationService(Factions,
        [
            new(KillEntry, BootyBay, Stormwind, 7, false, 10, 7, false, 20, TeamDependent: true),
            new(TeamAwardEntry, Stormwind, 0, 7, true, 40, 0, false, 0, TeamDependent: false),
            new(CappedEntry, BootyBay, 0, (byte)ReputationRank.Neutral, false, 25, 0, false, 0, TeamDependent: false),
        ], sink: _sink, roll: () => _roll);
        _service.Track(_player, _service.Create(_player, new CharacterReputationData([], 7)));
        _service.BuildInitializeFactions(_player); // login stage: clears every pending standing send
        _session.Clear();
    }

    [Fact]
    public void Create_PublishesTheStoredWatchedFaction()
    {
        Assert.Equal(7, _player.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex));
        Player other = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        _service.Create(other, CharacterReputationData.Empty);
        Assert.Equal(-1, other.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex));
        Assert.Null(_service.For(other)); // Create does not track
    }

    [Fact]
    public void Modify_SendsVisibleThenStanding_AndPersistsChangedRows()
    {
        Assert.True(_service.ModifyReputation(_player, BootyBay, 100));
        (WorldOpcode opcode, byte[] payload) = _session.Next();
        Assert.Equal(WorldOpcode.SmsgSetFactionVisible, opcode);
        Assert.Equal([0, 0, 0, 0], payload);
        (opcode, payload) = _session.Next();
        Assert.Equal(WorldOpcode.SmsgSetFactionStanding, opcode);
        Assert.Equal(ReputationPackets.SetFactionStanding([(0, 100)]), payload);
        Assert.Empty(_session.Sent);
        Assert.Equal([new CharacterReputationRow(1, BootyBay, 100, 1)], _sink.Rows);

        Assert.True(_service.SetReputation(_player, BootyBay, 9000));
        Assert.Equal(WorldOpcode.SmsgSetFactionStanding, _session.Next().Opcode);
        Assert.Empty(_session.Sent);
        Assert.Equal(ReputationRank.Honored, _service.GetRank(_player, BootyBay));
        Assert.Equal(9000, _service.GetReputation(_player, BootyBay));
    }

    [Fact]
    public void BeforeTheInitialPacket_TheFirstStandingUpdateCarriesEveryPendingFaction()
    {
        Player fresh = TestWorld.CreatePlayer(7, 0, 0, _session);
        _service.Track(fresh, _service.Create(fresh, CharacterReputationData.Empty));
        Assert.True(_service.ModifyReputation(fresh, Stormwind, 5));
        (WorldOpcode opcode, byte[] payload) = _session.Next(); // already visible: no SMSG_SET_FACTION_VISIBLE
        Assert.Equal(WorldOpcode.SmsgSetFactionStanding, opcode);
        Assert.Equal(ReputationPackets.SetFactionStanding([(7, 5), (0, 0), (5, 0), (6, 0), (10, 0)]), payload);
        Assert.Empty(_session.Sent);
    }

    [Fact]
    public void UnknownFactionsAndUntrackedPlayers_ChangeNothing()
    {
        Assert.False(_service.ModifyReputation(_player, Defias, 100));
        Assert.False(_service.ModifyReputation(_player, UnknownFaction, 100));
        Player stranger = TestWorld.CreatePlayer(3, 0, 0, _session);
        Assert.False(_service.ModifyReputation(stranger, BootyBay, 100));
        Assert.False(_service.SetAtWar(stranger, 0, true));
        Assert.False(_service.SetWatchedFaction(stranger, 7));
        Assert.Equal(0, _service.GetReputation(stranger, Stormwind));
        _service.Untrack(_player);
        Assert.False(_service.ModifyReputation(_player, BootyBay, 100));
        Assert.Empty(_session.Sent);
        Assert.Empty(_sink.Rows);
    }

    [Fact]
    public void QuestRewards_ScaleByQuestLevel_AndSkipUnknownOrZeroRewards()
    {
        _player.Level = 10;
        _service.RewardQuest(_player, 0, [new(BootyBay, 250)]);                // quest level 0 = player level
        Assert.Equal(250, _service.GetReputation(_player, BootyBay));
        _service.RewardQuest(_player, 1, [new(BootyBay, 250), new(UnknownFaction, 250), new(Defias, 250), new(Stormwind, 0)]);
        Assert.Equal(300, _service.GetReputation(_player, BootyBay));          // 20% for five-plus levels below
        _service.RewardQuest(_player, 1, [new(BootyBay, -100)]);              // losses are unscaled
        Assert.Equal(200, _service.GetReputation(_player, BootyBay));
        Assert.Equal(0, _service.GetReputation(_player, Stormwind));

        _service.GainModifier = (_, source, faction) => source == ReputationSource.Quest && faction == BootyBay ? 10 : 0;
        _service.RewardQuest(_player, 10, [new(BootyBay, 250)]);
        Assert.Equal(475, _service.GetReputation(_player, BootyBay));
        _service.RewardQuest(_player, 10, [new(BootyBay, -100)]);              // modifiers never scale losses
        Assert.Equal(375, _service.GetReputation(_player, BootyBay));
    }

    [Fact]
    public void Gain_DithersTheFraction()
    {
        _player.Level = 20;
        _roll = 0.59; // gray kill: 12 * 20% = 2.4, rand_dither rounds up only when the roll reaches 0.6
        Assert.Equal(2, _service.Gain(ReputationSource.Kill, _player, 12, BootyBay, 1));
        _roll = 0.61;
        Assert.Equal(3, _service.Gain(ReputationSource.Kill, _player, 12, BootyBay, 1));
    }

    [Fact]
    public void KillRewards_AreTeamDependent_AndTeamAwardsFeedTheParent()
    {
        _service.RewardKill(_player, Victim(KillEntry, 1));
        Assert.Equal(10, _service.GetReputation(_player, BootyBay));
        Assert.Equal(0, _service.GetReputation(_player, Stormwind)); // horde half of the row

        _service.RewardKill(_player, Victim(TeamAwardEntry, 1));
        Assert.Equal(40, _service.GetReputation(_player, Stormwind));
        Assert.Equal(20, _service.GetReputation(_player, AllianceParent));

        _service.RewardKill(_player, Victim(900199, 1)); // no row
        _service.RewardKill(TestWorld.CreatePlayer(4, 0, 0, new FakeSession()), Victim(KillEntry, 1)); // untracked
        Assert.Equal(10, _service.GetReputation(_player, BootyBay));

        var orc = TestWorld.CreatePlayer(5, 0, 0, new FakeSession(), race: Race.Orc);
        _service.Track(orc, _service.Create(orc, CharacterReputationData.Empty));
        _service.RewardKill(orc, Victim(KillEntry, 1));
        Assert.Equal(0, _service.GetReputation(orc, BootyBay));
        Assert.Equal(-42000 + 20, _service.GetReputation(orc, Stormwind));
    }

    [Fact]
    public void KillRewards_StopAtTheMaxStandingRank_AndShrinkForGrayKills()
    {
        _service.RewardKill(_player, Victim(CappedEntry, 1));
        Assert.Equal(25, _service.GetReputation(_player, BootyBay));
        _service.SetReputation(_player, BootyBay, 2990);
        _service.RewardKill(_player, Victim(CappedEntry, 1)); // still Neutral: may cross into Friendly
        Assert.Equal(3015, _service.GetReputation(_player, BootyBay));
        _service.RewardKill(_player, Victim(CappedEntry, 1)); // Friendly is past the cap
        Assert.Equal(3015, _service.GetReputation(_player, BootyBay));

        _service.SetReputation(_player, BootyBay, 0);
        _player.Level = 20; // gray at 13 and below
        _service.RewardKill(_player, Victim(CappedEntry, 13));
        Assert.Equal(5, _service.GetReputation(_player, BootyBay));
        _service.RewardKill(_player, Victim(CappedEntry, 14));
        Assert.Equal(30, _service.GetReputation(_player, BootyBay));
    }

    [Fact]
    public void DuplicateKillRows_AreRejected()
        => Assert.Throws<ArgumentException>(() => new ReputationService(Factions,
            [new(1, BootyBay, 0, 7, false, 1, 0, false, 0, false), new(1, BootyBay, 0, 7, false, 2, 0, false, 0, false)]));

    [Fact]
    public void ClientToggles_PersistButAreNotEchoed()
    {
        _service.ModifyReputation(_player, BootyBay, 1);
        _session.Clear();
        _sink.Rows.Clear();
        Assert.True(_service.SetAtWar(_player, 0, true));
        Assert.True(_service.IsAtWar(_player, BootyBay));
        Assert.Equal([new CharacterReputationRow(1, BootyBay, 1, 3)], _sink.Rows);
        Assert.False(_service.SetAtWar(_player, 7, true));
        Assert.True(_service.SetInactive(_player, 0, true));
        Assert.Equal(0x23u, _sink.Rows[^1].Flags);
        Assert.False(_service.SetInactive(_player, 10, true));
        Assert.Empty(_session.Sent);

        Assert.True(_service.SetWatchedFaction(_player, 0));
        Assert.Equal(0, _player.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex));
        Assert.False(_service.SetWatchedFaction(_player, 10));
        Assert.True(_service.SetWatchedFaction(_player, -1));
        Assert.Equal(-1, _player.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex));
        Assert.Equal([0, -1], _sink.Watched);
    }

    [Fact]
    public void NpcSeams_RankAndPriceDiscount()
    {
        var npc = new NpcInfo(default, 1, 1, NpcFlags.Vendor, 0, 0, 0, 0, 0, true, false, false, false, 0, FactionId: Stormwind);
        IPlayerReputation seam = _service;
        Assert.Equal(1f, _service.GetPriceDiscount(_player, npc));
        Assert.Equal((byte)ReputationRank.Neutral, seam.GetReputationRank(_player, Stormwind));
        _service.SetReputation(_player, Stormwind, 8999);
        Assert.Equal(1f, _service.GetPriceDiscount(_player, npc));
        _service.SetReputation(_player, Stormwind, 9000);
        Assert.Equal(0.9f, _service.GetPriceDiscount(_player, npc));
        Assert.Equal(1f, _service.GetPriceDiscount(_player, npc with { FactionId = 0 }));
        Assert.Equal((byte)ReputationRank.Honored, seam.GetReputationRank(_player, Stormwind));
    }

    [Fact]
    public void InitializeFactions_ForAnUntrackedPlayer_IsEmpty()
    {
        Player stranger = TestWorld.CreatePlayer(6, 0, 0, new FakeSession());
        Assert.Equal(ReputationPackets.InitializeFactions(null), _service.BuildInitializeFactions(stranger));
        byte[] tracked = _service.BuildInitializeFactions(_player);
        Assert.Equal(0x11, tracked[4 + (7 * 5)]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(tracked.AsSpan(5 + (7 * 5))));
    }

    private static Creature Victim(uint entry, byte level)
    {
        var template = new CreatureTemplate { Entry = entry, Name = "Synthetic victim", Faction = 3, MinLevel = level, MaxLevel = level };
        var spawn = new CreatureSpawn { Guid = 900200 + entry, Entry = entry, MapId = 0, X = 0, Y = 0, Z = 0 };
        return new Creature(spawn.Guid, template, spawn, CreatureContent.Empty, new Random(1));
    }
}
