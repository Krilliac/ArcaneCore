using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Ops.Diagnostics;
using ArcaneCore.World.Ops.Lifecycle;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.HotCode;
using ArcaneCore.World.Tests.Playerbots.Qa;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The announcement categories beyond HotSwap (docs/areas/hot-swap-announcements.md): defaults, config switches and the hook points.</summary>
public sealed class OpsAnnounceCategoriesTests
{
    private long _now = 1_000_000;

    private (OpsAnnouncer Announcer, RecordingSink Sink, AnnounceOptions Options) Rig(Action<AnnounceOptions>? configure = null)
    {
        var options = new AnnounceOptions();
        configure?.Invoke(options);
        var sink = new RecordingSink();
        return (new OpsAnnouncer(options, sink, NullLogger.Instance, () => _now), sink, options);
    }

    public static TheoryData<AnnounceCategory> NewCategories => new()
    {
        AnnounceCategory.AntiCheat, AnnounceCategory.Faults, AnnounceCategory.Wiring,
        AnnounceCategory.Shutdown, AnnounceCategory.Saves, AnnounceCategory.BotQa,
    };

    [Fact]
    public void EveryCategory_HasOptions_AndIsValidatedByTheConfigChecks()
    {
        (OpsAnnouncer announcer, _, _) = Rig();
        foreach (AnnounceCategory category in Enum.GetValues<AnnounceCategory>())
        {
            Assert.NotNull(announcer.OptionsFor(category));
            Assert.Contains(category.ToString(), AnnounceConfigChecks.Categories);
        }
    }

    [Theory]
    [MemberData(nameof(NewCategories))]
    public void Defaults_StaffOnly_NoWorldMessage_NoNames(AnnounceCategory category)
    {
        AnnounceCategoryOptions options = Rig().Announcer.OptionsFor(category);
        Assert.True(options.GmNotify);
        Assert.False(options.WorldMessage);
        Assert.False(options.AllowNamesInWorldMessage);
        Assert.True(options.IncludeDetails);
    }

    [Theory]
    [MemberData(nameof(NewCategories))]
    public void Fires_ToStaff_WithTheCategoryTag_AndNoWorldLine(AnnounceCategory category)
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig();
        announcer.Announce(category, AnnounceSeverity.Critical, "something happened", "who: Alice");

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains($"[{category}]", sent.StaffChat);
        Assert.Contains("Alice", sent.StaffChat);
        Assert.Null(sent.WorldChat);
    }

    [Theory]
    [MemberData(nameof(NewCategories))]
    public void ConfigOff_And_SeverityFloor_AreHonoured(AnnounceCategory category)
    {
        (OpsAnnouncer off, RecordingSink offSink, _) = Rig(o =>
        {
            AnnounceCategoryOptions c = new OpsAnnouncer(o, new RecordingSink(), NullLogger.Instance).OptionsFor(category);
            c.GmNotify = false;
            c.WorldMessage = false;
        });
        off.Announce(category, AnnounceSeverity.Critical, "x");
        Assert.Empty(offSink.Sent);

        (OpsAnnouncer floor, RecordingSink floorSink, _) = Rig(o =>
            new OpsAnnouncer(o, new RecordingSink(), NullLogger.Instance).OptionsFor(category).MinimumSeverity = AnnounceSeverity.Error);
        floor.Announce(category, AnnounceSeverity.Warning, "below");
        Assert.Empty(floorSink.Sent);
        floor.Announce(category, AnnounceSeverity.Error, "at the floor");
        Assert.Single(floorSink.Sent);
    }

    [Theory]
    [MemberData(nameof(NewCategories))]
    public void Dedup_SuppressesTheSameKey_UntilTheWindowPasses(AnnounceCategory category)
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o =>
        {
            AnnounceCategoryOptions c = new OpsAnnouncer(o, new RecordingSink(), NullLogger.Instance).OptionsFor(category);
            c.DedupSeconds = 60;
            c.MinimumSeverity = AnnounceSeverity.Info;
        });
        announcer.Announce(category, AnnounceSeverity.Error, "first", dedupKey: "k");
        announcer.Announce(category, AnnounceSeverity.Error, "second", dedupKey: "k");
        Assert.Single(sink.Sent);

        announcer.Announce(category, AnnounceSeverity.Error, "other", dedupKey: "other");
        Assert.Equal(2, sink.Sent.Count);

        _now += 61_000;
        announcer.Announce(category, AnnounceSeverity.Error, "again", dedupKey: "k");
        Assert.Equal(3, sink.Sent.Count);
    }

    [Fact]
    public void AntiCheat_WorldLine_HasNoNames_UnlessAllowed()
    {
        (OpsAnnouncer plain, RecordingSink plainSink, _) = Rig(o => o.AntiCheat.WorldMessage = true);
        plain.Announce(AnnounceCategory.AntiCheat, AnnounceSeverity.Error, "Anti-cheat kicked a player", "Mallory", shortTextWithNames: "Anti-cheat kicked Mallory");
        AnnounceDelivery sent = Assert.Single(plainSink.Sent);
        Assert.DoesNotContain("Mallory", sent.WorldChat);
        Assert.Contains("Mallory", sent.StaffChat);

        (OpsAnnouncer named, RecordingSink namedSink, _) = Rig(o =>
        {
            o.AntiCheat.WorldMessage = true;
            o.AntiCheat.AllowNamesInWorldMessage = true;
        });
        named.Announce(AnnounceCategory.AntiCheat, AnnounceSeverity.Error, "Anti-cheat kicked a player", "Mallory", shortTextWithNames: "Anti-cheat kicked Mallory");
        Assert.Contains("Mallory", Assert.Single(namedSink.Sent).WorldChat);
    }

    [Fact]
    public void ConfigChecks_RejectABadValueInANewCategory()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["World:Announce:Saves:MinimumSeverity"] = "Loud", ["World:Announce:BotQa:DedupSeconds"] = "-4" })
            .Build();
        List<ConfigIssue> issues = [.. new AnnounceConfigChecks().Check(config)];
        Assert.Contains(issues, i => i.Key == "World:Announce:Saves:MinimumSeverity");
        Assert.Contains(issues, i => i.Key == "World:Announce:BotQa:DedupSeconds");
    }

    // --- Faults -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Faults_ANewSignature_IsAnnouncedOnce_StaffOnly_EvenWithTheWorldMessageOn()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.Faults.WorldMessage = true);
        var ledger = new FaultLedger();
        var feature = new FaultAnnouncerFeature(new ServiceCollection().AddSingleton<IOpsAnnouncer>(announcer).AddSingleton(ledger).BuildServiceProvider());
        feature.Attach(null!);
        try
        {
            Exception Make() => new InvalidOperationException("save exploded for Alice");
            for (int i = 0; i < 3; i++)
            {
                ledger.Record("worldtick", Throw(Make()));
            }

            AnnounceDelivery sent = Assert.Single(sink.Sent); // the same signature three times: one line (dedup by signature id)
            Assert.Contains("InvalidOperationException", sent.StaffChat);
            Assert.Contains("worldtick", sent.StaffChat);
            Assert.Null(sent.WorldChat);

            ledger.Record("other-site", Throw(new TimeoutException("slow")));
            Assert.Equal(2, sink.Sent.Count);
        }
        finally
        {
            await feature.StopAsync();
        }

        ledger.Record("after-stop", Throw(new FormatException("late")));
        Assert.Equal(2, sink.Sent.Count); // unsubscribed
    }

    [Fact]
    public void Faults_AnUnhandledException_IsCritical_AndAThrowingSubscriberNeverBreaksTheLedger()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.Faults.MinimumSeverity = AnnounceSeverity.Critical);
        var ledger = new FaultLedger();
        ledger.FaultNoticed += _ => throw new InvalidOperationException("bad subscriber");
        var feature = new FaultAnnouncerFeature(new ServiceCollection().AddSingleton<IOpsAnnouncer>(announcer).AddSingleton(ledger).BuildServiceProvider());
        feature.Attach(null!);

        ledger.Record("worldtick", Throw(new InvalidOperationException("caught"))); // Warning: below the Critical floor
        Assert.Empty(sink.Sent);
        ledger.Record("unhandled", Throw(new InvalidOperationException("fatal")));
        Assert.Contains("Unhandled", Assert.Single(sink.Sent).StaffChat);
        Assert.Equal(2, ledger.Total);
    }

    private static Exception Throw(Exception ex)
    {
        try
        {
            throw ex;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    // --- Shutdown -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Shutdown_RequestCancelAndStop_AreAnnounced_WithTheActor_AndTheNativeBroadcastIsUnchanged()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IOpsAnnouncer>(announcer));
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();

        await host.World.InvokeAsync(() =>
        {
            feature.Request(300, ShutdownMask.Restart, ExitCodes2, "Admin");
            return 0;
        });
        AnnounceDelivery request = Assert.Single(sink.Sent);
        Assert.Contains("restart scheduled", request.StaffChat);
        Assert.Contains("by Admin", request.StaffChat);
        Assert.Null(request.WorldChat); // players get the client's own server message, not a second chat line

        await host.World.InvokeAsync(() =>
        {
            feature.Cancel("Admin");
            return 0;
        });
        Assert.Contains("restart was cancelled", sink.Sent[^1].StaffChat);

        await host.World.InvokeAsync(() =>
        {
            feature.Request(0, ShutdownMask.None, 0, "Admin");
            return 0;
        });
        Assert.Contains("shutting down now", sink.Sent[^1].StaffChat);
    }

    private const byte ExitCodes2 = 2;

    [Fact]
    public async Task Shutdown_WorldMessage_WhenSwitchedOn_CarriesNoActor()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.Shutdown.WorldMessage = true);
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IOpsAnnouncer>(announcer));
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        await host.World.InvokeAsync(() =>
        {
            feature.Request(300, ShutdownMask.None, 0, "SecretAdmin");
            return 0;
        });

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("SecretAdmin", sent.StaffChat);
        Assert.DoesNotContain("SecretAdmin", sent.WorldChat);
        Assert.Contains("shutdown scheduled", sent.WorldChat);
    }

    // --- Saves --------------------------------------------------------------------------------------------------

    [Fact]
    public void Saves_ARefusedSnapshotOfAQuarantinedCharacter_IsAnnounced_AndDeduplicated()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance, announcer: announcer);
        queue.QuarantineCharacter(7);

        queue.Enqueue(new CharacterState(7, 0, 12, 0, 0, 0, 0, 1, 0));
        queue.Enqueue(new CharacterState(7, 0, 12, 0, 0, 0, 0, 1, 0));

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("[Saves]", sent.StaffChat);
        Assert.Contains("quarantined", sent.StaffChat);
        Assert.Null(sent.WorldChat);
        Assert.Equal(0, queue.Pending);
    }

    // --- Wiring -------------------------------------------------------------------------------------------------

    private sealed class ThrowingProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("provider is gone");
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    [Fact]
    public void Wiring_AnAuditThatCannotRun_IsAnnouncedToStaffOnly()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig(o => o.Wiring.WorldMessage = true);
        var reporter = new WiringAuditReporter(new WiringAudit(new ThrowingProvider()), new Lifetime(), NullLogger<WiringAuditReporter>.Instance, announcer);

        reporter.Report();

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("[Wiring]", sent.StaffChat);
        Assert.Contains("could not run", sent.StaffChat);
        Assert.Null(sent.WorldChat);
    }

    // --- BotQa --------------------------------------------------------------------------------------------------

    [Fact]
    public void BotQa_ANewFinding_IsAnnouncedOnce_ByFindingId_WithNoBotIdentity()
    {
        (OpsAnnouncer announcer, RecordingSink sink, _) = Rig();
        var aggregator = new PlayerbotQaAggregator(10, 3, new byte[16]);
        aggregator.FindingCreated = view => PlayerbotQaAnnouncements.Announce(announcer, view);

        aggregator.Ingest(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f), NoQaContentFacts.Instance, QaData.T0);
        aggregator.Ingest(QaData.Stall(QaData.Bot2, -5608.8f, -439.4f), NoQaContentFacts.Instance, QaData.T0); // same finding, another bot

        AnnounceDelivery sent = Assert.Single(sink.Sent);
        Assert.Contains("[BotQa]", sent.StaffChat);
        Assert.Contains("Stall", sent.StaffChat);
        Assert.DoesNotContain(QaData.Bot1.ToString(), sent.StaffChat);
        Assert.DoesNotContain(QaData.Bot2.ToString(), sent.StaffChat);
        Assert.Null(sent.WorldChat);

        aggregator.Ingest(QaData.Stall(QaData.Bot1, 100f, 100f, quest: 99), NoQaContentFacts.Instance, QaData.T0);
        Assert.Equal(2, sink.Sent.Count);
    }
}
