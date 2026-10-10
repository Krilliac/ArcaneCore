using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The likely cause of a finding, guessed from the loaded content (scripted here with <see cref="FakeFacts"/>).</summary>
public sealed class PlayerbotQaClassifierTests
{
    private static QaSignal QuestStall(uint quest = 1656, uint target = 6747, uint map = 0)
        => QaData.Stall(QaData.Bot1, -5608.8f, -439.4f, quest, target, map);

    private static QaSignal DeathLoop(string attackers, uint quest = 0)
        => QaData.Parse(QaData.BotLine(QaData.Bot2, "Dawnrover", $"death loop: died 3 times in 99s within 60 yd of -5605.6,155.8,453.3 map 0 attackers={attackers}")) with { Quest = quest };

    private static QaTemplateFacts Template(uint rank = 0, byte min = 5, byte max = 6, string script = "") => new(rank, min, max, script);

    [Fact]
    public void QuestStall_ObjectiveCreatureWithNoSpawnsOnMap_IsMissingData()
    {
        var facts = new FakeFacts();
        facts.Objectives[1656] = [6747];
        facts.Templates[6747] = Template();
        facts.Spawns[(1, 6747)] = 9; // spawned, but on another map: the bot stalled on map 0

        QaClassification c = PlayerbotQaClassifier.Classify(QuestStall(map: 0), facts);

        Assert.Equal(QaCause.MissingData, c.Cause);
        Assert.Contains("creature entry 6747 has no spawn on map 0", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void QuestStall_ObjectiveCreatureSpawned_IsUnknownWithQuestTitle()
    {
        var facts = new FakeFacts();
        facts.Titles[1656] = "The Missing Diplomat";
        facts.Objectives[1656] = [6747];
        facts.Templates[6747] = Template();
        facts.Spawns[(0, 6747)] = 4;

        QaClassification c = PlayerbotQaClassifier.Classify(QuestStall(), facts);

        Assert.Equal(QaCause.Unknown, c.Cause);
        Assert.Equal("The Missing Diplomat", c.QuestTitle);
        Assert.Contains("quest 1656 (The Missing Diplomat)", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void QuestStall_NoTemplateAtAll_IsMissingData()
    {
        var facts = new FakeFacts();
        facts.Objectives[1656] = [6747];
        facts.Spawns[(0, 6747)] = 4; // spawn rows exist but the template row is gone

        QaClassification c = PlayerbotQaClassifier.Classify(QuestStall(), facts);

        Assert.Equal(QaCause.MissingData, c.Cause);
        Assert.Contains("no creature_template row", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void QuestStall_TargetIsAGameObject_IsNotBlamedOnMissingCreatureSpawns()
    {
        var facts = new FakeFacts(); // no template, not an objective creature: a quest goal target that is a game object
        facts.Titles[1656] = "Gather the Crates";

        QaClassification c = PlayerbotQaClassifier.Classify(QuestStall(), facts);

        Assert.Equal(QaCause.Unknown, c.Cause);
    }

    [Fact]
    public void ContentNotLoaded_MakesNoMissingDataClaim()
    {
        var facts = new FakeFacts { Available = false }; // an empty store would call every entry missing

        Assert.Equal(QaCause.Unknown, PlayerbotQaClassifier.Classify(QuestStall(), facts).Cause);
        Assert.Equal(QaCause.Unknown, PlayerbotQaClassifier.Classify(QuestStall(), NoQaContentFacts.Instance).Cause);
    }

    [Fact]
    public void RelayMovementPathMissing_IsMissingData_NoCreatureMovementPath()
    {
        QaSignal s = QaData.Parse("relay script 4021 uses unsupported MOVEMENT waypoint path 7 missing for entry 12345; that step is skipped");
        var facts = new FakeFacts();

        QaClassification c = PlayerbotQaClassifier.Classify(s, facts);

        Assert.Equal(QaCause.MissingData, c.Cause);
        Assert.Contains("no creature_movement / creature_movement_template path 7 for entry 12345", c.Detail, StringComparison.Ordinal);
        Assert.Contains("relay script 4021 MOVEMENT", c.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("loaded now", c.Detail, StringComparison.Ordinal);

        facts.Paths.Add((12345, 7));
        QaClassification later = PlayerbotQaClassifier.Classify(s, facts);
        Assert.Equal(QaCause.MissingData, later.Cause);
        Assert.Contains("a path is loaded now", later.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RelayScriptMissingAndUnimplementedStep_AreClassifiedApart()
    {
        QaClassification missing = PlayerbotQaClassifier.Classify(QaData.Relay(77), new FakeFacts());
        Assert.Equal(QaCause.MissingData, missing.Cause);
        Assert.Contains("dbscripts_on_relay", missing.Detail, StringComparison.Ordinal);

        QaSignal step = QaData.Parse("relay script 5 uses unsupported SET_HOVER fly animation flag; that step is skipped");
        QaClassification unimplemented = PlayerbotQaClassifier.Classify(step, new FakeFacts());
        Assert.Equal(QaCause.Unknown, unimplemented.Cause);
        Assert.Contains("SET_HOVER", unimplemented.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptNameWithoutRegisteredScript_IsMissingData_Sd2Script()
    {
        var facts = new FakeFacts();
        facts.Objectives[1656] = [6747];
        facts.Templates[6747] = Template(script: "boss_missing_script");
        facts.Spawns[(0, 6747)] = 2;

        QaClassification c = PlayerbotQaClassifier.Classify(QuestStall(), facts);
        Assert.Equal(QaCause.MissingData, c.Cause);
        Assert.Contains("boss_missing_script", c.Detail, StringComparison.Ordinal);
        Assert.Contains("SD2", c.Detail, StringComparison.Ordinal);

        facts.Scripts.Add("boss_missing_script");
        Assert.Equal(QaCause.Unknown, PlayerbotQaClassifier.Classify(QuestStall(), facts).Cause);
    }

    [Fact]
    public void RecoveryInvalidOperationException_IsBehaviourBug()
    {
        QaSignal s = QaData.Parse($"Playerbot {QaData.Bot1} action failed (InvalidOperationException: {PlayerbotRecovery.Stalled})");

        QaClassification c = PlayerbotQaClassifier.Classify(s, new FakeFacts());

        Assert.Equal(QaCause.BehaviourBug, c.Cause);
        Assert.Contains("InvalidOperationException", c.Detail, StringComparison.Ordinal);
        Assert.Contains("playerbot-recovery-stalled", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarantine_IsBehaviourBug()
    {
        QaSignal quarantine = QaData.Parse($"Playerbot {QaData.Bot1} quarantined for 30 s (fault 2 of 5): action: {PlayerbotRecovery.SpiritHealerFailed}");
        QaSignal disabled = QaData.Parse($"Playerbot {QaData.Bot1} disabled after 5 faults within 600 s: action: {PlayerbotRecovery.SpiritHealerFailed}");

        QaClassification q = PlayerbotQaClassifier.Classify(quarantine, new FakeFacts());
        QaClassification d = PlayerbotQaClassifier.Classify(disabled, new FakeFacts());

        Assert.Equal(QaCause.BehaviourBug, q.Cause);
        Assert.Contains("quarantined", q.Detail, StringComparison.Ordinal);
        Assert.Equal(QaCause.BehaviourBug, d.Cause);
        Assert.Contains("disabled", d.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u, QaCause.Unknown)]
    [InlineData(1u, QaCause.ContentDifficulty)]
    [InlineData(2u, QaCause.ContentDifficulty)]
    [InlineData(3u, QaCause.ContentDifficulty)]
    [InlineData(4u, QaCause.Unknown)]
    public void DeathLoop_EliteAttackers_IsContentDifficulty(uint rank, QaCause expected)
    {
        var facts = new FakeFacts();
        facts.Templates[16422] = Template(rank, 10, 11);

        QaClassification c = PlayerbotQaClassifier.Classify(DeathLoop("16422"), facts);

        Assert.Equal(expected, c.Cause);
        if (expected == QaCause.ContentDifficulty) Assert.Contains("16422", c.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((byte)8, QaCause.ContentDifficulty)] // quest level 5 + margin 3
    [InlineData((byte)9, QaCause.ContentDifficulty)]
    [InlineData((byte)7, QaCause.Unknown)]
    public void DeathLoop_AttackersAboveQuestLevel_IsContentDifficulty(byte attackerMaxLevel, QaCause expected)
    {
        var facts = new FakeFacts();
        facts.Levels[1656] = 5;
        facts.Templates[2000] = Template(0, 5, attackerMaxLevel);

        QaClassification c = PlayerbotQaClassifier.Classify(DeathLoop("2000", quest: 1656), facts);

        Assert.Equal(expected, c.Cause);
        Assert.Equal(3, PlayerbotQaClassifier.LevelMargin);
    }

    [Fact]
    public void DeathLoop_ManyDifferentAttackers_IsContentDifficulty()
    {
        QaClassification c = PlayerbotQaClassifier.Classify(DeathLoop("1/2/3"), new FakeFacts());
        Assert.Equal(QaCause.ContentDifficulty, c.Cause);
        Assert.Contains("3 different attackers", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DeathLoop_NoAttackers_IsBehaviourBug()
    {
        QaClassification c = PlayerbotQaClassifier.Classify(DeathLoop("none"), new FakeFacts());
        Assert.Equal(QaCause.BehaviourBug, c.Cause);
        Assert.Contains("no attacker", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedSetAside_ThirtyMinutes_RaisesSeverity()
    {
        // "weight per kind + 10*log2(1+count) + 5*min(bots,20) + 10 when seen in the last hour" (the class's documented formula).
        DateTime now = QaData.T0;
        Assert.Equal(25.0, PlayerbotQaClassifier.Severity(QaSignalKind.Stall, 0, 0, now, now));
        Assert.Equal(30.0, PlayerbotQaClassifier.Severity(QaSignalKind.Stall, 1, 1, now.AddHours(-2), now));
        Assert.Equal(40.0, PlayerbotQaClassifier.Severity(QaSignalKind.Stall, 1, 1, now, now));

        QaSignal setAside = QaData.Parse("Playerbots: 3 bots stalled on quest 1656 for 30 min; every bot sets it aside");
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(setAside, NoQaContentFacts.Instance, now);
        aggregator.Ingest(QaData.Stall(QaData.Bot1, 0, 0), NoQaContentFacts.Instance, now);
        QaFindingView[] byKind = [.. aggregator.Snapshot()];
        double shared = byKind.Single(f => f.Kind == QaSignalKind.SharedSetAside).Severity;
        double single = byKind.Single(f => f.Kind == QaSignalKind.Stall).Severity;
        Assert.True(shared > single, $"set-aside {shared} must outrank a single stall {single}");
    }

    [Fact]
    public void Severity_IsMonotone_CapsBots_AndHasAnExactRecencyBoundary()
    {
        DateTime now = QaData.T0;
        double Sev(long count, int bots, TimeSpan age, QaSignalKind kind = QaSignalKind.Stall) => PlayerbotQaClassifier.Severity(kind, count, bots, now - age, now);

        Assert.True(Sev(10, 1, TimeSpan.Zero) > Sev(2, 1, TimeSpan.Zero));
        Assert.True(Sev(2, 5, TimeSpan.Zero) > Sev(2, 1, TimeSpan.Zero));
        Assert.Equal(Sev(2, 20, TimeSpan.Zero), Sev(2, 500, TimeSpan.Zero)); // bots stop counting at 20
        Assert.Equal(10.0, Sev(2, 1, TimeSpan.Zero) - Sev(2, 1, TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)))); // seen exactly an hour ago still counts
        Assert.Equal(Sev(2, 1, TimeSpan.Zero), Sev(2, 1, TimeSpan.FromHours(1)));
        Assert.True(Sev(1, 1, TimeSpan.Zero, QaSignalKind.Quarantine) > Sev(1, 1, TimeSpan.Zero, QaSignalKind.ActionFault));
        Assert.True(Sev(1, 1, TimeSpan.Zero, QaSignalKind.ActionFault) > Sev(1, 1, TimeSpan.Zero, QaSignalKind.DeathLoop));
        Assert.True(Sev(-5, -3, TimeSpan.Zero) >= 0); // negative inputs are clamped, never NaN
    }
}
