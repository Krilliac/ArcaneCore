using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class StartingZoneProbeTests
{
    [Fact]
    public void BattleStanceUsesTheServersBytes1Byte2AndRejectsTheWrongField()
    {
        Assert.True(StartingZoneProbe.IsBattleStance(new Dictionary<int, uint>
            { [UpdateFields.UnitFieldBytes1] = 17u << 16 }));
        Assert.False(StartingZoneProbe.IsBattleStance(new Dictionary<int, uint>
            { [UpdateFields.UnitFieldBytes2] = 17u << 24 }));
        Assert.False(StartingZoneProbe.IsBattleStance(new Dictionary<int, uint>
            { [UpdateFields.UnitFieldBytes1] = 18u << 16 }));
    }
    [Fact]
    public void MovementUsesVanillaUnflaggedStopLayout()
    {
        byte[] body = StartingZoneProbe.Movement(1, 2, 3, 4, 1000);
        Assert.Equal(28, body.Length);
        var reader = new PacketReader(body);
        MovementInfo info = MovementInfo.Read(ref reader);
        Assert.Equal(MovementFlags.None, info.Flags);
        Assert.Equal((uint)1000, info.Time);
        Assert.Equal(1f, info.X);
        Assert.Equal(4f, info.Orientation);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void LogoutSuccessParserAcceptsOnlyFiveByteSuccess(bool success, byte delayed)
    {
        byte[] payload = [success ? (byte)0 : (byte)1, 0, 0, 0, delayed];
        Assert.Equal(success, StartingZoneProbe.IsLogoutSuccess(payload));
    }

    [Fact]
    public void SpellGoParserRequiresExactCasterSpellAndSelfHit()
    {
        ObjectGuid player = ObjectGuid.Player(7);
        SpellCastTargets targets = new();
        byte[] valid = SpellPackets.BuildSpellGo(player, player, StartingZoneProbe.LearnedSpell,
            SpellCastFlags.Unknown9, [player], [], targets);
        Assert.True(StartingZoneProbe.IsSelfSpellGo(valid, player.Value, StartingZoneProbe.LearnedSpell));

        byte[] wrongCaster = SpellPackets.BuildSpellGo(ObjectGuid.Player(8), player, StartingZoneProbe.LearnedSpell,
            SpellCastFlags.Unknown9, [player], [], targets);
        byte[] wrongSpell = SpellPackets.BuildSpellGo(player, player, 9999,
            SpellCastFlags.Unknown9, [player], [], targets);
        byte[] noSelfHit = SpellPackets.BuildSpellGo(player, player, StartingZoneProbe.LearnedSpell,
            SpellCastFlags.Unknown9, [ObjectGuid.Player(8)], [], targets);
        Assert.False(StartingZoneProbe.IsSelfSpellGo(wrongCaster, player.Value, StartingZoneProbe.LearnedSpell));
        Assert.False(StartingZoneProbe.IsSelfSpellGo(wrongSpell, player.Value, StartingZoneProbe.LearnedSpell));
        Assert.False(StartingZoneProbe.IsSelfSpellGo(noSelfHit, player.Value, StartingZoneProbe.LearnedSpell));
        Assert.False(StartingZoneProbe.IsSelfSpellGo([.. valid, 0, 0], player.Value, StartingZoneProbe.LearnedSpell));

        byte[] unrelatedGuid = [.. SpellPackets.BuildSpellGo(player, player, StartingZoneProbe.LearnedSpell,
            SpellCastFlags.Unknown9, [], [], targets), .. BitConverter.GetBytes(StartingZoneProbe.LearnedSpell)];
        Assert.False(StartingZoneProbe.IsSelfSpellGo(unrelatedGuid, player.Value, StartingZoneProbe.LearnedSpell));
    }

    [Fact]
    public void QuestIncompleteParserUsesEveryCanonicalSlotAndRejectsCompleteOrForeignRows()
    {
        var fields = new Dictionary<int, uint>
        {
            [UpdateFields.PlayerQuestLog11 + (5 * QuestConstants.FieldsPerSlot)] = 7,
            [UpdateFields.PlayerQuestLog11 + (5 * QuestConstants.FieldsPerSlot) + 1] = 0,
        };
        Assert.True(StartingZoneProbe.IsQuestIncomplete(fields, 7));
        Assert.False(StartingZoneProbe.IsQuestIncomplete(new Dictionary<int, uint>
        {
            [UpdateFields.PlayerQuestLog11] = 8,
            [UpdateFields.PlayerQuestLog11 + 1] = 0,
        }, 7));
        Assert.False(StartingZoneProbe.IsQuestIncomplete(new Dictionary<int, uint>
        {
            [UpdateFields.PlayerQuestLog11] = 7,
            [UpdateFields.PlayerQuestLog11 + 1] = QuestConstants.SlotStateComplete << 24,
        }, 7));
        Assert.False(StartingZoneProbe.IsQuestIncomplete(new Dictionary<int, uint>
        {
            [UpdateFields.PlayerQuestLog11] = 7,
            [UpdateFields.PlayerQuestLog11 + 1] = QuestConstants.SlotStateFail << 24,
        }, 7));
        Assert.True(StartingZoneProbe.IsQuestIncomplete(new Dictionary<int, uint>
        {
            [UpdateFields.PlayerQuestLog11] = 7,
        }, 7));
        Assert.False(StartingZoneProbe.IsQuestIncomplete(new Dictionary<int, uint>
        {
            [UpdateFields.PlayerQuestLog11] = 0,
            [UpdateFields.PlayerQuestLog11 + 1] = 0,
            [UpdateFields.PlayerQuestLog11 + 2] = 0,
        }, 7));
    }

    [Fact]
    public void QuestRefusalParserAcceptsOnlyExactVanillaBodies()
    {
        byte[] invalid = [3, 0, 0, 0];
        byte[] failed = [7, 0, 0, 0, 9, 0, 0, 0];
        byte[] full = [];
        Assert.Contains("invalid reason 3", StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestgiverQuestInvalid, invalid, 7));
        Assert.Contains("failed with reason 9", StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestgiverQuestFailed, failed, 7));
        Assert.Contains("quest log is full", StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestlogFull, full, 7));

        byte[] malformedInvalid = [3, 0, 0];
        byte[] foreignFailed = [8, 0, 0, 0, 9, 0, 0, 0];
        Assert.Throws<MockProtocolException>(() => StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestgiverQuestInvalid, malformedInvalid, 7));
        Assert.Throws<MockProtocolException>(() => StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestgiverQuestFailed, foreignFailed, 7));
        Assert.Throws<MockProtocolException>(() => StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestlogFull, [1], 7));
        Assert.Throws<MockProtocolException>(() => StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgGossipComplete, full, 7));
    }

    [Fact]
    public void QuestCompletionParserSeparatesCompleteFailAndIncompleteStates()
    {
        int id = UpdateFields.PlayerQuestLog11 + (2 * QuestConstants.FieldsPerSlot);
        var complete = new Dictionary<int, uint> { [id] = 7, [id + 1] = QuestConstants.SlotStateComplete << 24 };
        var failed = new Dictionary<int, uint> { [id] = 7, [id + 1] = QuestConstants.SlotStateFail << 24 };
        var both = new Dictionary<int, uint> { [id] = 7, [id + 1] = (QuestConstants.SlotStateComplete | QuestConstants.SlotStateFail) << 24 };
        var incomplete = new Dictionary<int, uint> { [id] = 7, [id + 1] = 0 };
        var foreign = new Dictionary<int, uint> { [id] = 8, [id + 1] = QuestConstants.SlotStateComplete << 24 };

        Assert.True(StartingZoneProbe.IsQuestComplete(complete, 7));
        Assert.False(StartingZoneProbe.IsQuestComplete(both, 7));
        Assert.False(StartingZoneProbe.IsQuestComplete(failed, 7));
        Assert.False(StartingZoneProbe.IsQuestComplete(incomplete, 7));
        Assert.False(StartingZoneProbe.IsQuestComplete(foreign, 7));
        Assert.False(StartingZoneProbe.IsQuestIncomplete(complete, 7));
        Assert.False(StartingZoneProbe.IsQuestIncomplete(both, 7));
        Assert.False(StartingZoneProbe.IsQuestIncomplete(failed, 7));
    }

    [Fact]
    public void ParserRefusesRemoteRealmAndUnsetPasswordWithoutEmittingSecrets()
    {
        string variable = "ARCANE_STARTING_ZONE_UNSET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, null);
        Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--account", "A", "--password-env", variable]));
        Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--account", "A", "--password-env", variable, "--realm", "10.0.0.1:3724"]));
    }
}
