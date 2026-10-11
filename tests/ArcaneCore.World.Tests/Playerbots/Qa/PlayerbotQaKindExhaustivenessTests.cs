using System.Text.RegularExpressions;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// Every <see cref="QaSignalKind"/> goes through every switch of bot QA (the kind table, the fingerprint, the subject, the classifier, the severity, the
/// label, the aggregator, the composer, the report and a dry-run publish), so a kind added to the enum without its cases fails here instead of
/// throwing on the QA task.
/// </summary>
public sealed class PlayerbotQaKindExhaustivenessTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    public static TheoryData<int> Kinds => [.. Enum.GetValues<QaSignalKind>().Select(kind => (int)kind)];

    /// <summary>A signal of <paramref name="kind"/> with every field a kind may read set to something plausible.</summary>
    internal static QaSignal Sample(QaSignalKind kind) => new()
    {
        Kind = kind, AtUtc = QaData.T0, BotKey = QaData.Bot1, BotCount = 3, Goal = "Quest", Quest = 1656, Target = 6747, HasPosition = true, Map = 0,
        X = 10, Y = 20, Z = 30, Attackers = [3], ExceptionType = "InvalidOperationException", FaultCode = "playerbot-recovery-x", Detail = "MOVEMENT",
        Relay = 7, PathId = 2, Entry = 3, Seconds = 120, Count = 3, Minutes = 30, Spell = 133, SpellName = "Fireball", SpellRank = "Rank 1", EffectIndex = 0,
        EffectName = "SchoolDamage", EffectValue = 14, Reason = kind == QaSignalKind.SpellRefused ? "NoPower" : QaSpellGaps.Effect, BotClass = 8,
        Message = "a sample line",
    };

    [Fact]
    public void TheKindTable_HasExactlyOneRowPerKind_WithUniqueLabelsAndSlugs()
    {
        QaSignalKind[] kinds = Enum.GetValues<QaSignalKind>();
        Assert.Equal(kinds.Length, QaKinds.All.Count);
        foreach (QaSignalKind kind in kinds) Assert.Equal(kind, QaKinds.Of(kind).Kind);
        Assert.Equal(kinds.Length, QaKinds.All.Select(r => r.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(kinds.Length, QaKinds.All.Select(r => r.Slug).Distinct(StringComparer.Ordinal).Count());
        Assert.All(QaKinds.All, r => Assert.Matches(new Regex("^[a-z][a-z-]{1,40}$"), r.Slug));
        Assert.All(QaKinds.All, r => Assert.True(r.GitHubLabel.Length <= 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => QaKinds.Of((QaSignalKind)999));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryKind_GoesThroughEverySwitch(int kindValue)
    {
        var kind = (QaSignalKind)kindValue;
        QaSignal signal = Sample(kind);

        string key = PlayerbotQaFingerprint.Key(signal);
        string subject = PlayerbotQaAggregator.Subject(signal, null);
        QaClassification cause = PlayerbotQaClassifier.Classify(signal, new FakeFacts());
        double severity = PlayerbotQaClassifier.Severity(kind, 1, 1, QaData.T0, QaData.T0);

        Assert.StartsWith(PlayerbotQaFingerprint.Version + "|", key, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(subject));
        Assert.NotEqual(kind.ToString(), subject); // the generic fallback of Subject: the kind has no case of its own
        Assert.False(string.IsNullOrWhiteSpace(cause.Detail));
        Assert.True(severity >= QaKinds.Of(kind).Weight);
        Assert.Equal(QaKinds.Of(kind).Label, QaFindingView.LabelOf(kind));

        var aggregator = new PlayerbotQaAggregator(100, 5);
        string? id = aggregator.Ingest(signal, new FakeFacts(), QaData.T0);
        Assert.NotNull(id);
        QaFindingView view = aggregator.Get(id!)!;
        Assert.Equal(kind, view.Kind);
        Assert.Equal(1, view.Count);
        _ = view.BotIndependent;

        var redactor = new PlayerbotQaRedactor();
        QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(view, ["bot-qa"], "test-build", redactor);
        Assert.StartsWith(PlayerbotQaIssueComposer.MarkerOf(view.Id), draft.Body, StringComparison.Ordinal);
        Assert.Contains(view.KindLabel, draft.Title, StringComparison.Ordinal);
        Assert.Equal(QaKinds.Of(kind).SpellKind, draft.Body.Contains("## Spell\n", StringComparison.Ordinal));
        Assert.NotEmpty(PlayerbotQaIssueComposer.Comment(view, 1, redactor));
        Assert.NotEmpty(PlayerbotQaReport.ChatShow(view, redactor));
        Assert.Contains(view.Id, PlayerbotQaReport.RenderMarkdown([view], QaData.T0, "b", redactor), StringComparison.Ordinal);
        Assert.Contains(view.Id, PlayerbotQaReport.RenderJson([view], QaData.T0, "b", redactor), StringComparison.Ordinal);

        // A dry run of the finding (thresholds of 1) writes its would-be issue: the publish path knows the kind.
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, MinOccurrences = 1, MinDistinctBots = 1 } };
        var publisher = new PlayerbotQaPublisher(options, _directory, aggregator, redactor, new ListLogger(), _ => null, null, new ManualTime(QaData.T0), "b");
        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_directory, "pending-issues", view.Id + ".md")));
    }

    [Fact]
    public void EveryKind_HasItsOwnKey()
    {
        string[] keys = [.. Enum.GetValues<QaSignalKind>().Select(kind => PlayerbotQaFingerprint.Key(Sample(kind)))];
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AnUndefinedKind_ThrowsInTheFingerprintAndTheClassifier_InsteadOfPassingAsAnotherKind()
    {
        QaSignal unknown = Sample(QaSignalKind.Stall) with { Kind = (QaSignalKind)999 };
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerbotQaFingerprint.Key(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerbotQaClassifier.Classify(unknown, new FakeFacts()));
    }
}
