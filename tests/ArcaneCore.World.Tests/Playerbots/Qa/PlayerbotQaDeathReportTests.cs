using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

public sealed class PlayerbotQaDeathReportTests
{
    [Fact]
    public void StallWarning_CarriesBotContextIntoTheIssueBody()
    {
        var logger = new ListLogger();
        string report = "stalled 120s: goal=Quest target=7865 quest=681 at 1.0,2.0,3.0 map 0";
        string context = new QaBotObservation(30, "Human", "Alliance", "Warrior", "Quest", 681,
            0, null, "None", []).ToLogSuffix();
        ManagedPlayerbotFeature.LogStall(logger, QaData.Bot1, "Testbot", 1, 0, report, false, null, context);

        QaSignal signal = QaData.Parse(Assert.Single(logger.Entries).Message);
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(signal, new FakeFacts(), QaData.T0);
        string body = PlayerbotQaIssueComposer.Compose(Assert.Single(aggregator.Snapshot()), ["bot-qa"], "test",
            new PlayerbotQaRedactor()).Body;
        Assert.Contains("level 30 Human Alliance Warrior", body, StringComparison.Ordinal);
        Assert.Contains("goal Quest, quest 681", body, StringComparison.Ordinal);
        Assert.Contains("graveyard unknown", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DeathWarning_WaitsForReleaseBeforeLoggingTheGraveyard()
    {
        var logger = new ListLogger();
        string report = "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=none";
        (int logged, _) = ManagedPlayerbotFeature.LogStall(logger, QaData.Bot1, "Testbot", 1, 0, report,
            false, null, awaitRelease: true);
        Assert.Equal(0, logged);
        Assert.Empty(logger.Entries);
        ManagedPlayerbotFeature.LogStall(logger, QaData.Bot1, "Testbot", 1, logged, report, false, null,
            new QaBotObservation(30, "Human", "Alliance", "Warrior", "Quest", 681, 4, 172.5f, "Fall", []).ToLogSuffix());
        Assert.Equal(4u, QaData.Parse(Assert.Single(logger.Entries).Message).Bot?.Graveyard);
    }

    [Fact]
    public void HostileGuardFinding_RendersBotGraveyardAndAttackerFacts()
    {
        string line = QaData.BotLine(QaData.Bot1, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=7865"
            + " qa[level=30,race=Human,team=Alliance,class=Warrior,goal=Quest,quest=681,graveyard=4,distance=172.5,source=Creature,attackerFacts=7865:55:55:55:0:1:1]");
        var facts = new FakeFacts();
        QaSignal signal = QaData.Parse(line);
        QaSignal oldSignal = QaData.Parse(QaData.BotLine(QaData.Bot1, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=7865"));
        Assert.Equal(PlayerbotQaFingerprint.Key(oldSignal), PlayerbotQaFingerprint.Key(signal));
        Assert.Equal("Quest", signal.Goal);
        Assert.Equal(681u, signal.Quest);
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(signal, facts, QaData.T0);

        QaFindingView finding = Assert.Single(aggregator.Snapshot());
        string body = PlayerbotQaIssueComposer.Compose(finding, ["bot-qa"], "test", new PlayerbotQaRedactor()).Body;
        Assert.Equal(QaCause.ContentDifficulty, finding.Cause);
        Assert.Contains("level 30 Human Alliance Warrior", body, StringComparison.Ordinal);
        Assert.Contains("goal Quest, quest 681", body, StringComparison.Ordinal);
        Assert.Contains("graveyard 4, 172.5 yd", body, StringComparison.Ordinal);
        Assert.Contains("7865: level 55 (template 55-55), rank normal, guard yes, hostile yes", body, StringComparison.Ordinal);
        Assert.Contains("hostile guard", body, StringComparison.Ordinal);
    }

    private static QaClassification ClassifyGuard(string facts) => PlayerbotQaClassifier.Classify(QaData.Parse(QaData.BotLine(QaData.Bot1, "Testbot",
        "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=7865"
        + " qa[level=30,race=Human,team=Alliance,class=Warrior,goal=Explore,quest=0,graveyard=0,distance=unknown,source=Creature,attackerFacts=" + facts + "]")),
        new FakeFacts());

    [Theory]
    [InlineData("7865:30:30:30:0:1:1")] // at the bot's level
    [InlineData("7865:20:20:20:0:1:1")] // below it
    public void HostileGuardAtOrBelowTheBotsLevel_IsContentDifficulty_OnTheGuardAlone(string facts)
    {
        QaClassification classification = ClassifyGuard(facts);
        Assert.Equal(QaCause.ContentDifficulty, classification.Cause);
        Assert.Contains("7865 is a hostile guard", classification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NonHostileGuard_IsNotFlaggedAsAGuardDeath()
    {
        QaClassification classification = ClassifyGuard("7865:30:30:30:0:1:0");
        Assert.Equal(QaCause.Unknown, classification.Cause);
        Assert.DoesNotContain("is a hostile guard", classification.Detail, StringComparison.Ordinal);
        Assert.Contains("guard yes, hostile no", classification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StallBody_ShowsTheQuestIdRow()
    {
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(QaData.Stall(QaData.Bot1, 1f, 2f, quest: 1656), new FakeFacts(), QaData.T0);
        QaFindingView finding = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaSignalKind.Stall, finding.Kind);
        string body = PlayerbotQaIssueComposer.Compose(finding, ["bot-qa"], "test", new PlayerbotQaRedactor()).Body;
        Assert.Contains("| Quest | 1656", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FallDeathFinding_IdentifiesFallRatherThanInventingAnAttacker()
    {
        string line = QaData.BotLine(QaData.Bot2, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=none"
            + " qa[level=32,race=Dwarf,team=Alliance,class=Hunter,goal=Explore,quest=0,graveyard=4,distance=25.0,source=Fall,attackerFacts=none]");
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(QaData.Parse(line), new FakeFacts(), QaData.T0);

        QaFindingView finding = Assert.Single(aggregator.Snapshot());
        string body = PlayerbotQaIssueComposer.Compose(finding, ["bot-qa"], "test", new PlayerbotQaRedactor()).Body;
        Assert.Contains("fall damage", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attackers none", body, StringComparison.Ordinal);
        Assert.Contains("level 32 Dwarf Alliance Hunter", body, StringComparison.Ordinal);
        Assert.Contains("graveyard 4, 25.0 yd", body, StringComparison.Ordinal);
    }

    [Fact]
    public void LevelGap_UsesObservedAttackerLevel_NotItsTemplateMaximum()
    {
        QaSignal signal = QaData.Parse(QaData.BotLine(QaData.Bot1, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=9000"
            + " qa[level=30,race=Human,team=Alliance,class=Warrior,goal=Explore,quest=0,graveyard=0,distance=unknown,source=Creature,attackerFacts=9000:31:31:55:0:0:1]"));
        QaClassification classification = PlayerbotQaClassifier.Classify(signal, new FakeFacts());
        Assert.Equal(QaCause.Unknown, classification.Cause);
        Assert.Contains("level 31 (template 31-55)", classification.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCasterPeriodicDeath_IsNamedWithoutClaimingAProvenBotBug()
    {
        QaSignal signal = QaData.Parse(QaData.BotLine(QaData.Bot1, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=none"
            + " qa[level=30,race=Human,team=Alliance,class=Warrior,goal=Explore,quest=0,graveyard=0,distance=unknown,source=PeriodicCasterMissing,attackerFacts=none]"));
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(signal, new FakeFacts(), QaData.T0);
        QaFindingView finding = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaCause.Unknown, finding.Cause);
        string body = PlayerbotQaIssueComposer.Compose(finding, ["bot-qa"], "test", new PlayerbotQaRedactor()).Body;
        Assert.Contains("damage-over-time killing tick whose original caster despawned or became unavailable", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NewDeathAtSameCell_ClearsOldQuestButKeepsEachSampleContext()
    {
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        var facts = new FakeFacts();
        facts.Titles[681] = "Old quest";
        QaSignal first = QaData.Parse(QaData.BotLine(QaData.Bot1, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=none"
            + " qa[level=30,race=Human,team=Alliance,class=Warrior,goal=Quest,quest=681,graveyard=0,distance=unknown,source=Fall,attackerFacts=none]"));
        QaSignal second = QaData.Parse(QaData.BotLine(QaData.Bot2, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 2.0,2.0,3.0 map 0 attackers=none"
            + " qa[level=31,race=Human,team=Alliance,class=Warrior,goal=Explore,quest=0,graveyard=0,distance=unknown,source=Fall,attackerFacts=none]"));
        Assert.Equal(PlayerbotQaFingerprint.Key(first), PlayerbotQaFingerprint.Key(second));
        aggregator.Ingest(first, facts, QaData.T0);
        aggregator.Ingest(second, facts, QaData.T0.AddMinutes(1));
        QaFindingView finding = Assert.Single(aggregator.Snapshot());
        Assert.Equal(0u, finding.Quest);
        Assert.Null(finding.QuestTitle);
        Assert.Equal(new uint[] { 681, 0 }, finding.Samples.Select(sample => sample.Bot!.Quest));
    }

    [Fact]
    public void NoAttackerLoop_RendersEachDeathSourceAndKeepsUnresolvedCauseUnknown()
    {
        string context = new QaBotObservation(30, "Human", "Alliance", "Warrior", "Explore", 0,
            4, 25f, "Unknown", [], ["Fall", "PeriodicCasterMissing", "Unknown"]).ToLogSuffix();
        QaSignal signal = QaData.Parse(QaData.BotLine(QaData.Bot1, "Testbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=none" + context));
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(signal, new FakeFacts(), QaData.T0);
        QaFindingView finding = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaCause.Unknown, finding.Cause);
        string body = PlayerbotQaIssueComposer.Compose(finding, ["bot-qa"], "test", new PlayerbotQaRedactor()).Body;
        Assert.Contains("death sources Fall/PeriodicCasterMissing/Unknown", body, StringComparison.Ordinal);
        Assert.Contains("fall damage", body, StringComparison.Ordinal);
        Assert.Contains("damage-over-time killing tick", body, StringComparison.Ordinal);
        Assert.Contains("null killing-blow source", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DeathLoopFinding_UpgradesCauseFromUnknownToContentDifficulty_WhenLaterFoldedSampleRevealsLevelGapOrElite()
    {
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        var facts = new FakeFacts();

        // First sample: level 30 bot dies to level 31 normal creature 7865 -> Unknown.
        string first = QaData.BotLine(QaData.Bot1, "Firstbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=7865"
            + " qa[level=30,race=Human,team=Alliance,class=Warrior,goal=Quest,quest=681,graveyard=4,distance=172.5,source=Creature,attackerFacts=7865:31:30:31:0:0:1]");
        aggregator.Ingest(QaData.Parse(first), facts, QaData.T0);
        QaFindingView initial = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaCause.Unknown, initial.Cause);

        // Second sample folds in (shares attacker 7865, adds elite/higher-level 7866 from a different cell key):
        string second = QaData.BotLine(QaData.Bot2, "Secondbot",
            "death loop: died 3 times in 90s within 60 yd of 60.0,2.0,3.0 map 0 attackers=7865/7866"
            + " qa[level=30,race=Human,team=Alliance,class=Mage,goal=Quest,quest=681,graveyard=4,distance=180.0,source=Creature,attackerFacts=7865:31:30:31:0:0:1/7866:35:34:35:1:0:1]");
        aggregator.Ingest(QaData.Parse(second), facts, QaData.T0.AddMinutes(1));

        QaFindingView updated = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaCause.ContentDifficulty, updated.Cause);
        Assert.Contains("7866 is an elite (level 34-35)", updated.CauseDetail, StringComparison.Ordinal);
        Assert.Contains("7866 is level 35 against bot level 30", updated.CauseDetail, StringComparison.Ordinal);
        Assert.Contains("7865: level 31 (template 30-31)", updated.CauseDetail, StringComparison.Ordinal);

        // Third sample on the original key (own == true) with only normal 7865 must not overwrite ContentDifficulty back to Unknown.
        string third = QaData.BotLine(QaData.Bot3, "Thirdbot",
            "death loop: died 3 times in 90s within 60 yd of 1.0,2.0,3.0 map 0 attackers=7865"
            + " qa[level=30,race=Human,team=Alliance,class=Rogue,goal=Quest,quest=681,graveyard=4,distance=172.5,source=Creature,attackerFacts=7865:31:30:31:0:0:1]");
        aggregator.Ingest(QaData.Parse(third), facts, QaData.T0.AddMinutes(2));

        QaFindingView afterThird = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaCause.ContentDifficulty, afterThird.Cause);
        Assert.Contains("7866 is an elite (level 34-35)", afterThird.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void DeathLoopFinding_UpgradesToContentDifficulty_WhenAccumulatedAttackersAcrossBridgedSignalsReachThree()
    {
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        var facts = new FakeFacts();

        // Loop A: normal attacker 100 (Unknown).
        string a = QaData.BotLine(QaData.Bot1, "BotA",
            "death loop: died 3 times in 90s within 60 yd of 10.0,10.0,0.0 map 0 attackers=100"
            + " qa[level=20,race=Human,team=Alliance,class=Warrior,goal=Explore,quest=0,graveyard=1,distance=50.0,source=Creature,attackerFacts=100:20:20:20:0:0:1]");
        // Loop B in the same zone cell: normal attacker 200 (Unknown).
        string b = QaData.BotLine(QaData.Bot2, "BotB",
            "death loop: died 3 times in 90s within 60 yd of 20.0,10.0,0.0 map 0 attackers=200"
            + " qa[level=20,race=Human,team=Alliance,class=Mage,goal=Explore,quest=0,graveyard=1,distance=50.0,source=Creature,attackerFacts=200:20:20:20:0:0:1]");
        aggregator.Ingest(QaData.Parse(a), facts, QaData.T0);
        aggregator.Ingest(QaData.Parse(b), facts, QaData.T0.AddMinutes(1));
        Assert.Equal(2, aggregator.Snapshot().Count);
        Assert.All(aggregator.Snapshot(), f => Assert.Equal(QaCause.Unknown, f.Cause));

        // Bridging signal shares 100 and 200 and adds normal attacker 300 -> merges A and B, union has 3 attackers -> ContentDifficulty.
        string bridge = QaData.BotLine(QaData.Bot3, "BotC",
            "death loop: died 3 times in 90s within 60 yd of 15.0,10.0,0.0 map 0 attackers=100/200/300"
            + " qa[level=20,race=Human,team=Alliance,class=Priest,goal=Explore,quest=0,graveyard=1,distance=50.0,source=Creature,attackerFacts=300:20:20:20:0:0:1]");
        aggregator.Ingest(QaData.Parse(bridge), facts, QaData.T0.AddMinutes(2));

        QaFindingView merged = Assert.Single(aggregator.Snapshot());
        Assert.Equal(QaCause.ContentDifficulty, merged.Cause);
        Assert.Contains("3 different attackers (100/200/300)", merged.CauseDetail, StringComparison.Ordinal);
    }
}
