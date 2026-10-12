using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.Ops.Announce;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

internal sealed class RecordingSink : IAnnounceSink
{
    public List<AnnounceDelivery> Sent { get; } = [];

    public void Deliver(AnnounceDelivery delivery) => Sent.Add(delivery);
}

internal sealed class RecordingReporter : IHotSwapReporter
{
    private readonly List<HotSwapEvent> _events = [];

    public IReadOnlyList<HotSwapEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Report(HotSwapEvent swap)
    {
        lock (_events)
        {
            _events.Add(swap);
        }
    }
}

/// <summary>The generic announcer: switches, severity floor, rate limit, duplicate window, text hygiene.</summary>
public sealed class OpsAnnouncerTests
{
    private long _now = 1_000_000;

    private (OpsAnnouncer Announcer, RecordingSink Sink, HotSwapAnnounceOptions Options) Rig(Action<HotSwapAnnounceOptions>? configure = null, bool enabled = true)
    {
        var options = new HotSwapAnnounceOptions();
        configure?.Invoke(options);
        var sink = new RecordingSink();
        return (new OpsAnnouncer(new AnnounceOptions { Enabled = enabled, HotSwap = options }, sink, NullLogger.Instance, () => _now), sink, options);
    }

    [Fact]
    public void Defaults_NotifyStaffOnly_AndNeverThePlayers()
    {
        var options = new HotSwapAnnounceOptions();
        Assert.True(options.GmNotify);
        Assert.False(options.WorldMessage);
        Assert.True(options.IncludeDetails);
        Assert.False(options.AllowNamesInWorldMessage);
        Assert.False(options.WorldMessageOnFailure);

        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig();
        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "Live data reload 'x' applied", "by Admin (Gm); 12 ms");

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("by Admin", sent.StaffChat);
        Assert.Contains("12 ms", sent.StaffChat);
        Assert.StartsWith("[HotSwap]", sent.StaffNotify);
        Assert.Null(sent.WorldChat);
    }

    [Fact]
    public void WorldMessage_CarriesOnlyTheShortText_NeverTheStaffDetail()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.WorldMessage = true);

        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "Live update applied", "by secretaccount (Gm); sha 123abc", [new("sha256", "deadbeef0123")]);

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("Live update applied", sent.WorldChat);
        Assert.DoesNotContain("secretaccount", sent.WorldChat);
        Assert.DoesNotContain("deadbeef", sent.WorldChat);
        Assert.DoesNotContain("123abc", sent.WorldChat);
        Assert.Contains("secretaccount", sent.StaffChat);
        Assert.Contains("deadbeef0123", sent.StaffChat);
    }

    [Fact]
    public void IncludeDetailsOff_KeepsTheStaffLineShort()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.IncludeDetails = false);

        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "short", "secret detail", [new("k", "v")]);

        Assert.DoesNotContain("secret detail", Assert.Single(sink.Sent).StaffChat);
        Assert.DoesNotContain("k=v", sink.Sent[0].StaffChat);
    }

    [Fact]
    public void NamesReachTheWorldMessageOnlyWhenTheCategoryAllowsIt()
    {
        (OpsAnnouncer off, RecordingSink offSink, _) = Rig(o => o.WorldMessage = true);
        off.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "safe text", shortTextWithNames: "Bob did it");
        Assert.Contains("safe text", offSink.Sent[0].WorldChat);
        Assert.DoesNotContain("Bob", offSink.Sent[0].WorldChat);

        (OpsAnnouncer on, RecordingSink onSink, _) = Rig(o => { o.WorldMessage = true; o.AllowNamesInWorldMessage = true; });
        on.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "safe text", shortTextWithNames: "Bob did it");
        Assert.Contains("Bob did it", onSink.Sent[0].WorldChat);
    }

    [Fact]
    public void GmOnly_SuppressesTheWorldMessageButNotTheStaffLine()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.WorldMessage = true);

        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Warning, "rejected", gmOnly: true);

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.NotNull(sent.StaffChat);
        Assert.Null(sent.WorldChat);
    }

    [Fact]
    public void BelowTheMinimumSeverity_DisabledAndBothOff_AreLoggedNotDelivered()
    {
        (OpsAnnouncer floor, RecordingSink floorSink, _) = Rig(o => o.MinimumSeverity = AnnounceSeverity.Warning);
        floor.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "quiet");
        floor.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Error, "loud");
        Assert.Contains("loud", Assert.Single(floorSink.Sent).StaffChat);

        (OpsAnnouncer master, RecordingSink masterSink, _) = Rig(enabled: false);
        master.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Critical, "x");
        Assert.Empty(masterSink.Sent);

        (OpsAnnouncer off, RecordingSink offSink, _) = Rig(o => o.GmNotify = false);
        off.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Critical, "x");
        Assert.Empty(offSink.Sent);
    }

    [Fact]
    public void RateLimit_StopsAStorm_ThenReportsHowManyWereSuppressed()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => { o.RateBurst = 3; o.RatePerMinute = 6; });

        for (int i = 0; i < 10; i++)
        {
            announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, $"swap {i}");
        }

        Assert.Equal(3, sink.Sent.Count);

        _now += 10_000; // 6 per minute: one token per 10 s
        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "later");

        Assert.Equal(4, sink.Sent.Count);
        Assert.Contains("+7 suppressed", sink.Sent[3].StaffChat);
    }

    [Fact]
    public void DuplicateWindow_SuppressesTheSameTextUntilItExpires()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.DedupSeconds = 30);

        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "same");
        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "same");
        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "other");
        Assert.Equal(2, sink.Sent.Count);

        _now += 31_000;
        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "same");
        Assert.Equal(3, sink.Sent.Count);
    }

    [Fact]
    public void TypedText_CannotInjectClientEscapesOrNewlines()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.WorldMessage = true);

        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "a|cffff0000red|r\nsecond line");

        string world = Assert.Single(sink.Sent).WorldChat!;
        Assert.DoesNotContain("|cffff0000red", world);
        Assert.DoesNotContain('\n', world);
        Assert.Equal(1, world.Split("|c").Length - 1); // only our own prefix colour
    }

    [Fact]
    public void ASinkThatThrows_NeverReachesTheCaller()
    {
        var announcer = new OpsAnnouncer(new AnnounceOptions(), new ThrowingSink(), NullLogger.Instance);

        announcer.Announce(AnnounceCategory.HotSwap, AnnounceSeverity.Info, "x");
    }

    private sealed class ThrowingSink : IAnnounceSink
    {
        public void Deliver(AnnounceDelivery delivery) => throw new InvalidOperationException("boom");
    }
}

/// <summary>What each lane's report turns into, the lane filter, and the metric.</summary>
public sealed class HotSwapReporterTests
{
    private static (HotSwapReporter Reporter, RecordingSink Sink) Rig(Action<HotSwapAnnounceOptions>? configure = null)
    {
        var options = new HotSwapAnnounceOptions { WorldMessage = true };
        configure?.Invoke(options);
        var sink = new RecordingSink();
        var announcer = new OpsAnnouncer(new AnnounceOptions { HotSwap = options }, sink, NullLogger.Instance);
        return (new HotSwapReporter(announcer, options), sink);
    }

    private static readonly HotSwapOrigin Gm = new("acct_alice (Alice)", "fix drop rates");

    [Fact]
    public void DataReload_StaffSeeTableActorReasonDurationAndCounts_PlayersSeeTableAndReason()
    {
        (HotSwapReporter reporter, RecordingSink sink) = Rig();

        reporter.Report(new HotSwapEvent(HotSwapLane.Data, "reload", "creature_template", HotSwapResult.Applied, Gm, TimeSpan.FromMilliseconds(250), "reloaded: 12345 creature templates"));

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("creature_template", sent.StaffChat);
        Assert.Contains("acct_alice (Alice)", sent.StaffChat);
        Assert.Contains("fix drop rates", sent.StaffChat);
        Assert.Contains("250 ms", sent.StaffChat);
        Assert.Contains("12345 creature templates", sent.StaffChat);
        Assert.Contains("creature_template", sent.WorldChat);
        Assert.Contains("fix drop rates", sent.WorldChat);
        Assert.DoesNotContain("acct_alice", sent.WorldChat);
        Assert.DoesNotContain("Alice", sent.WorldChat);
        Assert.DoesNotContain("12345", sent.WorldChat);
        Assert.DoesNotContain("250", sent.WorldChat);
    }

    [Fact]
    public void ModuleLoadReplaceUnload_NameTheModuleAndTheAction_AndKeepHashesStaffOnly()
    {
        (HotSwapReporter reporter, RecordingSink sink) = Rig();
        AnnounceField[] fields = [new("sha256", "0123456789ab"), new("opcodes", "2")];

        foreach (string action in new[] { "load", "replace", "unload" })
        {
            reporter.Report(new HotSwapEvent(HotSwapLane.Module, action, "GmCommands", HotSwapResult.Applied, Gm, TimeSpan.FromMilliseconds(40), string.Empty, fields));
        }

        Assert.Equal(3, sink.Sent.Count);
        Assert.Contains("'GmCommands' load applied", sink.Sent[0].StaffChat);
        Assert.Contains("'GmCommands' replace applied", sink.Sent[1].StaffChat);
        Assert.Contains("'GmCommands' unload applied", sink.Sent[2].StaffChat);
        Assert.All(sink.Sent, d =>
        {
            Assert.Contains("sha256=0123456789ab", d.StaffChat);
            Assert.DoesNotContain("0123456789ab", d.WorldChat);
            Assert.DoesNotContain("acct_alice", d.WorldChat);
        });
    }

    [Fact]
    public void CodeUpdate_NamesTheAssemblies_AsTheRuntimeActor()
    {
        (HotSwapReporter reporter, RecordingSink sink) = Rig();

        reporter.Report(new HotSwapEvent(HotSwapLane.Code, "metadata-update", "ArcaneCore.Game, ArcaneCore.World", HotSwapResult.Applied, HotSwapOrigin.Runtime, TimeSpan.Zero, "registry refresh Applied"));

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("Server code update (ArcaneCore.Game, ArcaneCore.World) applied", sent.StaffChat);
        Assert.Contains("by dotnet watch", sent.StaffChat);
    }

    [Fact]
    public void ARejection_ReachesStaffAtWarning_AndPlayersOnlyWhenAsked()
    {
        (HotSwapReporter quiet, RecordingSink quietSink) = Rig();
        (HotSwapReporter loud, RecordingSink loudSink) = Rig(o => o.WorldMessageOnFailure = true);
        var rejected = new HotSwapEvent(HotSwapLane.Module, "replace", "GmCommands", HotSwapResult.Rejected, Gm, TimeSpan.FromMilliseconds(5), "refused by the module allowlist");

        quiet.Report(rejected);
        loud.Report(rejected);

        Assert.Contains("REJECTED", Assert.Single(quietSink.Sent).StaffChat);
        Assert.Contains("refused by the module allowlist", quietSink.Sent[0].StaffChat);
        Assert.Null(quietSink.Sent[0].WorldChat);
        Assert.Contains("REJECTED", Assert.Single(loudSink.Sent).WorldChat);
        Assert.DoesNotContain("allowlist", loudSink.Sent[0].WorldChat);
        Assert.DoesNotContain("fix drop rates", loudSink.Sent[0].WorldChat); // a failure never repeats the reason to players
    }

    [Fact]
    public void ReasonStaysOutOfTheWorldMessage_WhenTheOptionIsOff()
    {
        (HotSwapReporter reporter, RecordingSink sink) = Rig(o => o.WorldMessageIncludeReason = false);

        reporter.Report(new HotSwapEvent(HotSwapLane.Data, "reload", "item_template", HotSwapResult.Applied, Gm, TimeSpan.Zero, string.Empty));

        Assert.DoesNotContain("fix drop rates", sink.Sent[0].WorldChat);
        Assert.Contains("fix drop rates", sink.Sent[0].StaffChat);
    }

    [Fact]
    public void LaneFilter_AnnouncesOnlyTheNamedLanes_ButStillCountsEverything()
    {
        (HotSwapReporter reporter, RecordingSink sink) = Rig(o => o.Lanes = ["module"]);
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "arcanecore.hotswap")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Interlocked.Add(ref counted, value));
        listener.Start();

        reporter.Report(new HotSwapEvent(HotSwapLane.Data, "reload", "item_template", HotSwapResult.Applied, Gm, TimeSpan.Zero, string.Empty));
        reporter.Report(new HotSwapEvent(HotSwapLane.Module, "load", "GmCommands", HotSwapResult.Applied, Gm, TimeSpan.Zero, string.Empty));

        Assert.Single(sink.Sent);
        Assert.Contains("GmCommands", sink.Sent[0].StaffChat);
        Assert.True(Interlocked.Read(ref counted) >= 2, "arcanecore.hotswap{kind,result} counts every report");
    }

    [Fact]
    public void AnUnchangedReload_IsStaffOnly()
    {
        (HotSwapReporter reporter, RecordingSink sink) = Rig();

        reporter.Report(new HotSwapEvent(HotSwapLane.Data, "reload", "spell_template", HotSwapResult.Unchanged, Gm, TimeSpan.Zero, "kept"));

        Assert.NotNull(Assert.Single(sink.Sent).StaffChat);
        Assert.Null(sink.Sent[0].WorldChat);
    }
}

/// <summary>The free-text reason after the name, and the configuration of the announcements.</summary>
public sealed class HotSwapReasonAndConfigTests
{
    [Theory]
    [InlineData("creature_template", "creature_template", "")]
    [InlineData("  creature_template  ", "creature_template", "")]
    [InlineData("creature_template fix drops", "creature_template", "fix drops")]
    [InlineData("GmCommands   spaced   reason ", "GmCommands", "spaced   reason")]
    [InlineData("name a|cffff0000b", "name", "a/cffff0000b")]
    [InlineData("", "", "")]
    public void SplitReason_KeepsTheOldOneWordSyntax_AndTakesTheRestAsTheReason(string args, string word, string reason)
    {
        Assert.Equal((word, reason), AnnounceTexts.SplitReason(args));
    }

    [Fact]
    public void ALongReason_IsCut()
    {
        (_, string reason) = AnnounceTexts.SplitReason("x " + new string('r', 500));

        Assert.Equal(AnnounceTexts.MaxReason, reason.Length);
    }

    [Fact]
    public void TheSectionBindsFromConfiguration_AndTheCheckAcceptsIt()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Announce:HotSwap:WorldMessage"] = "true",
            ["World:Announce:HotSwap:MinimumSeverity"] = "Warning",
            ["World:Announce:HotSwap:Lanes:0"] = "Module",
            ["World:Announce:HotSwap:RateBurst"] = "4",
        }).Build();

        var options = configuration.GetSection(AnnounceOptions.SectionName).Get<AnnounceOptions>()!;

        Assert.True(options.HotSwap.WorldMessage);
        Assert.Equal(AnnounceSeverity.Warning, options.HotSwap.MinimumSeverity);
        Assert.Equal(["Module"], options.HotSwap.Lanes);
        Assert.Equal(4, options.HotSwap.RateBurst);
        Assert.Empty(new AnnounceConfigChecks().Check(configuration));
    }

    [Fact]
    public void TheCheck_ReportsEveryBadKey()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Announce:HotSwap:GmNotify"] = "maybe",
            ["World:Announce:HotSwap:MinimumSeverity"] = "Loud",
            ["World:Announce:HotSwap:RateBurst"] = "-1",
            ["World:Announce:HotSwap:DedupSeconds"] = "soon",
            ["World:Announce:HotSwap:Lanes:0"] = "Everything",
        }).Build();

        string[] keys = [.. new AnnounceConfigChecks().Check(configuration).Select(i => i.Key)];

        Assert.Contains("World:Announce:HotSwap:GmNotify", keys);
        Assert.Contains("World:Announce:HotSwap:MinimumSeverity", keys);
        Assert.Contains("World:Announce:HotSwap:RateBurst", keys);
        Assert.Contains("World:Announce:HotSwap:DedupSeconds", keys);
        Assert.Contains("World:Announce:HotSwap:Lanes:0", keys);
    }
}
