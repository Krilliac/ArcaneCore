using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Strict loopback wire checks for the real combat and quest-credit contracts.</summary>
public sealed class StartingZoneCombatTests
{
    [Fact]
    public void AttackerStateUpdate_UsesTypedGuidsPositiveDamageAndFiniteSubDamage()
    {
        ObjectGuid attacker = ObjectGuid.Player(0x1001);
        ObjectGuid victim = ObjectGuid.WithEntry(HighGuid.Unit, 6, 0x2002);
        byte[] body = CombatPackets.AttackerStateUpdate(
            HitInfo.None, attacker, victim, 17,
            [new SubDamage(0, 17, 0, 0)], VictimState.Normal, 0);

        Assert.True(StartingZoneCombat.TryReadAttackerState(body, out StartingZoneCombat.AttackerState state));
        Assert.Equal(attacker.Value, state.Attacker);
        Assert.Equal(victim.Value, state.Target);
        Assert.Equal(17u, state.TotalDamage);
        Assert.Equal(0u, state.MeleeSpellId);
    }

    [Fact]
    public void AttackerStateUpdate_ExposesNextSwingSpellAndPhysicalSchool()
    {
        ObjectGuid attacker = ObjectGuid.Player(0x1001);
        ObjectGuid victim = ObjectGuid.WithEntry(HighGuid.Unit, 6, 0x2002);
        byte[] body = CombatPackets.AttackerStateUpdate(
            HitInfo.AffectsVictim, attacker, victim, 19,
            [new SubDamage(1, 19, 0, 0)], VictimState.Normal, 0, meleeSpellId: 78);

        Assert.True(StartingZoneCombat.TryReadAttackerState(body, out StartingZoneCombat.AttackerState state));
        Assert.Equal(78u, state.MeleeSpellId);
        Assert.Equal(1u, state.School);
        Assert.True(state.DisplayDamage > 0);
        Assert.True(StartingZoneCombat.IsHeroicExecution(state, castSuccess: true, healthBefore: 42, healthAfter: 23));
        Assert.False(StartingZoneCombat.IsHeroicExecution(state, castSuccess: true, healthBefore: 23, healthAfter: 23));
        Assert.False(StartingZoneCombat.IsHeroicExecution(state, castSuccess: false, healthBefore: 42, healthAfter: 23));
    }

    [Fact]
    public void SpellStart_RequiresThePlayerCasterAndSpellIdentity()
    {
        ObjectGuid caster = ObjectGuid.Player(0x1001);
        ObjectGuid other = ObjectGuid.Player(0x1002);
        byte[] body = SpellPackets.BuildSpellStart(caster, caster, 78, SpellCastFlags.Unknown2, 0,
            SpellCastTargets.ForUnit(ObjectGuid.WithEntry(HighGuid.Unit, 6, 0x2002)));

        Assert.True(StartingZoneCombat.IsSpellStart(body, 78, caster.Value));
        Assert.False(StartingZoneCombat.IsSpellStart(body, 78, other.Value));
        Assert.False(StartingZoneCombat.IsSpellStart(body, 79, caster.Value));
    }

    [Fact]
    public void QuestCampaign_RequiresQuestSevenCompletionIdentity()
    {
        Assert.True(StartingZoneCampaign.IsRewardIdentity(new MockQuestComplete(7, 3, 170, 0, []), 7));
        Assert.False(StartingZoneCampaign.IsRewardIdentity(new MockQuestComplete(8, 3, 170, 0, []), 7));
        Assert.False(StartingZoneCampaign.IsRewardIdentity(new MockQuestComplete(7, 2, 170, 0, []), 7));
    }

    [Fact]
    public void AttackerStateUpdate_RejectsTruncationTrailingBytesAndInvalidComponentCount()
    {
        byte[] body = CombatPackets.AttackerStateUpdate(
            HitInfo.None, ObjectGuid.Player(1), ObjectGuid.WithEntry(HighGuid.Unit, 6, 2), 3,
            [new SubDamage(0, 3, 0, 0)], VictimState.Normal, 0);

        Assert.False(StartingZoneCombat.TryReadAttackerState(body[..^1], out _));
        Assert.False(StartingZoneCombat.TryReadAttackerState([.. body, 0], out _));

        (int countOffset, _) = PacketOffsets(body);
        byte[] countTooLarge = [.. body];
        countTooLarge[countOffset] = 17;
        Assert.False(StartingZoneCombat.TryReadAttackerState(countTooLarge, out _));

        byte[] nonFinite = [.. body];
        (_, int displayOffset) = PacketOffsets(nonFinite);
        BinaryPrimitives.WriteInt32LittleEndian(nonFinite.AsSpan(displayOffset), BitConverter.SingleToInt32Bits(float.NaN));
        Assert.False(StartingZoneCombat.TryReadAttackerState(nonFinite, out _));

        byte[] missingGuid = CombatPackets.AttackerStateUpdate(
            HitInfo.None, default, ObjectGuid.WithEntry(HighGuid.Unit, 6, 2), 0, [], VictimState.Normal, 0);
        Assert.False(StartingZoneCombat.TryReadAttackerState(missingGuid, out _));
    }

    [Theory]
    [InlineData(7u, 6u, 1u, 10u)]
    [InlineData(7u, 6u, 10u, 10u)]
    public void QuestKill_RequiresQuest7KoboldObjectiveAndObservedGuid(uint quest, uint creature, uint count, uint required)
    {
        byte[] body = QuestKillBody(quest, creature, count, required, 0xF130000600001234);
        MockQuestKill parsed = ScenarioWire.QuestKill(body);
        Assert.True(StartingZoneCombat.IsExpectedKillCredit(parsed, 0xF130000600001234ul, 7, count, required));
    }

    [Fact]
    public void QuestKill_RejectsWrongCreatureForeignGuidGoBitAndCounts()
    {
        ulong observed = 0xF130000600001234;
        foreach (byte[] body in new[]
        {
            QuestKillBody(8, 6, 1, 10, observed),
            QuestKillBody(7, 5, 1, 10, observed),
            QuestKillBody(7, 6 | 0x80000000u, 1, 10, observed),
            QuestKillBody(7, 6, 0, 10, observed),
            QuestKillBody(7, 6, 11, 10, observed),
            QuestKillBody(7, 6, 1, 9, observed),
            QuestKillBody(7, 6, 1, 10, 0xF130000600005678),
        })
        {
            MockQuestKill parsed = ScenarioWire.QuestKill(body);
            Assert.False(StartingZoneCombat.IsExpectedKillCredit(parsed, observed, 7, 1, 10));
        }
    }

    [Fact]
    public void QuestJournal_UsesSixBitCounterAndSeparateCompleteOrFailedState()
    {
        int slot = 4;
        int questField = UpdateFields.PlayerQuestLog11 + (slot * QuestConstants.FieldsPerSlot);
        int countField = questField + 1;
        uint counterTen = 10u;
        var incomplete = new Dictionary<int, uint> { [questField] = 7, [countField] = counterTen };
        var complete = new Dictionary<int, uint> { [questField] = 7, [countField] = counterTen | (QuestConstants.SlotStateComplete << 24) };
        var failed = new Dictionary<int, uint> { [questField] = 7, [countField] = counterTen | (QuestConstants.SlotStateFail << 24) };
        var foreign = new Dictionary<int, uint> { [questField] = 8, [countField] = counterTen | (QuestConstants.SlotStateComplete << 24) };

        Assert.Equal(10u, StartingZoneCombat.QuestProgress(incomplete, 7));
        Assert.True(StartingZoneProbe.IsQuestIncomplete(incomplete, 7));
        Assert.True(StartingZoneProbe.IsQuestComplete(complete, 7));
        Assert.False(StartingZoneProbe.IsQuestComplete(incomplete, 7));
        Assert.False(StartingZoneProbe.IsQuestComplete(failed, 7));
        Assert.False(StartingZoneProbe.IsQuestComplete(foreign, 7));
        Assert.Equal(10u, incomplete[countField] & 0x3Fu);
        Assert.Equal(0u, incomplete[countField] & (0x3Fu << 6));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(10u)]
    public void CombatLoop_CompletesAtTheRequestedQuestCounter(uint expectedCount)
    {
        Assert.True(StartingZoneCombat.IsCombatComplete(
            sawTargetDeath: true, sawQuestCredit: true, questProgress: expectedCount,
            expectedQuestCount: expectedCount, sawMatchingSwingDamage: true,
            sawHealthDecrease: true, heroic: false, heroicExecution: false));

        Assert.False(StartingZoneCombat.IsCombatComplete(
            sawTargetDeath: true, sawQuestCredit: true, questProgress: expectedCount == 10 ? 9u : expectedCount + 1,
            expectedQuestCount: expectedCount, sawMatchingSwingDamage: true,
            sawHealthDecrease: true, heroic: false, heroicExecution: false));
    }

    [Fact]
    public void CombatLoop_RecognizesObservedPlayerDeathOnlyWhenHealthFieldIsPresent()
    {
        Assert.True(StartingZoneCombat.TryObservedPlayerDeath(
            new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 0 }, out uint deadHealth));
        Assert.Equal(0u, deadHealth);
        Assert.False(StartingZoneCombat.TryObservedPlayerDeath(
            new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 37 }, out uint liveHealth));
        Assert.Equal(37u, liveHealth);
        Assert.False(StartingZoneCombat.TryObservedPlayerDeath(
            new Dictionary<int, uint>(), out _));
    }

    [Fact]
    public void CombatLoop_DeathGuardThrowsWithBoundedWireEvidence()
    {
        var error = Assert.Throws<MockProtocolException>(() => StartingZoneCombat.ThrowIfObservedPlayerDeath(
            new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 0 },
            target: 0xF130000600001234ul, targetHealth: 19, quest: 7, expectedQuestCount: 2,
            sawMatchingSwingDamage: false, sawHealthDecrease: true, sawMatchingSwing: true));

        Assert.Contains("playerHealth=0", error.Message);
        Assert.Contains("targetHealth=19", error.Message);
        Assert.Contains("questCount=0/2", error.Message);
        Assert.Contains("matchingSwingDamage=False", error.Message);
    }

    [Fact]
    public void CombatLoop_DeathGuardDoesNotTreatMissingHealthAsDeath()
    {
        StartingZoneCombat.ThrowIfObservedPlayerDeath(
            new Dictionary<int, uint>(), target: 0xF130000600001234ul, targetHealth: 19,
            quest: 7, expectedQuestCount: 2, sawMatchingSwingDamage: false,
            sawHealthDecrease: false, sawMatchingSwing: false);
    }

    [Fact]
    public async Task CombatBudget_BeforeReadDeathGuardFailsBeforeWaitingForTraffic()
    {
        var budget = new StartingZoneCombat.CombatBudget();
        budget.BeforeRead = () => StartingZoneCombat.ThrowIfObservedPlayerDeath(
            new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 0 },
            target: 0xF130000600001234ul, targetHealth: 19, quest: 7, expectedQuestCount: 2,
            sawMatchingSwingDamage: false, sawHealthDecrease: true, sawMatchingSwing: true);

        MockProtocolException error = await Assert.ThrowsAsync<MockProtocolException>(
            () => budget.ReadAsync(null!, CancellationToken.None));

        Assert.Contains("player died during Kobold combat", error.Message);
        Assert.Contains("targetHealth=19", error.Message);
        Assert.Equal(0, budget.Frames);
        Assert.Equal(0, budget.Bytes);
    }

    [Fact]
    public async Task CombatBudget_DrainDeathGuardFailsBeforeWaitingForTraffic()
    {
        var budget = new StartingZoneCombat.CombatBudget();
        budget.BeforeRead = () => StartingZoneCombat.ThrowIfObservedPlayerDeath(
            new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 0 },
            target: 0xF130000600001234ul, targetHealth: 19, quest: 7, expectedQuestCount: 2,
            sawMatchingSwingDamage: false, sawHealthDecrease: true, sawMatchingSwing: true);

        MockProtocolException error = await Assert.ThrowsAsync<MockProtocolException>(
            () => budget.DrainQuietAsync(null!, CancellationToken.None));

        Assert.Contains("player died during Kobold combat", error.Message);
        Assert.Contains("questCount=0/2", error.Message);
        Assert.Equal(0, budget.Frames);
        Assert.Equal(0, budget.Bytes);
    }

    [Fact]
    public void CombatLoop_SelectsFirstLiveAllowedEntrySixAttacker()
    {
        ulong character = ObjectGuid.Player(18).Value;
        ulong dead = ObjectGuid.WithEntry(HighGuid.Unit, 6, 80002).Value;
        ulong foreign = ObjectGuid.WithEntry(HighGuid.Unit, 7, 80003).Value;
        ulong eligible = ObjectGuid.WithEntry(HighGuid.Unit, 6, 79992).Value;
        var live = new Dictionary<ulong, (uint Entry, uint Health)>
        {
            [dead] = (6, 0), [foreign] = (7, 100), [eligible] = (6, 100),
        };
        var allowed = new HashSet<ulong> { dead, eligible };

        ulong? selected = StartingZoneCombat.SelectActiveEligibleAttacker(
            [character, dead, foreign, eligible], character, allowed,
            guid => live.TryGetValue(guid, out (uint Entry, uint Health) state)
                && state.Entry == 6 && state.Health > 0);

        Assert.Equal(eligible, selected);
    }

    [Fact]
    public void CombatLoop_RequiresCurrentTargetAndCombatFlagsWhenCanonicalFieldsExist()
    {
        ulong character = ObjectGuid.Player(18).Value;
        var fields = new Dictionary<int, uint>
        {
            [UpdateFields.ObjectFieldEntry] = 6,
            [UpdateFields.UnitFieldHealth] = 100,
            [UpdateFields.UnitFieldTarget] = (uint)character,
            [UpdateFields.UnitFieldTarget + 1] = 0,
            [UpdateFields.UnitFieldFlags] = (uint)UnitFlags.InCombat,
        };

        Assert.True(StartingZoneCombat.IsActiveEntrySixTarget(fields, character));
        fields[UpdateFields.UnitFieldTarget + 1] = 0xF130;
        Assert.False(StartingZoneCombat.IsActiveEntrySixTarget(fields, character));
        fields[UpdateFields.UnitFieldTarget + 1] = 0;
        fields[UpdateFields.UnitFieldTarget] = (uint)ObjectGuid.Player(19).Value;
        Assert.False(StartingZoneCombat.IsActiveEntrySixTarget(fields, character));
        fields[UpdateFields.UnitFieldTarget] = (uint)character;
        fields[UpdateFields.UnitFieldFlags] = 0;
        Assert.False(StartingZoneCombat.IsActiveEntrySixTarget(fields, character));
        Assert.False(StartingZoneCombat.IsActiveEntrySixTarget(
            new Dictionary<int, uint>
            {
                [UpdateFields.ObjectFieldEntry] = 6,
                [UpdateFields.UnitFieldHealth] = 100,
            }, character));
    }

    [Fact]
    public void CombatEvidence_DefaultShapeDoesNotExposeContinuationAttackers()
    {
        var evidence = new StartingZoneCombat.CombatEvidence(
            Target: 1, StartingHealth: 10, FinalHealth: 0, QuestCount: 1, RequiredCount: 10,
            Frames: 1, Bytes: 1, PlayerX: 0, PlayerY: 0, PlayerZ: 0, ClientTime: 1);

        Assert.Null(evidence.ActiveAttackers);
    }

    [Fact]
    public void Campaign_RejectsUnvettedLiveAssistanceBeforeCombatExitWait()
    {
        ulong player = ObjectGuid.Player(18).Value;
        ulong known = ObjectGuid.WithEntry(HighGuid.Unit, 6, 79992).Value;
        ulong unknown = ObjectGuid.WithEntry(HighGuid.Unit, 6, 79995).Value;
        var allowed = new HashSet<ulong> { known };
        var live = new Dictionary<int, uint>
        {
            [UpdateFields.UnitFieldHealth] = 10,
            [UpdateFields.UnitFieldTarget] = (uint)player,
            [UpdateFields.UnitFieldTarget + 1] = 0,
            [UpdateFields.UnitFieldFlags] = (uint)UnitFlags.InCombat,
        };
        Assert.Equal(unknown, StartingZoneCampaign.UnknownAssistingTarget([known, unknown], player, allowed, _ => live));
        live[UpdateFields.UnitFieldHealth] = 0;
        Assert.Null(StartingZoneCampaign.UnknownAssistingTarget([unknown], player, allowed, _ => live));
        live[UpdateFields.UnitFieldHealth] = 10;
        live[UpdateFields.UnitFieldTarget + 1] = 0xF130;
        Assert.Null(StartingZoneCampaign.UnknownAssistingTarget([unknown], player, allowed, _ => live));
    }

    [Fact]
    public void Campaign_DoesNotTreatMissingCombatFlagsAsFoodEligibility()
    {
        var fields = new Dictionary<int, uint>
        {
            [UpdateFields.UnitFieldHealth] = 20,
            [UpdateFields.UnitFieldMaxhealth] = 60,
        };
        Assert.False(StartingZoneCampaign.CanConsumeFood(fields));
        fields[UpdateFields.UnitFieldFlags] = 0;
        Assert.True(StartingZoneCampaign.CanConsumeFood(fields));
    }

    [Fact]
    public void CampaignFoodGuardRequiresAliveOutOfCombatThresholdState()
    {
        var fields = new Dictionary<int, uint>
        {
            [UpdateFields.UnitFieldHealth] = 50,
            [UpdateFields.UnitFieldMaxhealth] = 100,
            [UpdateFields.UnitFieldFlags] = 0,
        };
        Assert.True(StartingZoneCampaign.CanConsumeFood(fields));
        fields[UpdateFields.UnitFieldFlags] = (uint)UnitFlags.InCombat;
        Assert.False(StartingZoneCampaign.CanConsumeFood(fields));
        fields[UpdateFields.UnitFieldFlags] = 0;
        fields[UpdateFields.UnitFieldHealth] = 0;
        Assert.False(StartingZoneCampaign.CanConsumeFood(fields));
        Assert.False(StartingZoneCampaign.CanConsumeFood(new Dictionary<int, uint>()));
    }

    [Fact]
    public void StartingZoneOptions_DefaultToLoopbackAndKeepFirstCharacterSelection()
    {
        string variable = "ARCANE_STARTING_ZONE_TEST_" + Guid.NewGuid().ToString("N");
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "set-for-local-parser-test");
            StartingZoneProbe.Options options = StartingZoneProbe.Parse(["--password-env", variable]);
            Assert.Equal("127.0.0.1", options.Realm.Address.ToString());
            Assert.Equal(3724, options.Realm.Port);
            Assert.Equal("ARCPLAY", options.Account);
            Assert.Equal(StartingZoneProbe.DefaultCharacter, options.Character);
            Assert.False(options.Heroic);

            StartingZoneProbe.Options heroic = StartingZoneProbe.Parse([
                "--password-env", variable, "--combat-spawn", "79994", "--combat-position", "-8797.1,-174.6,81.6",
                "--heroic", "true"]);
            Assert.True(heroic.Heroic);

            StartingZoneProbe.Options campaign = StartingZoneProbe.Parse([
                "--password-env", variable, "--combat-spawn", "79994", "--combat-position", "-8797.1,-174.6,81.6",
                "--complete-quest", "true"]);
            Assert.True(campaign.CompleteQuest);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static byte[] QuestKillBody(uint quest, uint creature, uint count, uint required, ulong guid)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body, quest);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), creature);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), count);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), required);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), guid);
        return body;
    }

    private static (int CountOffset, int DisplayOffset) PacketOffsets(byte[] body)
    {
        var reader = new PacketReader(body);
        _ = reader.ReadUInt32();
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        _ = reader.ReadUInt32();
        int countOffset = reader.Position;
        byte componentCount = reader.ReadByte();
        if (componentCount == 0)
            throw new Xunit.Sdk.XunitException("test packet needs one component");
        _ = reader.ReadUInt32();
        int displayOffset = reader.Position;
        return (countOffset, displayOffset);
    }
}
