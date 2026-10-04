using ArcaneCore.Game.Battlegrounds;
using Xunit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// Wire layouts of the battleground packets, with hand-written expected bytes (little endian) from vmangos
/// Server/Packets/Battleground.cpp, BattleGroundHandler.cpp and the gtker/wow_messages 1.12 definitions.
/// </summary>
public sealed class BattlegroundPacketTests
{
    private static byte[] Bytes(params int[] values) => [.. values.Select(v => (byte)v)];

    [Fact]
    public void AnEmptyStatusIsTheQueueSlotAndAZeroMap()
    {
        // BattlefieldStatusEmpty (Battleground.cpp:113-119): u32 queue slot, u32 0.
        Assert.Equal(Bytes(1, 0, 0, 0, 0, 0, 0, 0), BattlegroundPackets.BuildBattlefieldStatusEmpty(1));
    }

    [Fact]
    public void ANoneStatusIsBuiltAsTheEmptyForm()
    {
        // BuildBattleGroundStatusPacket (BattleGroundMgr.cpp:1036-1043): status 0 means the empty packet.
        Assert.Equal(Bytes(2, 0, 0, 0, 0, 0, 0, 0), BattlegroundPackets.BuildBattlefieldStatus(2, 489, 5, 3, BattlegroundStatus.None, 123, 456));
    }

    [Fact]
    public void AQueuedStatusCarriesTheAverageWaitAndTheTimeInQueue()
    {
        // u32 slot, u32 map 489, u8 bracket 5, u32 client instance 2, u32 status 1, u32 90000, u32 7000 (Battleground.cpp:87-101).
        byte[] expected = Bytes(0, 0, 0, 0, 0xE9, 0x01, 0, 0, 5, 2, 0, 0, 0, 1, 0, 0, 0, 0x90, 0x5F, 0x01, 0, 0x58, 0x1B, 0, 0);
        Assert.Equal(expected, BattlegroundPackets.BuildBattlefieldStatus(0, 489, 5, 2, BattlegroundStatus.WaitQueue, 90_000, 7_000));
        Assert.Equal(25, expected.Length);
    }

    [Fact]
    public void AnInvitationStatusWritesOnlyOneTime()
    {
        // WAIT_JOIN writes time1 (80000 = 0x13880) and no time2 (Battleground.cpp:97-101).
        byte[] expected = Bytes(1, 0, 0, 0, 0xE9, 0x01, 0, 0, 5, 7, 0, 0, 0, 2, 0, 0, 0, 0x80, 0x38, 0x01, 0);
        Assert.Equal(expected, BattlegroundPackets.BuildBattlefieldStatus(1, 489, 5, 7, BattlegroundStatus.WaitJoin, 80_000, 999));
        Assert.Equal(21, expected.Length);
    }

    [Fact]
    public void AnInProgressStatusCarriesTheAutoLeaveTimeAndTheStartTime()
    {
        byte[] expected = Bytes(2, 0, 0, 0, 0xE9, 0x01, 0, 0, 3, 1, 0, 0, 0, 3, 0, 0, 0, 0xC0, 0xD4, 0x01, 0, 0x10, 0x27, 0, 0);
        Assert.Equal(expected, BattlegroundPackets.BuildBattlefieldStatus(2, 489, 3, 1, BattlegroundStatus.InProgress, 120_000, 10_000));
    }

    [Fact]
    public void ABattlefieldListIsTheMasterTheMapTheBracketAndTheInstanceIds()
    {
        // Battleground.cpp:203-214: u64 battlemaster, u32 map, u8 bracket, u32 count, u32 ids.
        var master = ObjectGuid.WithEntry(HighGuid.Unit, 0x1234, 0x345678);
        byte[] actual = BattlegroundPackets.BuildBattlefieldList(master, 489, 4, [1, 5]);

        Assert.Equal(8 + 4 + 1 + 4 + 8, actual.Length);
        Assert.Equal(BitConverter.GetBytes(master.Value), actual[..8]);
        Assert.Equal(Bytes(0xE9, 0x01, 0, 0, 4, 2, 0, 0, 0, 1, 0, 0, 0, 5, 0, 0, 0), actual[8..]);
    }

    [Fact]
    public void AnEmptyBattlefieldListHasACountOfZero()
    {
        byte[] actual = BattlegroundPackets.BuildBattlefieldList(ObjectGuid.Empty, 489, 0, []);
        Assert.Equal(Bytes(0, 0, 0, 0, 0, 0, 0, 0, 0xE9, 0x01, 0, 0, 0, 0, 0, 0, 0), actual);
    }

    [Fact]
    public void ARunningScoreboardHasOneZeroByteThenTheRows()
    {
        // MSG_PVP_LOG_DATA (Battleground.cpp:141-166): u8 ended, [u8 winner], u32 count, rows.
        var snapshot = new PvpLogSnapshot(false, BattlegroundWinner.None,
        [
            new PvpLogRow(ObjectGuid.Player(0x0A), 4, 1, 2, 3, 4, [5, 6]),
        ]);

        byte[] expected = Bytes(
            0,
            1, 0, 0, 0,
            0x0A, 0, 0, 0, 0, 0, 0, 0,
            4, 0, 0, 0,        // rank
            1, 0, 0, 0,        // killing blows
            2, 0, 0, 0,        // honorable kills
            3, 0, 0, 0,        // deaths
            4, 0, 0, 0,        // bonus honor
            2, 0, 0, 0,        // extra field count
            5, 0, 0, 0,        // flag captures
            6, 0, 0, 0);       // flag returns
        Assert.Equal(expected, BattlegroundPackets.BuildPvpLogData(snapshot));
    }

    [Theory]
    [InlineData(BattlegroundWinner.Horde, 0)]
    [InlineData(BattlegroundWinner.Alliance, 1)]
    [InlineData(BattlegroundWinner.None, 2)]
    public void AnEndedScoreboardCarriesTheWinnerByte(BattlegroundWinner winner, int wire)
    {
        byte[] actual = BattlegroundPackets.BuildPvpLogData(new PvpLogSnapshot(true, winner, []));
        Assert.Equal(Bytes(1, wire, 0, 0, 0, 0), actual);
    }

    [Fact]
    public void ARowWithNoExtraFieldsWritesAZeroCount()
    {
        byte[] actual = BattlegroundPackets.BuildPvpLogData(new PvpLogSnapshot(false, BattlegroundWinner.None, [new PvpLogRow(ObjectGuid.Player(1), 4, 0, 0, 0, 0, [])]));
        Assert.Equal(1 + 4 + 8 + (5 * 4) + 4, actual.Length);
        Assert.Equal(Bytes(0, 0, 0, 0), actual[^4..]);
    }

    [Fact]
    public void TheScoreboardNeverCarriesMoreThanEightyRows()
    {
        List<PvpLogRow> rows = [.. Enumerable.Range(1, 95).Select(i => new PvpLogRow(ObjectGuid.Player((uint)i), 4, 0, 0, 0, 0, []))];
        byte[] actual = BattlegroundPackets.BuildPvpLogData(new PvpLogSnapshot(false, BattlegroundWinner.None, rows));

        Assert.Equal(80u, BitConverter.ToUInt32(actual, 1));
        Assert.Equal(1 + 4 + (80 * (8 + 24)), actual.Length);
    }

    [Fact]
    public void PlayerPositionsAreTheTeammatesThenTheCarriers()
    {
        // BattleGroundHandler.cpp:274-324: u32 count, (u64 guid, f32 x, f32 y) each, then u8 carriers and their entries.
        byte[] actual = BattlegroundPackets.BuildPlayerPositions(
            [(ObjectGuid.Player(7), 1.5f, -2.0f), (ObjectGuid.Player(8), 0f, 100f)],
            [(ObjectGuid.Player(9), 3f, 4f)]);

        Assert.Equal(4 + (2 * 16) + 1 + 16, actual.Length);
        Assert.Equal(2u, BitConverter.ToUInt32(actual, 0));
        Assert.Equal(7ul, BitConverter.ToUInt64(actual, 4));
        Assert.Equal(1.5f, BitConverter.ToSingle(actual, 12));
        Assert.Equal(-2.0f, BitConverter.ToSingle(actual, 16));
        Assert.Equal(8ul, BitConverter.ToUInt64(actual, 20));
        Assert.Equal(100f, BitConverter.ToSingle(actual, 32));
        Assert.Equal(1, actual[36]);
        Assert.Equal(9ul, BitConverter.ToUInt64(actual, 37));
        Assert.Equal(4f, BitConverter.ToSingle(actual, 49));
    }

    [Fact]
    public void PlayerPositionsWithNobodyAreAZeroCountAndAZeroCarrierByte()
    {
        Assert.Equal(Bytes(0, 0, 0, 0, 0), BattlegroundPackets.BuildPlayerPositions([], []));
    }

    [Fact]
    public void JoinedAndLeftCarryTheFullGuid()
    {
        Assert.Equal(Bytes(5, 0, 0, 0, 0, 0, 0, 0), BattlegroundPackets.BuildPlayerJoined(ObjectGuid.Player(5)));
        Assert.Equal(Bytes(6, 0, 0, 0, 0, 0, 0, 0), BattlegroundPackets.BuildPlayerLeft(ObjectGuid.Player(6)));
    }

    [Fact]
    public void GroupJoinedCarriesTheMapOrTheFailureCodes()
    {
        // The result is a map id on success (BattleGroundHandler.cpp:256) and these codes otherwise (BattleGroundMgr.h:77-82).
        Assert.Equal(Bytes(0xE9, 0x01, 0, 0), BattlegroundPackets.BuildGroupJoined(489));
        Assert.Equal(Bytes(0xFE, 0xFF, 0xFF, 0xFF), BattlegroundPackets.BuildGroupJoined(BattlegroundPackets.GroupJoinDeserters));
        Assert.Equal(Bytes(0xFF, 0xFF, 0xFF, 0xFF), BattlegroundPackets.BuildGroupJoined(BattlegroundPackets.GroupJoinFailed));
    }

    [Fact]
    public void WinAndLoseAreEmptyAndSoundAndWorldStateAreTwoAndOneWords()
    {
        Assert.Empty(BattlegroundPackets.BuildBattlefieldWin());
        Assert.Empty(BattlegroundPackets.BuildBattlefieldLose());
        Assert.Equal(Bytes(0xF8, 0x1F, 0, 0), BattlegroundPackets.BuildPlaySound(8184));
        Assert.Equal(Bytes(0x1A, 0x06, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF), BattlegroundPackets.BuildUpdateWorldState(1562, uint.MaxValue));
    }

    // ---------------------------------------------------------------- client packets

    [Fact]
    public void BattlemasterJoinIsGuidMapInstanceAndTheGroupFlag()
    {
        byte[] body = Bytes(0x11, 0, 0, 0, 0, 0, 0, 0, 0xE9, 0x01, 0, 0, 7, 0, 0, 0, 1);
        Assert.True(BattlegroundPackets.TryParseBattlemasterJoin(body, out BattlemasterJoin join));
        Assert.Equal(new BattlemasterJoin(ObjectGuid.Player(0x11), 489, 7, true), join);

        body[16] = 0;
        Assert.True(BattlegroundPackets.TryParseBattlemasterJoin(body, out join));
        Assert.False(join.JoinAsGroup);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(18)]
    public void BattlemasterJoinOfTheWrongSizeIsRefused(int length) => Assert.False(BattlegroundPackets.TryParseBattlemasterJoin(new byte[length], out _));

    [Fact]
    public void BattlefieldPortIsMapAndAction()
    {
        Assert.True(BattlegroundPackets.TryParseBattlefieldPort(Bytes(0xE9, 0x01, 0, 0, 1), out BattlefieldPort port));
        Assert.Equal(new BattlefieldPort(489, 1), port);
        Assert.True(BattlegroundPackets.TryParseBattlefieldPort(Bytes(0xE9, 0x01, 0, 0, 0), out port));
        Assert.Equal(0, port.Action);
        Assert.False(BattlegroundPackets.TryParseBattlefieldPort(Bytes(0xE9, 0x01, 0, 0), out _));
        Assert.False(BattlegroundPackets.TryParseBattlefieldPort(Bytes(0xE9, 0x01, 0, 0, 1, 0), out _));
    }

    [Fact]
    public void SingleMapPacketsAreAMapOnly()
    {
        Assert.True(BattlegroundPackets.TryParseMap(Bytes(0xE9, 0x01, 0, 0), out uint map));
        Assert.Equal(489u, map);
        Assert.False(BattlegroundPackets.TryParseMap(Bytes(1, 2, 3), out _));
        Assert.False(BattlegroundPackets.TryParseMap(Bytes(1, 2, 3, 4, 5), out _));
    }

    [Fact]
    public void GuidOnlyPacketsAreExactlyEightBytes()
    {
        Assert.True(BattlegroundPackets.TryParseGuid(Bytes(9, 0, 0, 0, 0, 0, 0, 0), out ObjectGuid guid));
        Assert.Equal(ObjectGuid.Player(9), guid);
        Assert.False(BattlegroundPackets.TryParseGuid(Bytes(9, 0, 0, 0, 0, 0, 0), out _));
        Assert.False(BattlegroundPackets.TryParseGuid(new byte[9], out _));
    }

    [Fact]
    public void AreaSpiritHealerTimeIsGuidAndMilliseconds()
    {
        // wow_messages smsg_area_spirit_healer_time.wowm: guid, u32 next resurrect time.
        Assert.Equal(Bytes(3, 0, 0, 0, 0, 0, 0, 0, 0x30, 0x75, 0, 0), BattlegroundPackets.BuildAreaSpiritHealerTime(ObjectGuid.Player(3), 30_000));
    }
}
