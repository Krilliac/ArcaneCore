using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The subject of an anomaly finding is a readable example of the latest detail; the finding's identity (the fingerprint) is unchanged.</summary>
public sealed class PlayerbotQaAnomalySubjectTests
{
    private sealed class Sink : IAnnounceSink
    {
        public List<AnnounceDelivery> Sent { get; } = [];
        public void Deliver(AnnounceDelivery delivery) => Sent.Add(delivery);
    }

    private static QaSignal Anomaly(string detail, string goal = "bot-motion", string reason = "wall-crossed")
        => new()
        {
            Kind = QaSignalKind.Anomaly, AtUtc = QaData.T0, Goal = goal, Reason = reason, FaultCode = goal + "/" + reason, Detail = detail,
            Message = goal + "/" + reason + ": " + detail, Occurrences = 5, BotCount = 1,
        };

    private static string SubjectOf(string detail)
    {
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(Anomaly(detail), NoQaContentFacts.Instance, QaData.T0);
        return Assert.Single(aggregator.Snapshot()).Subject;
    }

    [Fact]
    public void Subject_KeepsDecimalNumbersAndCase()
    {
        string subject = SubjectOf("Walked from -8949.5,-132.4,83.1 to -8950.2,-130.0,83.1 through a wall (Stormwind)");
        Assert.Equal("bot-motion/wall-crossed: Walked from -8949.5,-132.4,83.1 to -8950.2,-130.0,83.1 through a wall (Stormwind)", subject);
    }

    [Fact]
    public void Subject_KeepsMillisecondsAndEntries()
    {
        string subject = SubjectOf("think 41.7 ms (over 25.0 ms) driver=brain goal=vendor entry 1234");
        Assert.Contains("41.7 ms (over 25.0 ms)", subject, StringComparison.Ordinal);
        Assert.Contains("entry 1234", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Subject_MasksGuidsAndHex_ButNotOtherNumbers()
    {
        string subject = SubjectOf("gameobject 3f2b1c9e-0a4d-4e1b-9c55-7d1a2b3c4d5e at 0xDEADBEEF, guid 987654321 entry 55 and guid=0x1F");
        Assert.DoesNotContain("3f2b1c9e", subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEADBEEF", subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654321", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("0x1F", subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entry 55", subject, StringComparison.Ordinal);
        Assert.Contains("guid #", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Subject_CollapsesWhitespace_AndCutsOnAWordBoundaryWithAnEllipsis()
    {
        string detail = string.Join("  \n ", Enumerable.Range(0, 60).Select(i => "word" + i));
        string subject = SubjectOf(detail);
        string example = subject["bot-motion/wall-crossed: ".Length..];
        Assert.EndsWith("...", example, StringComparison.Ordinal);
        Assert.True(example.Length <= 123, example.Length.ToString());
        Assert.DoesNotContain("  ", example, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', example);
        string body = example[..^3];
        Assert.Matches(@"^(word\d+ )*word\d+$", body); // no word cut in the middle: it ends on a whole word
        Assert.StartsWith("word0 word1 ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Subject_MasksAnObjectGuidInItsHighColonHexForm()
    {
        // ObjectGuid.ToString() is "{High}:{Value:X16}" (the craft-failed detail names its gather target this way).
        string subject = SubjectOf("gather or disenchant loot timed out for target GameObject:F11000000000A1B2 near 3 nodes");
        Assert.DoesNotContain("F11000000000A1B2", subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target GameObject:# near 3 nodes", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortDetail_IsNotCut()
    {
        Assert.EndsWith(": Ok 5", SubjectOf("Ok   5"), StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_OfAnAnomaly_IsUnchanged_AndIgnoresTheDetail()
    {
        string key = PlayerbotQaFingerprint.Key(Anomaly("walked 1.5 yards"));
        Assert.Equal(PlayerbotQaFingerprint.Version + "|anomaly|bot-motion|wall-crossed", key);
        Assert.Equal(key, PlayerbotQaFingerprint.Key(Anomaly("a completely different detail 99")));

        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.Ingest(Anomaly("first 1"), NoQaContentFacts.Instance, QaData.T0);
        aggregator.Ingest(Anomaly("second 2"), NoQaContentFacts.Instance, QaData.T0);
        QaFindingView finding = Assert.Single(aggregator.Snapshot());
        Assert.Equal(PlayerbotQaFingerprint.Hash(key), finding.Id);
        Assert.EndsWith(": second 2", finding.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_StillMasksNumbersAndLowerCases()
    {
        Assert.Equal("think #.# ms", PlayerbotQaFingerprint.Normalise("Think 41.7 ms"));
    }

    [Fact]
    public void Announcement_RedactsBotNamesAndGuids_ButKeepsTheNumbers()
    {
        var sink = new Sink();
        var announcer = new OpsAnnouncer(new AnnounceOptions(), sink, NullLogger.Instance, () => 1_000_000);
        var redactor = new PlayerbotQaRedactor();
        redactor.SetNames(["Thrallbot"]);
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.FindingCreated = view => PlayerbotQaAnnouncements.Announce(announcer, view, redactor);

        aggregator.Ingest(Anomaly("Thrallbot walked 3.5 yd through a wall at -8949.5,-132.4 guid 77"), NoQaContentFacts.Instance, QaData.T0);

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("[BotQa]", sent.StaffChat, StringComparison.Ordinal);
        Assert.DoesNotContain("Thrallbot", sent.StaffChat, StringComparison.Ordinal);
        Assert.Contains("<name>", sent.StaffChat, StringComparison.Ordinal);
        Assert.Contains("3.5 yd", sent.StaffChat, StringComparison.Ordinal);
        Assert.Contains("-8949.5,-132.4", sent.StaffChat, StringComparison.Ordinal);
        Assert.DoesNotContain("guid 77", sent.StaffChat, StringComparison.Ordinal);
    }
}
