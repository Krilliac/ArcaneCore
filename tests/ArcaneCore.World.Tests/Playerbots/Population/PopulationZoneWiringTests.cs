using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The feature wiring of the zone veto and the zone capacity survey, through the real feature in a real world: a bot that escaped a zone
/// is not relocated into it, and the background capacity survey is superseded, cancelled and discarded as its sources change.
/// </summary>
public sealed class PopulationZoneWiringTests
{
    private static async Task<Guid> StartOneBotInElwynnAsync(PopulationTestHost world)
    {
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("created 1", status.Reason);
        await world.WaitForSettledAsync(1);
        PlayerbotPopulationMember member = Assert.Single(world.Members.Snapshot);
        Assert.Equal(PopulationTestHost.Elwynn, member.ZoneId);
        return member.BotId;
    }

    private static Task SetBotAsync(PopulationTestHost world, Guid bot, byte level, uint zone) => world.Host.World.InvokeAsync(() =>
    {
        Player player = world.Bots.FindSession(bot)!.Player!;
        player.Level = level;
        player.ZoneId = zone;
        return true;
    });

    private static PopulationSpawnCapacity Capacity(int spawns)
        => new(new Dictionary<(uint ZoneId, Team Team), int[]> { [(PopulationTestHost.Elwynn, Team.Alliance)] = [.. Enumerable.Repeat(8, spawns)] });

    [Fact]
    public async Task ABotThatEscapedAZone_IsNotRelocatedBackIntoIt()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        await SetBotAsync(world, bot, 20, PopulationTestHost.Elwynn); // out-levelled in Elwynn; Westfall (12-20) is the only zone that fits
        world.Population.ExcludedZones = [];

        // The brain reports that it escaped Westfall (a dead-end pin); the next capture records it, and the relocation pass leaves the bot alone.
        await world.Host.OnWorldAsync(() => world.Bots.FindBrain(bot)!.RememberEscapedZone(PopulationTestHost.Westfall));
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task WithoutARecordedEscape_TheSameBotIsRelocatedToWestfall()
    {
        // The control of the test above: only the recorded escape keeps the bot out of Westfall.
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        await SetBotAsync(world, bot, 20, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Westfall, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    // Issue #1454: bots placed in Deadwind Pass before the operator excluded it stalled there for good. The zone left the atlas, so
    // the relocation pass skipped them like bots in a capital; a stalled bot that escaped to its bind point in the same area came back.
    // The bot keeps thinking (every 100 ms) between the report and the pass. The report has to outlive those thinks, as a stall does until
    // the bot makes progress: before, the first look after ReportExternal cleared it, and the pass saw a stall only when it beat that think.
    private static async Task StallAsync(PopulationTestHost world, Guid bot)
    {
        long thinks = await world.Host.OnWorldAsync(() =>
        {
            PlayerbotBrain brain = world.Bots.FindBrain(bot)!;
            brain.StallWatch.ReportExternal("test: stalled");
            return brain.ThinkCount;
        });
        await world.Host.WaitForWorldAsync(() => world.Bots.FindBrain(bot)!.ThinkCount >= thinks + 3, "three thinks after the stall report");
        Assert.NotNull(await world.Host.OnWorldAsync(() => world.Bots.FindBrain(bot)!.StallReport));
    }

    private static void SeedZone(PopulationTestHost world, uint zone)
        => world.Members.Seed(Assert.Single(world.Members.Snapshot) with { ZoneId = zone });

    [Fact]
    public async Task AStalledBotInAnExcludedZone_IsRelocatedToAZoneThatFitsIt()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        // Westfall stays excluded (the host's default): a level 8 bot stands in it, as one placed before the exclusion would.
        await SetBotAsync(world, bot, 8, PopulationTestHost.Westfall);
        SeedZone(world, PopulationTestHost.Westfall);
        await StallAsync(world, bot);
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task ABotInAnExcludedZone_IsRelocated_EvenWhenNotStalled()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        await SetBotAsync(world, bot, 8, PopulationTestHost.Westfall);
        SeedZone(world, PopulationTestHost.Westfall);
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task AStalledBotInAZoneItEscaped_IsRelocated_ThoughItFitsTheZone()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        // Level 12 fits Elwynn (10 plus the margin of 2) and Westfall (12-20).
        await SetBotAsync(world, bot, 12, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        await world.Host.OnWorldAsync(() => world.Bots.FindBrain(bot)!.RememberEscapedZone(PopulationTestHost.Elwynn));
        await StallAsync(world, bot);
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Westfall, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task AFittingBotInAZoneItEscaped_StaysWhileItIsNotStalled()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        await SetBotAsync(world, bot, 12, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        await world.Host.OnWorldAsync(() => world.Bots.FindBrain(bot)!.RememberEscapedZone(PopulationTestHost.Elwynn));
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Elwynn, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task AStalledBotInAZoneOverItsCapacity_IsRelocated()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        await SetBotAsync(world, bot, 12, PopulationTestHost.Elwynn);
        world.Population.ExcludedZones = [];
        // Elwynn feeds no level 12 bot (cap 0: the bot itself is over it); Westfall has 30 fitting spawns (cap 10 at 3 spawns a bot).
        var capacity = new PopulationSpawnCapacity(new Dictionary<(uint ZoneId, Team Team), int[]>
        {
            [(PopulationTestHost.Westfall, Team.Alliance)] = [.. Enumerable.Repeat(14, 30)],
        });
        world.Feature.CapacitySurveyor = _ => capacity;
        world.Feature.CapacitySurveyorVersion = 1;
        world.Ticks.Set(200, 10);
        PopulationStatus first = await world.PassAsync();
        Assert.DoesNotContain("moved", first.Reason);
        await world.Host.WaitForWorldAsync(() => ReferenceEquals(world.Feature.Atlas.Capacity, capacity), "capacity attached");

        // Not stalled, it stays: a fitting bot is not evicted from a full zone.
        world.Ticks.Set(200, 10);
        Assert.DoesNotContain("moved", (await world.PassAsync()).Reason);

        await StallAsync(world, bot);
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("moved 1", status.Reason);
        Assert.Equal(PopulationTestHost.Westfall, Assert.Single(world.Members.Snapshot).ZoneId);
    }

    [Fact]
    public async Task AStalledBotInACapital_IsStillLeftAlone()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        Guid bot = await StartOneBotInElwynnAsync(world);
        await SetBotAsync(world, bot, 20, PopulationTestHost.Stormwind);
        world.Population.ExcludedZones = [];
        await StallAsync(world, bot);
        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.DoesNotContain("moved", status.Reason);
        Assert.Equal(PopulationTestHost.Stormwind, await world.Host.World.InvokeAsync(() => world.Bots.FindSession(bot)!.Player!.ZoneId));
    }

    [Fact]
    public async Task TheCapacitySurvey_IsSuperseded_KeepsTheOldCapacityMeanwhile_AndALateResultIsIgnored()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        var releaseSlow = new ManualResetEventSlim();
        int calls = 0;
        PopulationSpawnCapacity first = Capacity(9), second = Capacity(12), slow = Capacity(30);
        world.Feature.CapacitySurveyor = ct => Interlocked.Increment(ref calls) == 1 ? first : second;
        world.Feature.CapacitySurveyorVersion = 1;
        await world.PassAsync();
        await world.Host.WaitForWorldAsync(() => ReferenceEquals(world.Feature.Atlas.Capacity, first), "first capacity attached");

        // A new survey starts (sources changed) and is slow: the old capacity stays in force while it runs.
        world.Feature.CapacitySurveyor = _ => { releaseSlow.Wait(TimeSpan.FromSeconds(30)); return slow; };
        world.Feature.CapacitySurveyorVersion = 2;
        await world.PassAsync();
        Task slowTask = world.Feature.CapacityTask;
        Assert.False(slowTask.IsCompleted);
        Assert.Same(first, world.Feature.Atlas.Capacity);

        // Superseded by a faster one: it wins, and the slow one's late result is dropped.
        world.Feature.CapacitySurveyor = _ => second;
        world.Feature.CapacitySurveyorVersion = 3;
        await world.PassAsync();
        await world.Host.WaitForWorldAsync(() => ReferenceEquals(world.Feature.Atlas.Capacity, second), "second capacity attached");
        releaseSlow.Set();
        await slowTask;
        await world.Host.OnWorldAsync(() => { });
        Assert.Same(second, world.Feature.Atlas.Capacity);
    }

    [Fact]
    public async Task ACancelledCapacitySurvey_DeliversNothing_AndRestartsOnTheNextPass()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        int calls = 0;
        PopulationSpawnCapacity result = Capacity(9);
        var started = new ManualResetEventSlim();
        world.Feature.CapacitySurveyor = ct =>
        {
            if (Interlocked.Increment(ref calls) > 1) return result;
            started.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();
            return result;
        };
        await world.PassAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        Task cancelled = world.Feature.CapacityTask;

        await world.Host.World.InvokeAsync(() => { _ = world.Feature.CancelBackgroundWork(); return true; });
        await cancelled;
        Assert.Null(world.Feature.Atlas.Capacity);

        await world.PassAsync(); // the key was reset by the cancel: the survey starts again
        await world.Host.WaitForWorldAsync(() => ReferenceEquals(world.Feature.Atlas.Capacity, result), "capacity attached after the restart");
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ASurveyThatCountedNothing_IsDiscarded_AndNeverCapsEveryZoneAtZero()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        world.Feature.CapacitySurveyor = _ => new PopulationSpawnCapacity(new Dictionary<(uint ZoneId, Team Team), int[]>());
        world.Feature.CapacitySurveyorVersion = 1;
        await world.PassAsync();
        await world.Feature.CapacityTask;
        await world.Host.OnWorldAsync(() => { });
        Assert.Null(world.Feature.Atlas.Capacity);

        PopulationSpawnCapacity real = Capacity(9);
        world.Feature.CapacitySurveyor = _ => real;
        world.Feature.CapacitySurveyorVersion = 2;
        await world.PassAsync();
        await world.Host.WaitForWorldAsync(() => ReferenceEquals(world.Feature.Atlas.Capacity, real), "real capacity attached");

        // An empty result later does not remove it either.
        world.Feature.CapacitySurveyor = _ => new PopulationSpawnCapacity(new Dictionary<(uint ZoneId, Team Team), int[]>());
        world.Feature.CapacitySurveyorVersion = 3;
        await world.PassAsync();
        await world.Feature.CapacityTask;
        await world.Host.OnWorldAsync(() => { });
        Assert.Same(real, world.Feature.Atlas.Capacity);
    }
}
