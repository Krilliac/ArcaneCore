using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// <c>bot-population/band-deficit</c> and <c>bot-population/login-failed</c> (Hardening Wave H1): each fires once on the failure it names and
/// stays silent on the healthy path. The ledger is process-wide and the population runs on its own thread, so every test uses a level band of
/// its own (the subject of band-deficit) or the registry id of its own bot (login-failed) and reads only reports with that subject.
/// </summary>
public sealed class PlayerbotPopulationAnomalyTests
{
    private const string BandDeficit = "bot-population/band-deficit";
    private const string LoginFailed = "bot-population/login-failed";

    private static AnomalyCapture CaptureBand(PopulationTestHost world, int minLevel, int maxLevel)
        => PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == BandDeficit && n.Subject == $"band {minLevel}-{maxLevel}");

    /// <summary>One pass with a fresh tick window, then the feature's clock moves on by <paramref name="advanceMs"/>.</summary>
    private static async Task PassAsync(PopulationTestHost world, long advanceMs)
    {
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        world.Feature.ClockMs += advanceMs;
    }

    private static Task<PopulationTestHost> StartWantingOneBotAsync(int minLevel, int maxLevel)
        => PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 1;
            o.Population.IntervalSeconds = 10; // the report waits for max(60 s, 3 intervals)
            o.Population.Bands = [new PopulationBand { MinLevel = minLevel, MaxLevel = maxLevel, Weight = 1 }];
        });

    // --- band-deficit ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BandDeficit_Fires_OnceWhenABandStaysBelowItsTargetForThreePassesAndAMinute()
    {
        await using PopulationTestHost world = await StartWantingOneBotAsync(2, 9);
        world.Feature.Provisioner.AreaLevelSource = _ => 60; // no zone fits level 2-9: the band cannot be filled
        using AnomalyCapture capture = CaptureBand(world, 2, 9);

        await PassAsync(world, 31_000);
        await PassAsync(world, 31_000);
        Assert.Empty(capture.Notices); // two passes, 31 s: neither bound reached

        await PassAsync(world, 31_000);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal(AnomalySeverity.Info, notice.Severity);
        Assert.Contains("below target for", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("want=1, have=0", notice.Detail, StringComparison.Ordinal);

        // The same episode reports once, however long it lasts.
        await PassAsync(world, 31_000);
        await PassAsync(world, 31_000);
        Assert.Single(capture.Notices);
    }

    [Fact]
    public async Task BandDeficit_StaysSilent_WhenTheDeficitIsShorterThanThreePasses_OrThanTheTimeLimit()
    {
        await using PopulationTestHost world = await StartWantingOneBotAsync(3, 8);
        world.Feature.Provisioner.AreaLevelSource = _ => 60;
        using AnomalyCapture capture = CaptureBand(world, 3, 8);

        // Three passes, but all inside a minute: the time bound is not met.
        await PassAsync(world, 5_000);
        await PassAsync(world, 5_000);
        await PassAsync(world, 5_000);
        await PassAsync(world, 5_000);
        Assert.Empty(capture.Notices);

        // Only two passes a minute apart on a fresh deficit: the pass bound is not met.
        await using PopulationTestHost second = await StartWantingOneBotAsync(3, 8);
        second.Feature.Provisioner.AreaLevelSource = _ => 60;
        await PassAsync(second, 120_000);
        await PassAsync(second, 120_000);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public async Task BandDeficit_StaysSilent_WhenTheBandIsFilled()
    {
        await using PopulationTestHost world = await StartWantingOneBotAsync(1, 9);
        using AnomalyCapture capture = CaptureBand(world, 1, 9);

        for (int i = 0; i < 5; i++) await PassAsync(world, 61_000);

        Assert.Equal(1, world.Feature.Status.Bands.Sum(b => b.Have));
        Assert.Empty(capture.Notices);
    }

    // --- login-failed ----------------------------------------------------------------------------------------------------------------

    /// <summary>A stopped, real bot the population owns, whose account the server will refuse to log in.</summary>
    private static async Task<Guid> OwnedBotAsync(PopulationTestHost world, string name, bool refusable)
    {
        PlayerbotOperationResult made = await world.Bots.CreateAsync(name, 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        ManagedPlayerbot record = Assert.IsType<ManagedPlayerbot>(world.Owners.Peek(id));
        world.Members.Seed(new PlayerbotPopulationMember(id, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        if (refusable)
        {
            Assert.True(await world.Host.Accounts.SetStatusAsync(record.AccountName, AccountStatus.Banned));
        }

        return id;
    }

    [Fact]
    public async Task LoginFailed_Fires_WhenTheRestartOfAParkedBotIsRefused()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        Guid id = await OwnedBotAsync(world, "Refusedone", refusable: true);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == LoginFailed && n.Subject.Contains(id.ToString("D"), StringComparison.Ordinal));

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains("population restart failed to bring bot online: code=owner-refused", notice.Detail, StringComparison.Ordinal);
        Assert.Equal(0, world.RunningCount);
        Assert.NotNull(status);
    }

    [Fact]
    public async Task LoginFailed_StaysSilent_WhenTheParkedBotIsRestarted()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        Guid id = await OwnedBotAsync(world, "Startedfine", refusable: false);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == LoginFailed && n.Subject.Contains(id.ToString("D"), StringComparison.Ordinal));

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("started 1", status.Reason);
        await world.WaitForSettledAsync(1);
        Assert.Empty(capture.Notices);
    }
}
