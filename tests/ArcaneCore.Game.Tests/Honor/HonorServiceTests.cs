using System.Buffers.Binary;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>The world-thread honor owner (vmangos HonorMgr::Add / Update / Reset, HonorMgr.cpp:686-698, 792-940).</summary>
public sealed class HonorServiceTests
{
    private const uint Today = 20_000;
    private const uint WeekBegin = 19_997;

    private readonly FixedHonorClock _clock = new(Today);
    private readonly RecordingHonorSink _sink = new();
    private readonly FakeSession _session = new();
    private readonly Player _player;
    private readonly HonorService _service;

    public HonorServiceTests()
    {
        _player = TestWorld.CreatePlayer(1, 0, 0, _session);
        _service = new HonorService(new HonorOptions(), _clock, () => WeekBegin, _sink);
        _service.Track(_player, _service.Create(_player, CharacterHonorData.Empty));
        _session.Clear();
    }

    private Player Victim(uint guid, float rankPoints = 0f, Race race = Race.Orc)
    {
        Player victim = TestWorld.CreatePlayer(guid, 0, 0, new FakeSession(), race: race);
        _service.Track(victim, _service.Create(victim, CharacterHonorData.Empty with { State = CharacterHonorState.Empty with { RankPoints = rankPoints } }));
        return victim;
    }

    private static Creature MakeCreature(uint entry, bool racialLeader = false, byte level = 60)
    {
        CreatureTemplate template = CreatureTestSupport.Template(entry, b => { b.MinLevel = level; b.MaxLevel = level; }) with { RacialLeader = racialLeader };
        return new Creature(entry, template, null, CreatureContent.Empty, new Random(1));
    }

    [Fact]
    public void Zero_points_are_ignored_and_untracked_players_get_nothing()
    {
        Assert.False(_service.Add(_player, 0f, HonorKind.Honorable, null));
        Assert.Empty(_session.Sent);
        Assert.Empty(_sink.Rows);
        Player stranger = TestWorld.CreatePlayer(9, 0, 0, new FakeSession());
        Assert.False(_service.Add(stranger, 10f, HonorKind.Honorable, null));
    }

    [Fact]
    public void A_player_kill_records_the_row_and_sends_the_pvp_credit_with_the_victims_rank()
    {
        Player victim = Victim(2, rankPoints: 2500f); // internal rank 6
        Assert.True(_service.Add(_player, 188.3f, HonorKind.Honorable, victim));

        Assert.Equal([new HonorCpRecord(4, 2, 188.3f, Today, (byte)HonorKind.Honorable)], _sink.Rows);
        (WorldOpcode opcode, byte[] payload) = _session.Next();
        Assert.Equal(WorldOpcode.SmsgPvpCredit, opcode);
        Assert.Equal(188, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(victim.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12)));
        Assert.Equal(16, payload.Length);
    }

    [Fact]
    public void An_unranked_player_victim_is_shown_as_at_least_scout()
    {
        Player victim = Victim(2);
        _service.Add(_player, 50f, HonorKind.Honorable, victim);
        byte[] payload = _session.Next().Payload;
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12)));
    }

    [Fact]
    public void A_creature_source_records_type_three_with_its_entry_and_a_racial_leader_shows_rank_19()
    {
        Creature leader = MakeCreature(3057, racialLeader: true);
        Creature plain = MakeCreature(299);
        _service.Add(_player, HonorKillPoints.RacialLeaderHonor, HonorKind.Honorable, leader);
        Assert.Equal(new HonorCpRecord(3, 3057, 488f, Today, 1), _sink.Rows[0]);
        byte[] payload = _session.Next().Payload;
        Assert.Equal(488, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(19u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12)));

        _service.Add(_player, 5f, HonorKind.Quest, plain);
        payload = _session.Next().Payload;
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12)));
        Assert.Equal(plain.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4)));
    }

    [Fact]
    public void Without_a_source_the_row_names_the_owner_with_type_zero_and_the_credit_has_no_victim()
    {
        _service.Add(_player, 100f, HonorKind.Other, null);
        Assert.Equal([new HonorCpRecord(0, 1, 100f, Today, (byte)HonorKind.Other)], _sink.Rows);
        byte[] payload = _session.Next().Payload;
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12)));
    }

    [Fact]
    public void Dishonorable_kills_cost_rank_points_at_once_floor_at_zero_and_send_a_negative_credit()
    {
        _service.SetRankPoints(_player, 500f);
        _sink.Clear();
        _session.Clear();
        Creature civilian = MakeCreature(1000);

        Assert.True(_service.Add(_player, 100f, HonorKind.Dishonorable, civilian));
        Assert.Equal(400f, _service.For(_player)!.RankPoints);
        Assert.Equal(-100, BinaryPrimitives.ReadInt32LittleEndian(_session.Next().Payload));

        Assert.True(_service.Add(_player, 1000f, HonorKind.Dishonorable, civilian));
        Assert.Equal(0f, _service.For(_player)!.RankPoints);
        Assert.Equal(2u, _player.GetUInt16(UpdateFields.PlayerFieldSessionKills, 1));
        Assert.Equal(2u, _player.GetUInt32(UpdateFields.PlayerFieldLifetimeDishonorbaleKills));
        Assert.Equal(0u, _player.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution)); // DK adds no contribution
        Assert.Equal(0f, _sink.States[^1].RankPoints);
    }

    [Fact]
    public void Update_derives_every_honor_tab_field_from_the_rows_and_the_stored_totals()
    {
        var rows = new List<HonorCpRecord>
        {
            new(4, 10, 100f, Today, 1), new(4, 11, 50f, Today, 1), new(3, 12, 20f, Today, 2),            // today: 2 HK, 1 DK
            new(4, 13, 30f, Today - 1, 1), new(0, 1, 5f, Today - 1, 3),                                  // yesterday
            new(4, 14, 10f, Today - 2, 1),                                                               // earlier this week
            new(4, 15, 999f, WeekBegin - 7, 1), new(3, 16, 40f, WeekBegin - 7, 2),                       // previous week
        };
        var state = new CharacterHonorState(5500f, 12, 9, 4, 44.5f, 7, 3, 0, false);
        Player p = TestWorld.CreatePlayer(5, 0, 0, new FakeSession());
        _service.Track(p, _service.Create(p, new CharacterHonorData(state, rows)));

        Assert.Equal(2u, p.GetUInt16(UpdateFields.PlayerFieldSessionKills, 0));
        Assert.Equal(1u, p.GetUInt16(UpdateFields.PlayerFieldSessionKills, 1));
        Assert.Equal(1u, p.GetUInt32(UpdateFields.PlayerFieldYesterdayKills));
        Assert.Equal(35u, p.GetUInt32(UpdateFields.PlayerFieldYesterdayContribution));
        Assert.Equal(4u, p.GetUInt32(UpdateFields.PlayerFieldThisWeekKills));
        Assert.Equal(195u, p.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution));
        Assert.Equal(4u, p.GetUInt32(UpdateFields.PlayerFieldLastWeekKills));
        Assert.Equal(44u, p.GetUInt32(UpdateFields.PlayerFieldLastWeekContribution));
        Assert.Equal(9u, p.GetUInt32(UpdateFields.PlayerFieldLastWeekRank));
        Assert.Equal(11u, p.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills));  // stored 7 + 4 this week
        Assert.Equal(5u, p.GetUInt32(UpdateFields.PlayerFieldLifetimeDishonorbaleKills)); // stored 3 + both DK rows
        Assert.Equal(7, p.GetByte(UpdateFields.PlayerBytes3, 3));
        Assert.Equal(12, p.GetByte(UpdateFields.PlayerFieldBytes, 3));
        Assert.Equal(25, p.GetByte(UpdateFields.PlayerFieldBytes2, 0));
    }

    [Fact]
    public void The_highest_rank_rises_only_when_the_current_visual_rank_is_higher_and_positive()
    {
        Player p = TestWorld.CreatePlayer(5, 0, 0, new FakeSession());
        _service.Track(p, _service.Create(p, CharacterHonorData.Empty with { State = CharacterHonorState.Empty with { RankPoints = 5500f, HighestRank = 6 } }));
        Assert.Equal(7, p.GetByte(UpdateFields.PlayerFieldBytes, 3));

        Player higher = TestWorld.CreatePlayer(6, 0, 0, new FakeSession());
        _service.Track(higher, _service.Create(higher, CharacterHonorData.Empty with { State = CharacterHonorState.Empty with { RankPoints = 5500f, HighestRank = 12 } }));
        Assert.Equal(12, higher.GetByte(UpdateFields.PlayerFieldBytes, 3));

        Player negative = TestWorld.CreatePlayer(7, 0, 0, new FakeSession());
        _service.Track(negative, _service.Create(negative, CharacterHonorData.Empty with { State = CharacterHonorState.Empty with { RankPoints = -3000f } }));
        Assert.Equal(0, negative.GetByte(UpdateFields.PlayerFieldBytes, 3));
        Assert.Equal(3, negative.GetByte(UpdateFields.PlayerBytes3, 3));
    }

    [Fact]
    public void A_negative_contribution_total_clamps_to_zero_in_the_fields()
    {
        _service.Add(_player, -50f, HonorKind.Other, null);
        Assert.Equal(0u, _player.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution));
        _clock.Day = Today + 1; // the -50 row is now yesterday's
        _service.Update(_player);
        Assert.Equal(0u, _player.GetUInt32(UpdateFields.PlayerFieldYesterdayContribution));
    }

    [Fact]
    public void Total_kills_today_counts_only_the_same_victim_of_the_same_type_on_todays_date()
    {
        Player victim = Victim(2);
        Player other = Victim(3);
        _service.Add(_player, 10f, HonorKind.Honorable, victim);
        _service.Add(_player, 10f, HonorKind.Honorable, victim);
        _service.Add(_player, 10f, HonorKind.Honorable, other);
        Creature creature = MakeCreature(2); // same number, different type: not the same victim
        _service.Add(_player, 10f, HonorKind.Honorable, creature);
        Assert.Equal(2u, _service.TotalKillsToday(_player, victim));
        Assert.Equal(1u, _service.TotalKillsToday(_player, other));
        Assert.Equal(1u, _service.TotalKillsToday(_player, creature));
        _clock.Day = Today + 1;
        Assert.Equal(0u, _service.TotalKillsToday(_player, victim));
    }

    [Fact]
    public void Kill_points_use_the_killers_own_history_with_the_victim()
    {
        Player victim = Victim(2, rankPoints: 2500f); // visual rank 2
        _player.Level = 60;
        victim.Level = 60;
        float first = _service.KillPoints(_player, victim, 1);
        Assert.Equal(HonorKillPoints.Honorable(60, 60, 2, 0, 1), first);
        _service.Add(_player, first, HonorKind.Honorable, victim);
        Assert.Equal(HonorKillPoints.Honorable(60, 60, 2, 1, 1), _service.KillPoints(_player, victim, 1));
    }

    [Fact]
    public void Reset_clears_state_and_rows_and_asks_the_sink_to_delete()
    {
        _service.Add(_player, 100f, HonorKind.Honorable, null);
        _service.SetRankPoints(_player, 9000f);
        _sink.Clear();
        _service.Reset(_player);
        Assert.Equal(1, _sink.Resets);
        Assert.Equal(0f, _service.For(_player)!.RankPoints);
        Assert.Equal(0u, _player.GetUInt32(UpdateFields.PlayerFieldThisWeekKills));
        Assert.Equal(0, _player.GetByte(UpdateFields.PlayerBytes3, 3));
        Assert.Empty(_service.For(_player)!.Rows);
    }

    [Fact]
    public void The_player_honor_seam_reports_current_highest_and_visual_rank()
    {
        _service.Track(_player, _service.Create(_player, CharacterHonorData.Empty with { State = CharacterHonorState.Empty with { RankPoints = 5500f, HighestRank = 12 } }));
        IPlayerHonor honor = _service;
        Assert.Equal((byte)7, honor.CurrentRank(_player));
        Assert.Equal((byte)12, honor.HighestRank(_player));
        Assert.Equal((sbyte)3, honor.VisualRank(_player));
        Player stranger = TestWorld.CreatePlayer(77, 0, 0, new FakeSession());
        Assert.Equal((byte)0, honor.CurrentRank(stranger));
        Assert.Equal((byte)0, honor.HighestRank(stranger));
    }

    [Fact]
    public void The_awards_seam_adds_honor_for_other_lanes()
    {
        IHonorAwards awards = _service;
        Assert.True(awards.Add(_player, 20f, HonorKind.Bonus, null));
        Assert.Equal((byte)HonorKind.Bonus, _sink.Rows.Single().Type);
    }

    [Fact]
    public void Racial_leader_exclusions_come_from_the_options()
    {
        var options = new HonorOptions { RacialLeaderExcludedEntries = [15423] };
        var service = new HonorService(options, _clock, () => WeekBegin, _sink);
        Assert.True(service.IsRacialLeader(MakeCreature(3057, racialLeader: true)));
        Assert.False(service.IsRacialLeader(MakeCreature(15423, racialLeader: true)));
        Assert.False(service.IsRacialLeader(MakeCreature(299)));
    }

    [Fact]
    public void Options_default_to_retail()
    {
        var o = new HonorOptions();
        Assert.True(o.Enabled);
        Assert.True(o.DishonorableKills);
        Assert.Equal(0u, o.MinHonorKills);
        Assert.Equal(0.2f, o.RpDecay);
        Assert.Equal(3u, o.MaintenanceDay);
        Assert.Equal(0, o.TimeZoneOffsetHours);
        Assert.Equal(0u, o.PoolSizePerFaction);
        Assert.False(o.CityProtector);
        Assert.Empty(o.RacialLeaderExcludedEntries);
        Assert.Equal(HonorMaintenanceMode.Live, o.MaintenanceMode);
        Assert.Equal(new HonorMaintenanceOptions(0.2f, 0, 0), o.Maintenance);
    }

    [Fact]
    public void The_clock_turns_unix_time_into_a_game_day_with_the_configured_offset()
    {
        Assert.Equal(Today, new FixedHonorClock(Today).GameDay(0));
        Assert.Equal(Today + 1, new FixedHonorClock(Today) { SecondsIntoDay = 86_000 }.GameDay(3600));
    }
}

internal sealed class FixedHonorClock(uint day) : HonorClock
{
    public uint Day { get; set; } = day;

    public long SecondsIntoDay { get; set; } = 100;

    /// <summary>Extra milliseconds on top (the PvP damage history ages in milliseconds).</summary>
    public long ExtraMs { get; set; }

    public override long UnixMilliseconds => (((Day * 86_400L) + SecondsIntoDay) * 1000) + ExtraMs;
}

internal sealed class RecordingHonorSink : IHonorSink
{
    public List<HonorCpRecord> Rows { get; } = [];

    public List<CharacterHonorState> States { get; } = [];

    public int Resets { get; private set; }

    public void CpAdded(Player player, HonorCpRecord row) => Rows.Add(row);

    public void StateChanged(Player player, CharacterHonorState state) => States.Add(state);

    public void Reset(Player player) => Resets++;

    public void Clear()
    {
        Rows.Clear();
        States.Clear();
        Resets = 0;
    }
}
