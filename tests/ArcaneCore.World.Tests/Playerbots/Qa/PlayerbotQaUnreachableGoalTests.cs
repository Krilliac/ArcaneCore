using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The skip of a goal no walk reaches (PlayerbotWorldDestinations.SkipUnreachable) is not silent: the feature writes one warning per report
/// (ManagedPlayerbotFeature.LogUnreachable) and bot QA turns it into an <see cref="QaSignalKind.UnreachableGoal"/> finding, keyed by the goal
/// and what it names, carrying no bot name or id.
/// </summary>
public sealed class PlayerbotQaUnreachableGoalTests
{
    private const string Report = "goal unreachable on foot: goal=Quest target=6747 quest=1656 at -5608.8,-439.4,401.2 map 0 components=12/3";

    [Fact]
    public void TheLoggedLine_IsParsedAsAnUnreachableGoal_WithNoBotIdentity()
    {
        var log = new ListLogger();
        ManagedPlayerbotFeature.LogUnreachable(log, QaData.Bot1, "Ironwander", Report, qaContext: null);

        QaSignal s = QaData.Parse(Assert.Single(log.Entries).Message);
        Assert.Equal(QaSignalKind.UnreachableGoal, s.Kind);
        Assert.Equal("Quest", s.Goal);
        Assert.Equal(6747u, s.Target);
        Assert.Equal(1656u, s.Quest);
        Assert.Equal(-5608.8f, s.X);
        Assert.Equal("components 12/3", s.Detail);
        Assert.Equal(QaData.Bot1, s.BotKey);
        Assert.DoesNotContain("Ironwander", s.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(QaData.Bot1.ToString(), s.Message, StringComparison.Ordinal);
        Assert.StartsWith("Playerbot <bot> goal unreachable on foot: goal=Quest target=6747", s.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFinding_IsKeyedByGoalAndTarget_NotByTheBotsPlace()
    {
        QaSignal a = QaData.Parse("Playerbot " + QaData.Bot1 + " (A) " + Report);
        QaSignal b = QaData.Parse("Playerbot " + QaData.Bot1 + " (B) " + Report.Replace("-5608.8,-439.4,401.2", "-5000.0,-400.0,300.0"));
        QaSignal other = QaData.Parse("Playerbot " + QaData.Bot1 + " (A) " + Report.Replace("target=6747", "target=6748"));
        Assert.Equal(PlayerbotQaFingerprint.Key(a), PlayerbotQaFingerprint.Key(b));
        Assert.NotEqual(PlayerbotQaFingerprint.Key(a), PlayerbotQaFingerprint.Key(other));
        Assert.Contains("unreachable on foot", PlayerbotQaAggregator.Subject(a, null), StringComparison.Ordinal);
        Assert.Equal(QaCause.MissingData, PlayerbotQaClassifier.Classify(a, new FakeFacts()).Cause);
    }

    private const string TeldrassilReport = "goal unreachable on foot: goal=Grind target=2031 quest=456 at -3565.3,-4342.6,11.8 map 1 components=12764/284";

    [Fact]
    public void AGoalAcrossWater_IsABehaviourBug_NotMissingData()
    {
        var facts = new FakeFacts();
        facts.SpawnPoints[(1, 2031)] = [(10270f, 744f), (10300f, 760f)];
        QaSignal s = QaData.Parse("Playerbot " + QaData.Bot1 + " (A) " + TeldrassilReport);

        QaClassification c = PlayerbotQaClassifier.Classify(s, facts);

        Assert.Equal(QaCause.BehaviourBug, c.Cause);
        Assert.Contains("goal across water", c.Detail, StringComparison.Ordinal);
        Assert.Contains("bot-travel/leg-failed", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AGoalOnTheBotsOwnSide_StaysMissingData()
    {
        var facts = new FakeFacts();
        facts.SpawnPoints[(1, 2031)] = [(-3600f, -4300f)];
        QaSignal s = QaData.Parse("Playerbot " + QaData.Bot1 + " (A) " + TeldrassilReport);

        Assert.Equal(QaCause.MissingData, PlayerbotQaClassifier.Classify(s, facts).Cause);
    }

    [Fact]
    public void AGoalWithSomeSpawnsOnTheBotsSide_StaysMissingData()
    {
        var facts = new FakeFacts();
        facts.SpawnPoints[(1, 2031)] = [(10270f, 744f), (-3600f, -4300f)];
        QaSignal s = QaData.Parse("Playerbot " + QaData.Bot1 + " (A) " + TeldrassilReport);

        Assert.Equal(QaCause.MissingData, PlayerbotQaClassifier.Classify(s, facts).Cause);
    }
}
