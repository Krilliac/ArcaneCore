using System.Text.Json.Nodes;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>Rate per hour, first and last seen and bots affected in the chat top list and the report.md / report.json exports.</summary>
public sealed class PlayerbotQaRateTests
{
    private static readonly PlayerbotQaRedactor Redactor = new();

    [Fact]
    public void RatePerHour_IsTheCountOverTheTimeSinceFirstSeen_WithAOneMinuteFloor()
    {
        QaFindingView twelve = QaData.View("aaaaaaaaaaaaaaaa", count: 12);
        QaFindingView fresh = QaData.View("bbbbbbbbbbbbbbbb", count: 3);

        Assert.Equal(6, PlayerbotQaReport.RatePerHour(twelve, QaData.T0.AddHours(2)), 6);
        Assert.Equal(2, PlayerbotQaReport.ObservedHours(twelve, QaData.T0.AddHours(2)), 6);
        // Seen three times in the same instant: 180 per hour, not a division by zero or millions per hour.
        Assert.Equal(180, PlayerbotQaReport.RatePerHour(fresh, QaData.T0), 6);
        Assert.Equal("6.0/h", PlayerbotQaReport.RateText(6));
        Assert.Equal("<0.1/h", PlayerbotQaReport.RateText(0.01));
    }

    [Fact]
    public void ChatTop_ShowsRateBotsAffectedFirstAndLastSeen()
    {
        QaFindingView f = QaData.View("aaaaaaaaaaaaaaaa", count: 12, bots: 4, last: QaData.T0.AddMinutes(90));

        IReadOnlyList<string> lines = PlayerbotQaReport.ChatTop([f], 5, Redactor, now: QaData.T0.AddHours(2));

        string line = lines[1];
        Assert.Contains(" x12 6.0/h bots 4 ", line, StringComparison.Ordinal);
        Assert.Contains("30m ago, first 2h", line, StringComparison.Ordinal); // last seen 30 minutes ago, first seen two hours ago
        Assert.Contains("stall", line, StringComparison.Ordinal);
        Assert.True(line.Length <= 223);
    }

    [Fact]
    public void ChatShow_StatesTheRate()
    {
        QaFindingView f = QaData.View("aaaaaaaaaaaaaaaa", count: 12, bots: 4, last: QaData.T0.AddHours(2));

        string line = PlayerbotQaReport.ChatShow(f, Redactor)[2];

        Assert.Contains("seen 12 times by 4 bots, 6.0/h, sev", line, StringComparison.Ordinal);
        Assert.Contains("first 2026-10-10", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_HasRateFirstSeenLastSeenAndBotsColumns_AndTheDetailBullet()
    {
        QaFindingView f = QaData.View("aaaaaaaaaaaaaaaa", count: 12, bots: 4, last: QaData.T0.AddMinutes(90));

        string md = PlayerbotQaReport.RenderMarkdown([f], QaData.T0.AddHours(2), "b", Redactor);

        Assert.Contains("| # | Id | Severity | Seen | Rate/h | First seen | Last seen | Bots | Cause |", md, StringComparison.Ordinal);
        Assert.Contains("| 1 | `aaaaaaaa` | 30.0 | 12 | 6.0 | 2026-10-10 12:00:00Z | 30m ago | 4 |", md, StringComparison.Ordinal);
        Assert.Contains("seen 12 times by 4 bots affected (6.0/h over 2.0 h), first 2026-10-10 12:00:00Z, last 2026-10-10 13:30:00Z", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_StaleTable_HasTheSameNewColumns()
    {
        DateTime start = QaData.T0.AddDays(2);
        QaFindingView stale = QaData.View("aaaaaaaaaaaaaaaa", count: 24, bots: 3, last: QaData.T0.AddHours(1));
        QaFindingView current = QaData.View("bbbbbbbbbbbbbbbb", count: 2, last: start.AddMinutes(1));

        string md = PlayerbotQaReport.RenderMarkdown([stale, current], start.AddHours(1), "b", Redactor, since: start);

        Assert.Contains("| Id | Severity | Seen | Rate/h | First seen | Last seen | Bots | Kind |", md, StringComparison.Ordinal);
        Assert.Contains("| `aaaaaaaa` | 30.0 | 24 | 0.5 | 2026-10-10 12:00:00Z |", md, StringComparison.Ordinal); // 24 over the 49 hours since the first sighting
    }

    [Fact]
    public void Json_CarriesRatePerHourAndObservedHours_BesideFirstLastAndBots()
    {
        QaFindingView f = QaData.View("aaaaaaaaaaaaaaaa", count: 12, bots: 4, last: QaData.T0.AddMinutes(90));

        string json = PlayerbotQaReport.RenderJson([f], QaData.T0.AddHours(2), "b", Redactor);

        JsonNode entry = JsonNode.Parse(json)!["Findings"]![0]!;
        Assert.Equal(6.0, (double)entry["RatePerHour"]!, 6);
        Assert.Equal(2.0, (double)entry["ObservedHours"]!, 6);
        Assert.Equal(4, (int)entry["DistinctBots"]!);
        Assert.Equal(QaData.T0, ((DateTime)entry["FirstSeenUtc"]!).ToUniversalTime());
        Assert.Equal(QaData.T0.AddMinutes(90), ((DateTime)entry["LastSeenUtc"]!).ToUniversalTime());
    }
}
