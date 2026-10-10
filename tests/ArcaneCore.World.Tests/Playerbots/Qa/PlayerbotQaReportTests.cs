using System.Text.Json.Nodes;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>Ranking, chat listings, id resolution and the Markdown/JSON exports.</summary>
public sealed class PlayerbotQaReportTests
{
    private static readonly PlayerbotQaRedactor Redactor = new();

    [Fact]
    public void Rank_OrdersBySeverity_ThenCount_ThenNewest_ThenId()
    {
        QaFindingView low = QaData.View("0000000000000001", severity: 10, count: 99);
        QaFindingView high = QaData.View("0000000000000002", severity: 50, count: 1);
        QaFindingView sameSevMore = QaData.View("0000000000000004", severity: 30, count: 9);
        QaFindingView sameSevLess = QaData.View("0000000000000003", severity: 30, count: 2);
        QaFindingView tieOld = QaData.View("0000000000000005", severity: 20, count: 5, last: QaData.T0);
        QaFindingView tieNew = QaData.View("0000000000000006", severity: 20, count: 5, last: QaData.T0.AddHours(1));
        QaFindingView tieIdB = QaData.View("00000000000000b7", severity: 15, count: 5);
        QaFindingView tieIdA = QaData.View("00000000000000a7", severity: 15, count: 5);

        IReadOnlyList<QaFindingView> ranked = PlayerbotQaReport.Rank([low, tieIdB, sameSevLess, tieOld, high, tieIdA, sameSevMore, tieNew]);

        Assert.Equal(
            [high.Id, sameSevMore.Id, sameSevLess.Id, tieNew.Id, tieOld.Id, tieIdA.Id, tieIdB.Id, low.Id], ranked.Select(f => f.Id));
    }

    [Fact]
    public void Rank_IsStableAcrossInputOrder()
    {
        QaFindingView[] findings = [.. Enumerable.Range(1, 12).Select(i => QaData.View($"{i:x16}", severity: i % 3 * 10, count: i % 4))];

        string[] forward = [.. PlayerbotQaReport.Rank(findings).Select(f => f.Id)];
        string[] backward = [.. PlayerbotQaReport.Rank(findings.Reverse()).Select(f => f.Id)];

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void ChatTop_Empty_SaysSo()
        => Assert.Equal(["Bot QA: no findings."], PlayerbotQaReport.ChatTop([], 10, Redactor));

    [Fact]
    public void ChatTop_ListsTheRankedTop_AndClampsN()
    {
        QaFindingView[] findings = [.. Enumerable.Range(1, 40).Select(i => QaData.View($"{i:x16}", severity: i))];

        IReadOnlyList<string> three = PlayerbotQaReport.ChatTop(findings, 3, Redactor);
        IReadOnlyList<string> huge = PlayerbotQaReport.ChatTop(findings, 1000, Redactor);
        IReadOnlyList<string> zero = PlayerbotQaReport.ChatTop(findings, 0, Redactor);

        Assert.Equal(4, three.Count); // header + 3
        Assert.Contains("top 3 of 40", three[0], StringComparison.Ordinal);
        Assert.StartsWith("#1 " + PlayerbotQaFingerprint.ShortId($"{40:x16}"), three[1], StringComparison.Ordinal);
        Assert.Equal(PlayerbotQaReport.MaxChatFindings + 1, huge.Count);
        Assert.Equal(2, zero.Count);
    }

    [Fact]
    public void ChatLines_AreBounded_AndRedacted()
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["Ironwander"]);
        QaFindingView f = QaData.View("aaaaaaaaaaaaaaaa", subject: "Ironwander near 203.0.113.9 " + new string('q', 600), causeDetail: new string('d', 600) + " Ironwander",
            samples: [new QaSample(QaData.T0, false, 0, 0, 0, 0, [], "Playerbot Ironwander " + new string('m', 600))]);

        string[] lines = [.. PlayerbotQaReport.ChatTop([f], 5, redactor), .. PlayerbotQaReport.ChatShow(f, redactor)];

        Assert.All(lines, line => Assert.True(line.Length <= 223, line.Length.ToString()));
        Assert.All(lines, line => Assert.DoesNotContain("Ironwander", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.DoesNotContain("203.0.113.9", line, StringComparison.Ordinal));
    }

    [Fact]
    public void ChatShow_HasTheEvidenceAndAGoCommand()
    {
        QaFindingView f = QaData.View("aaaaaaaaaaaaaaaa");

        IReadOnlyList<string> lines = PlayerbotQaReport.ChatShow(f, Redactor);

        Assert.Contains(lines, l => l.StartsWith("aaaaaaaaaaaaaaaa stall:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("go there: .go xyz -5608.8 -439.4 401.2 0", StringComparison.Ordinal));
    }

    [Fact]
    public void GoHint_UsesTheMiddleSampleWithAPosition_AndIsNullWithout()
    {
        QaSample At(float x, bool pos = true) => new(QaData.T0, pos, 1, x, 0, 0, [], "m");
        QaFindingView three = QaData.View("aaaaaaaaaaaaaaaa", samples: [At(1), At(2), At(3)]);
        QaFindingView none = QaData.View("bbbbbbbbbbbbbbbb", samples: [At(1, pos: false)]);

        Assert.Equal(".go xyz 2.0 0.0 0.0 1", PlayerbotQaReport.GoHint(three));
        Assert.Null(PlayerbotQaReport.GoHint(none));
    }

    [Fact]
    public void TryResolve_AcceptsAUniquePrefix_AndExplainsEveryRefusal()
    {
        QaFindingView[] findings = [QaData.View("abcd111122223333"), QaData.View("abcd444455556666"), QaData.View("ef01111122223333")];

        Assert.True(PlayerbotQaReport.TryResolve(findings, "ef01", out QaFindingView? one, out string error1));
        Assert.Equal("ef01111122223333", one!.Id);
        Assert.Equal(string.Empty, error1);
        Assert.True(PlayerbotQaReport.TryResolve(findings, " ABCD4 ", out QaFindingView? two, out _)); // trimmed and case-insensitive
        Assert.Equal("abcd444455556666", two!.Id);

        Assert.False(PlayerbotQaReport.TryResolve(findings, "abcd", out QaFindingView? ambiguous, out string error2));
        Assert.Null(ambiguous);
        Assert.Contains("More than one", error2, StringComparison.Ordinal);
        Assert.False(PlayerbotQaReport.TryResolve(findings, "ab", out _, out string error3));
        Assert.Contains("at least 4", error3, StringComparison.Ordinal);
        Assert.False(PlayerbotQaReport.TryResolve(findings, "zzzz", out _, out string error4));
        Assert.Contains("at least 4", error4, StringComparison.Ordinal);
        Assert.False(PlayerbotQaReport.TryResolve(findings, "0000", out _, out string error5));
        Assert.Contains("No finding", error5, StringComparison.Ordinal);
        Assert.False(PlayerbotQaReport.TryResolve(findings, new string('a', 17), out _, out _));
    }

    [Fact]
    public void Markdown_ListsFindingsInRankOrder_Redacted_WithPipesEscaped()
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["Ironwander"]);
        QaFindingView top = QaData.View("1111111111111111", severity: 80, subject: "quest 1 | Ironwander from 203.0.113.9");
        QaFindingView second = QaData.View("2222222222222222", severity: 20, subject: "quest 2");

        string md = PlayerbotQaReport.RenderMarkdown([second, top], QaData.T0, "build 1", redactor);

        Assert.True(md.IndexOf("11111111", StringComparison.Ordinal) < md.IndexOf("22222222", StringComparison.Ordinal));
        Assert.Contains("2 findings", md, StringComparison.Ordinal);
        Assert.Contains("quest 1 \\| <name> from <ip>", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Ironwander", md, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_Empty_SaysNoFindings()
        => Assert.Contains("No findings.", PlayerbotQaReport.RenderMarkdown([], QaData.T0, "b", Redactor), StringComparison.Ordinal);

    [Fact]
    public void Json_IsRankedRedacted_AndHasNoBotIdentity()
    {
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["Ironwander"]);
        QaSample sample = new(QaData.T0, true, 0, 1.5f, 2.5f, 3.5f, [7, 8], $"Playerbot {QaData.Bot1} (Ironwander) stalled from 203.0.113.9 {QaData.FakeToken}");
        QaFindingView low = QaData.View("2222222222222222", severity: 5);
        QaFindingView high = QaData.View("1111111111111111", severity: 70, samples: [sample, new QaSample(QaData.T0, false, 0, 9, 9, 9, [], "no position")]);

        string json = PlayerbotQaReport.RenderJson([low, high], QaData.T0, "b", redactor);

        JsonNode root = JsonNode.Parse(json)!;
        Assert.Equal(2, (int)root["FindingCount"]!);
        JsonArray findings = root["Findings"]!.AsArray();
        Assert.Equal("1111111111111111", (string)findings[0]!["Id"]!);
        Assert.Equal(1, (int)findings[0]!["Rank"]!);
        Assert.Equal("2222222222222222", (string)findings[1]!["Id"]!);
        JsonArray samples = findings[0]!["Samples"]!.AsArray();
        Assert.Null(samples[1]!["X"]); // an unknown position is not exported as 0
        Assert.Equal(1.5f, (float)samples[0]!["X"]!);
        Assert.DoesNotContain("Ironwander", json, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", json, StringComparison.Ordinal);
        Assert.DoesNotContain(QaData.FakeToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(QaData.Bot1.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }
}
