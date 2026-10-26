using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// A stall names the goal the bot held. Its <c>quest=</c> is only the last quest the brain remembers: for any goal but Quest it is not what the
/// bot was doing (GitHub #154: every evidence line read <c>goal=Explore target=0 quest=109</c>, yet the title, the summary, the Quest row and the
/// likely cause all spoke of quest 109, and the investigation went to the wrong quest). The fingerprint, and so the issue marker, is unchanged.
/// </summary>
public sealed class PlayerbotQaStallGoalTests : IDisposable
{
    /// <summary>The first evidence line of GitHub #154, as the server logged it (bot id and name are placeholders).</summary>
    private const string Issue154Line = "stalled 120s: goal=Explore target=0 quest=109 at -10301.6,1431.5,39.4 map 0";

    /// <summary>The marker of #154, <c>&lt;!-- arcanecore-bot-qa:0679c57a28dbea99 --&gt;</c> (read from the issue body on GitHub, and equal to sha256sum of the key).</summary>
    private const string Issue154Id = "0679c57a28dbea99";

    private const string Issue154Key = "v1|stall|goal=Explore|quest=109|target=0|map=0|cell=-207,28";

    /// <summary>A real goal=Quest stall from a live log (bot-groups run).</summary>
    private const string QuestLine = "stalled 123s: goal=Quest target=963 quest=11 at -9928.2,47.6,32.6 map 0";

    /// <summary>The same stall as a bot walking home to turn quest 11 in logs it: the brain marks it before the qa context.</summary>
    private const string ReturnTripLine = QuestLine + " return-trip qa[level=10,race=Human,team=Alliance,class=Warrior,goal=Quest,quest=11,graveyard=0,distance=unknown,source=None,attackerFacts=none]";

    private static readonly byte[] Salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static FakeFacts Gryan()
    {
        var facts = new FakeFacts();
        facts.Titles[109] = "Report to Gryan Stoutmantle";
        return facts;
    }

    private static QaFindingView IngestOne(string report, IQaContentFacts facts)
    {
        var aggregator = new PlayerbotQaAggregator(100, 5, Salt);
        Assert.NotNull(aggregator.Ingest(QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", report)), facts, QaData.T0));
        return Assert.Single(aggregator.Snapshot());
    }

    /// <summary>
    /// An Explore stall carries the entry the brain still remembered from an earlier goal; it is not a creature the bot was walking to, so a
    /// missing spawn for it is no cause (GitHub #1481: "3502 has no spawn" on 2,077 Explore stalls).
    /// </summary>
    [Fact]
    public void ExploreStall_WithARememberedEntryThatHasNoSpawn_IsNotBlamedOnMissingData()
    {
        QaFindingView f = IngestOne("stalled 120s: goal=Explore target=3502 quest=0 at -10301.6,1431.5,39.4 map 0", new FakeFacts());

        Assert.NotEqual(QaCause.MissingData, f.Cause);
        Assert.DoesNotContain("has no spawn", f.CauseDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("no spawn", PlayerbotQaClassifier.Classify(QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", "stalled 120s: goal=Explore target=3502 quest=0 at -10301.6,1431.5,39.4 map 0")), new FakeFacts()).Detail, StringComparison.Ordinal);
    }

    /// <summary>A later Explore stall of a finding re-classifies it: a cause the first signal set does not outlive the classifier that set it.</summary>
    [Fact]
    public void ALaterSignalOfTheSameFinding_RefreshesItsCause()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5, Salt);
        string line = QaData.BotLine(QaData.Bot1, "Ironwander", "stalled 120s: goal=Vendor target=3502 quest=0 at -10301.6,1431.5,39.4 map 0");
        Assert.NotNull(aggregator.Ingest(QaData.Parse(line), new FakeFacts(), QaData.T0));
        Assert.Equal(QaCause.MissingData, Assert.Single(aggregator.Snapshot()).Cause);

        var facts = new FakeFacts();
        facts.Spawns[(0, 3502)] = 4;
        facts.Templates[3502] = new QaTemplateFacts(0, 1, 60, string.Empty);
        Assert.NotNull(aggregator.Ingest(QaData.Parse(line), facts, QaData.T0.AddMinutes(1)));

        Assert.NotEqual(QaCause.MissingData, Assert.Single(aggregator.Snapshot()).Cause);
    }

    /// <summary>A gathering trip names a game object node, never a creature (GitHub #1487: "creature 1617 has no spawn" for a herb node).</summary>
    [Fact]
    public void GatherStall_NamingANode_IsNotReadAsAMissingCreature()
    {
        QaSignal s = QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", "stalled 120s: goal=Gather target=1617 quest=0 at -10301.6,1431.5,39.4 map 0"));

        QaClassification c = PlayerbotQaClassifier.Classify(s, new FakeFacts());

        Assert.NotEqual(QaCause.MissingData, c.Cause);
        Assert.DoesNotContain("creature", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExploreStall_WithAStaleQuest_IsTitledByItsGoal_NotTheQuest()
    {
        QaFindingView f = IngestOne(Issue154Line, Gryan());

        Assert.Equal("goal Explore map 0 near -10350,1400", f.Subject);
        Assert.Equal("Explore", f.Goal);
        QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(f, ["bot-qa"], "b", new PlayerbotQaRedactor());
        Assert.Equal("[bot-qa] stall: goal Explore map 0 near -10350,1400", draft.Title);
        Assert.DoesNotContain("Gryan", draft.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("109", draft.Title, StringComparison.Ordinal);

        // The body: the summary line names the goal, a Goal row is shown, and the quest is labelled as what it is.
        Assert.Contains("**stall** - goal Explore map 0 near -10350,1400", draft.Body, StringComparison.Ordinal);
        Assert.Contains("| Goal | Explore |", draft.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("| Quest |", draft.Body, StringComparison.Ordinal);
        Assert.Contains("| Last quest (not the goal) | 109 Report to Gryan Stoutmantle |", draft.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(".lookup quest", draft.Body, StringComparison.Ordinal);
        Assert.Contains("no missing content found for goal Explore", draft.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("found for quest", draft.Body, StringComparison.Ordinal);
    }

    private static FakeFacts GryanTrip(params QaEnderFact[] spawns)
    {
        FakeFacts facts = Gryan();
        facts.Titles[11] = "Report to Gryan";
        facts.Spawns[(0, 963)] = 1;
        facts.Templates[963] = new QaTemplateFacts(0, 1, 1, string.Empty);
        facts.Enders[11] = [.. spawns];
        return facts;
    }

    [Fact]
    public void EnderTripStall_NamesTheEnderItsDistanceAndMap()
    {
        // The ender stands 100 yd east of the stall (-9928.2,47.6): 3-4-5 scaled by 20.
        FakeFacts facts = GryanTrip(new QaEnderFact(963, "Gryan Stoutmantle", 0, -9928.2f + 60f, 47.6f + 80f, 30f));
        QaFindingView f = IngestOne(ReturnTripLine, facts);

        Assert.Contains("the bot was walking to its ender 963 (Gryan Stoutmantle)", f.CauseDetail, StringComparison.Ordinal);
        Assert.Contains("nearest spawn 100 yd from the bot on map 0", f.CauseDetail, StringComparison.Ordinal);
        Assert.StartsWith("no missing content found for quest 11 (Report to Gryan)", f.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void EnderTripStall_WithTheEnderOnAnotherMap_SaysSo()
    {
        FakeFacts facts = GryanTrip(new QaEnderFact(963, "Gryan Stoutmantle", 1, 1634f, -4439f, 30f));
        facts.Spawns.Remove((0, 963)); // the ender spawns on map 1 only: the facts must agree with the spawn it is reported at
        facts.Spawns[(1, 963)] = 1;
        QaFindingView f = IngestOne(ReturnTripLine, facts);

        Assert.Contains("ender 963 (Gryan Stoutmantle), on another map (1; the bot is on map 0)", f.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void EnderTripStall_AtTheEnder_SaysTheBotReachedItsEnderInsteadOfWalkingToIt()
    {
        // #388 / #328: the ender stands within 10 yd of the bot (-9928.2, 47.6), so the bot reached its ender rather than stalling mid-route.
        FakeFacts facts = GryanTrip(new QaEnderFact(963, "Gryan Stoutmantle", 0, -9928.2f + 7f, 47.6f, 32.6f));
        QaFindingView f = IngestOne(ReturnTripLine, facts);

        Assert.Contains("the bot reached its ender 963 (Gryan Stoutmantle) but could not complete the turn-in, nearest spawn 7 yd from the bot on map 0",
            f.CauseDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("the bot was walking to its ender", f.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void EnderTripStall_WithTurnInRefusal_NamesTheRefusalReason()
    {
        FakeFacts facts = GryanTrip(new QaEnderFact(963, "Gryan Stoutmantle", 0, -9928.2f, 47.6f, 32.6f));
        string lineWithRefusal = ReturnTripLine.Replace(
            "attackerFacts=none]",
            "attackerFacts=none,turnInRefusal=InventoryFull]",
            StringComparison.Ordinal);
        QaSignal parsed = QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", lineWithRefusal));
        Assert.Equal("InventoryFull", parsed.Bot?.TurnInRefusal);
        Assert.EndsWith(",turnInRefusal=InventoryFull]", parsed.Bot!.ToLogSuffix(), StringComparison.Ordinal);

        QaFindingView f = IngestOne(lineWithRefusal, facts);
        Assert.Contains("the bot reached its ender 963 (Gryan Stoutmantle) and turn-in was refused (inventory full), nearest spawn 0 yd from the bot on map 0",
            f.CauseDetail, StringComparison.Ordinal);
    }

    /// <summary>The line the real no-route stall of the walk home logs (PlayerbotQuestReturnStallMarkTests): the plan failed, so target=0.</summary>
    private const string NoRouteTripLine = "stalled 10s: goal=Quest target=0 quest=900001 at -8949.0,-132.0,83.5 map 0 return-trip qa[level=10,race=Human,team=Alliance,class=Warrior,goal=Quest,quest=900001,graveyard=0,distance=unknown,source=None,attackerFacts=none]";

    private static FakeFacts FixtureTrip(params QaEnderFact[] enders)
    {
        var facts = new FakeFacts();
        facts.Titles[900001] = "Synthetic quest";
        facts.Enders[900001] = [.. enders];
        return facts;
    }

    [Fact]
    public void NoRouteTripStall_WithOneEnder_NamesThatEnder()
    {
        FakeFacts facts = FixtureTrip(new QaEnderFact(900010, "Synthetic questgiver", 0, -8799.95f, -132.5f, 83.5f));
        QaFindingView f = IngestOne(NoRouteTripLine, facts);

        Assert.Contains("the bot was walking to its ender 900010 (Synthetic questgiver)", f.CauseDetail, StringComparison.Ordinal);
        Assert.Contains("nearest spawn 149 yd from the bot on map 0", f.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRouteTripStall_WithTheOnlyEnderOnAnotherMap_SaysSo()
    {
        FakeFacts facts = FixtureTrip(new QaEnderFact(900010, "Synthetic questgiver", 1, 1634f, -4439f, 30f));
        facts.Spawns[(1, 900010)] = 1;
        facts.Templates[900010] = new QaTemplateFacts(0, 1, 1, string.Empty);
        QaFindingView f = IngestOne(NoRouteTripLine, facts);

        Assert.Contains("ender 900010 (Synthetic questgiver), on another map (1; the bot is on map 0)", f.CauseDetail, StringComparison.Ordinal);
    }

    /// <summary>The quest is complete, so a kill objective without a spawn says nothing about the walk home.</summary>
    [Fact]
    public void NoRouteTripStall_IgnoresTheObjectivesOfTheCompletedQuest()
    {
        FakeFacts facts = FixtureTrip(new QaEnderFact(900010, "Synthetic questgiver", 0, -8799.95f, -132.5f, 83.5f));
        facts.Objectives[900001] = [90]; // no spawn of entry 90 anywhere
        QaFindingView f = IngestOne(NoRouteTripLine, facts);

        Assert.Equal(QaCause.Unknown, f.Cause);
        Assert.Contains("the bot was walking to its ender 900010", f.CauseDetail, StringComparison.Ordinal);

        // Before the quest is complete the same facts are missing data.
        QaFindingView unmarked = IngestOne(NoRouteTripLine.Replace(" return-trip", string.Empty, StringComparison.Ordinal), facts);
        Assert.Equal(QaCause.MissingData, unmarked.Cause);
    }

    [Fact]
    public void NoRouteTripStall_WithTwoEnders_KeepsTheGenericVerdict()
    {
        // Which of two enders the bot was going to is unknown: nothing is claimed.
        FakeFacts facts = FixtureTrip(new QaEnderFact(900010, "First", 0, -8799f, -132f, 83f), new QaEnderFact(900011, "Second", 0, -8700f, -132f, 83f));
        QaFindingView f = IngestOne(NoRouteTripLine, facts);

        Assert.DoesNotContain("ender", f.CauseDetail, StringComparison.Ordinal);
        Assert.StartsWith("no missing content found for quest 900001", f.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRouteStall_WithoutTheReturnTripMark_IsUnchanged()
    {
        FakeFacts facts = FixtureTrip(new QaEnderFact(900010, "Synthetic questgiver", 0, -8799.95f, -132.5f, 83.5f));
        string unmarked = NoRouteTripLine.Replace(" return-trip", string.Empty, StringComparison.Ordinal);
        QaFindingView f = IngestOne(unmarked, facts);

        Assert.Equal("no missing content found for quest 900001 (Synthetic quest)", f.CauseDetail);
    }

    [Fact]
    public void QuestStall_OnANonEnderTarget_KeepsTheGenericVerdict()
    {
        FakeFacts facts = GryanTrip(new QaEnderFact(964, "Someone Else", 0, 0f, 0f, 0f));
        QaFindingView f = IngestOne(ReturnTripLine, facts);

        Assert.Equal("no missing content found for quest 11 (Report to Gryan), target 963", f.CauseDetail);
    }

    /// <summary>
    /// In vanilla one NPC often gives and ends a quest. A bot that stalls on the way to take quest 11 from NPC 963 (who also ends it) was not
    /// going to its ender: without the brain's return-trip mark the finding must not say so.
    /// </summary>
    [Fact]
    public void QuestStall_OnTheWayToTakeTheQuestFromItsEnder_DoesNotClaimAnEnderTrip()
    {
        FakeFacts facts = GryanTrip(new QaEnderFact(963, "Gryan Stoutmantle", 0, -9928.2f + 60f, 47.6f + 80f, 30f));
        QaFindingView f = IngestOne(QuestLine, facts);

        Assert.Equal("no missing content found for quest 11 (Report to Gryan), target 963", f.CauseDetail);
        Assert.DoesNotContain("ender", f.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReturnTripMark_IsParsedOnlyBeforeTheQaContext()
    {
        Assert.True(QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", ReturnTripLine)).ReturnTrip);
        Assert.False(QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", QuestLine)).ReturnTrip);
    }

    [Fact]
    public void QuestStall_StillNamesTheQuest_AfterTheGoal()
    {
        var facts = new FakeFacts();
        facts.Titles[109] = "Report to Gryan Stoutmantle";
        facts.Spawns[(0, 6747)] = 1;
        facts.Templates[6747] = new QaTemplateFacts(0, 1, 1, string.Empty);
        QaFindingView f = IngestOne("stalled 120s: goal=Quest target=6747 quest=109 at -10301.6,1431.5,39.4 map 0", facts);

        Assert.Equal("goal Quest, quest 109 (Report to Gryan Stoutmantle) target 6747 map 0 near -10350,1400", f.Subject);
        QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(f, ["bot-qa"], "b", new PlayerbotQaRedactor());
        Assert.Equal("[bot-qa] stall: goal Quest, quest 109 (Report to Gryan Stoutmantle) target 6747 map 0 near -10350,1400", draft.Title);
        Assert.Contains("| Goal | Quest |", draft.Body, StringComparison.Ordinal);
        Assert.Contains("| Quest | 109 Report to Gryan Stoutmantle |", draft.Body, StringComparison.Ordinal);
        Assert.Contains(".lookup quest Report to Gryan Stoutmantle", draft.Body, StringComparison.Ordinal);
        Assert.Contains("no missing content found for quest 109 (Report to Gryan Stoutmantle)", draft.Body, StringComparison.Ordinal);

        // The real live line, with no content loaded.
        Assert.Equal("goal Quest, quest 11 target 963 map 0 near -9950,0", IngestOne(QuestLine, NoQaContentFacts.Instance).Subject);
    }

    [Fact]
    public void Classifier_DoesNotBlameQuestContent_ForANonQuestGoal()
    {
        // Every objective creature of quest 109 lacks a spawn: for a Quest goal that is missing data, for an Explore goal it says nothing.
        FakeFacts facts = Gryan();
        facts.Objectives[109] = [6747];
        QaSignal explore = QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", Issue154Line));
        QaSignal quest = explore with { Goal = "Quest" };

        QaClassification forQuest = PlayerbotQaClassifier.Classify(quest, facts);
        Assert.Equal(QaCause.MissingData, forQuest.Cause);
        Assert.Contains("quest objective creatures", forQuest.Detail, StringComparison.Ordinal);

        QaClassification forExplore = PlayerbotQaClassifier.Classify(explore, facts);
        Assert.Equal(QaCause.Unknown, forExplore.Cause);
        Assert.Equal("no missing content found for goal Explore", forExplore.Detail);
        Assert.DoesNotContain("109", forExplore.Detail, StringComparison.Ordinal);

        // A Grind target is still checked as a creature, whatever quest the brain remembers.
        QaSignal grind = explore with { Goal = "Grind", Target = 1196 };
        QaClassification forGrind = PlayerbotQaClassifier.Classify(grind, facts);
        Assert.Equal(QaCause.MissingData, forGrind.Cause);
        Assert.Contains("creature entry 1196 has no spawn on map 0", forGrind.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry set-aside (Goal "Entry", no quest) is still checked as a creature: an entry with no template and no spawn is missing data, as
    /// before the goal change (review of #168: treating every set-aside as a quest goal skipped the creature check and said Unknown).
    /// </summary>
    [Fact]
    public void EntrySetAside_WithNoTemplateAndNoSpawn_IsStillMissingData()
    {
        QaSignal entry = QaData.Parse("Playerbots: 3 bots stalled on entry 4321 for 30 min; every bot sets it aside");
        Assert.Equal("Entry", entry.Goal);

        QaClassification c = PlayerbotQaClassifier.Classify(entry, new FakeFacts());
        Assert.Equal(QaCause.MissingData, c.Cause);
        Assert.Equal("creature entry 4321 has no spawn on any map and no creature_template row", c.Detail);
    }

    [Fact]
    public void Fingerprints_OfExistingIssues_AreUnchanged()
    {
        // #154 (goal Explore, stale quest 109), #164 and #163 (goal Explore, quest 0): the markers read from GitHub.
        QaSignal s154 = QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", Issue154Line));
        Assert.Equal(Issue154Key, PlayerbotQaFingerprint.Key(s154));
        Assert.Equal(Issue154Id, PlayerbotQaFingerprint.Of(s154));
        Assert.Equal("f5e1a4226644961d", PlayerbotQaFingerprint.Of(QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander",
            "stalled 120s: goal=Explore target=0 quest=0 at -10314.9,1445.1,38.9 map 0"))));
        Assert.Equal("d11119a0fdd72ae1", PlayerbotQaFingerprint.Of(QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander",
            "stalled 120s: goal=Explore target=0 quest=0 at -10319.2,1451.8,39.3 map 0"))));
        // The golden quest stall of PlayerbotQaAggregatorTests.
        Assert.Equal("428d6d853e162fd3", PlayerbotQaFingerprint.Of(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));
    }

    /// <summary>
    /// A findings.json written before the goal was kept (no <c>Goal</c>, the old subject) still loads; the restored finding keeps its id and its
    /// published issue, gets its goal and subject back from its key, and the next #154 stall folds into it: no second issue is filed.
    /// </summary>
    [Fact]
    public void OldFindingsFile_StillLoads_KeepsIdAndIssue_AndGetsTheGoalSubject()
    {
        const string old = """
            {
              "FormatVersion": 1,
              "SaltBase64": "AQIDBAUGBwgJCgsMDQ4PEA==",
              "Findings": [
                {
                  "Id": "0679c57a28dbea99",
                  "Key": "v1|stall|goal=Explore|quest=109|target=0|map=0|cell=-207,28",
                  "Kind": "Stall",
                  "Subject": "quest 109 (Report to Gryan Stoutmantle) map 0 near -10350,1400",
                  "Count": 8,
                  "DistinctBots": 7,
                  "FirstSeenUtc": "2026-10-11T05:07:52Z",
                  "LastSeenUtc": "2026-10-11T07:06:59Z",
                  "Samples": [
                    { "AtUtc": "2026-10-11T07:06:59Z", "HasPosition": true, "Map": 0, "X": -10313.4, "Y": 1446.7, "Z": 38.7, "Attackers": [],
                      "Message": "Playerbot <bot> stalled 120s: goal=Explore target=0 quest=109 at -10313.4,1446.7,38.7 map 0; it gives up that goal" }
                  ],
                  "Quest": 109,
                  "Target": 0,
                  "Map": 0,
                  "QuestTitle": "Report to Gryan Stoutmantle",
                  "Cause": "Unknown",
                  "CauseDetail": "no missing content found for quest 109 (Report to Gryan Stoutmantle)",
                  "Severity": 91.7,
                  "Publication": { "State": "Published", "Issue": 154, "Reason": null },
                  "LastCommentUtc": null,
                  "OccurrencesReported": 8
                }
              ],
              "Bots": {},
              "Ledger": { "CreatesUtc": [], "CommentsUtc": [] }
            }
            """;
        File.WriteAllText(Path.Combine(_directory, "findings.json"), old);
        QaStoreFile loaded = new PlayerbotQaStore(_directory, new ListLogger()).Load()!;
        Assert.NotNull(loaded);

        var aggregator = new PlayerbotQaAggregator(100, 5, Salt);
        Assert.Equal(0, aggregator.Restore(new QaAggregatorState(loaded.Findings, loaded.Bots)));
        QaFindingView restored = Assert.Single(aggregator.Snapshot());
        Assert.Equal(Issue154Id, restored.Id);
        Assert.Equal(Issue154Key, restored.Key);
        Assert.Equal(new QaPublication(QaPublicationState.Published, 154), restored.Publication);
        Assert.Equal("Explore", restored.Goal);
        Assert.Equal("goal Explore map 0 near -10350,1400", restored.Subject);
        Assert.Equal(PlayerbotQaIssueComposer.MarkerOf(Issue154Id),
            PlayerbotQaIssueComposer.Compose(restored, ["bot-qa"], "b", new PlayerbotQaRedactor()).Marker);

        // The next #154 stall is the same finding, still Published#154 (the publisher comments on it, it never creates a new issue).
        string? id = aggregator.Ingest(QaData.Parse(QaData.BotLine(QaData.Bot2, "Graveweaver", Issue154Line)), Gryan(), QaData.T0);
        Assert.Equal(Issue154Id, id);
        QaFindingView after = Assert.Single(aggregator.Snapshot());
        Assert.Equal(9, after.Count);
        Assert.Equal(new QaPublication(QaPublicationState.Published, 154), after.Publication);
        Assert.Equal("no missing content found for goal Explore", after.CauseDetail);
    }

    [Fact]
    public void Report_And_Chat_NameTheGoal_AndLabelTheLastQuest()
    {
        QaFindingView f = IngestOne(Issue154Line, Gryan());
        var redactor = new PlayerbotQaRedactor();

        string md = PlayerbotQaReport.RenderMarkdown([f], QaData.T0, "b", redactor);
        Assert.Contains("## 1. stall: goal Explore map 0 near -10350,1400", md, StringComparison.Ordinal);
        Assert.Contains("- Last quest 109 (not the goal): Report to Gryan Stoutmantle", md, StringComparison.Ordinal);
        Assert.DoesNotContain("- Quest 109", md, StringComparison.Ordinal);

        IReadOnlyList<string> show = PlayerbotQaReport.ChatShow(f, redactor);
        Assert.Equal($"{f.Id} stall: goal Explore map 0 near -10350,1400", show[0]);
        Assert.Contains("last quest 109 (not the goal): Report to Gryan Stoutmantle", show);
        Assert.Contains("stall: goal Explore map 0 near -10350,1400", PlayerbotQaReport.ChatTop([f], 10, redactor)[1], StringComparison.Ordinal);

        string json = PlayerbotQaReport.RenderJson([f], QaData.T0, "b", redactor);
        Assert.Contains("\"Goal\": \"Explore\"", json, StringComparison.Ordinal);
    }
}
