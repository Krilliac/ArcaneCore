using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// Issue #1618: <c>logger.LogFault</c> already records into the fault ledger, so a catch that also calls <c>FaultLedger.Shared.Record</c>
/// with the same site counts each fault twice. Every population catch that logs with <c>LogFault("playerbot.population", ...)</c> must count
/// an injected fault exactly once (see <see cref="FaultCountProbe"/>).
/// </summary>
public sealed class PlayerbotPopulationFaultCountTests
{
    private const string Site = "playerbot.population";

    [Fact]
    public async Task FailingPass_CountsTheFaultOnce()
    {
        var probe = new FaultCountProbe(Site);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        world.Feature.Provisioner.AreaLevelSource = _ => throw probe.Fault();

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Equal("error", status.State);
        probe.AssertCountedOnce("the pass body catch");
    }

    [Fact]
    public async Task AFaultThatEscapesThePass_IsCountedOnce_ByTheBackgroundPassCatch()
    {
        var probe = new FaultCountProbe(Site);
        // The pass's closing information line is the first thing after the body's own catch: a logger that throws there is a fault the body
        // cannot catch, so it reaches the catch around the background task. It only throws on that line (a pass that created a bot writes it).
        var logger = new ThrowOnPassLineLogger(probe.Fault());
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1,
            configureServices: services => services.AddSingleton<ILogger<PlayerbotPopulationFeature>>(logger));

        world.Ticks.Set(200, 10);
        await world.PassAsync();

        Assert.True(logger.Thrown, "the pass never wrote its closing line, so the fault was never injected");
        probe.AssertCountedOnce("the background pass catch");
    }

    [Fact]
    public async Task AFailingGraveyardTerrainLookup_CountsTheFaultOnce()
    {
        var probe = new FaultCountProbe(Site);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1, withRelocation: true);
        world.Feature.TerrainLocator = _ => throw probe.Fault();

        // The lookup ends on the world thread (it posts its outcome after the catch), so a pass that no longer waits for it comes after the fault is counted.
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        PopulationStatus status;
        do
        {
            world.Ticks.Set(200, 10);
            status = await world.PassAsync();
            if (!status.Reason.Contains("waiting for the graveyard terrain lookup", StringComparison.Ordinal)) break;
            await Task.Delay(20);
        }
        while (DateTime.UtcNow < deadline);

        Assert.DoesNotContain("waiting for the graveyard terrain lookup", status.Reason);
        probe.AssertCountedOnce("the graveyard terrain lookup catch");
    }

    [Fact]
    public async Task AFailingCapacitySurvey_CountsTheFaultOnce()
    {
        var probe = new FaultCountProbe(Site);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        world.Feature.CapacitySurveyor = _ => throw probe.Fault();
        world.Feature.CapacitySurveyorVersion = 1;

        await world.PassAsync();
        await world.Feature.CapacityTask;

        probe.AssertCountedOnce("the zone capacity survey catch");
    }

    [Fact]
    public async Task AFailingDeleteOfAnUnplacedBot_CountsTheFaultOnce()
    {
        var probe = new FaultCountProbe(Site);
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Faultdelone", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        world.Members.Seed(new PlayerbotPopulationMember(id, (await world.Owners.LoadAllAsync()).Single().CharacterId, 0,
            PlayerbotPopulationFeature.PendingDeleteBand, now, now));
        world.Members.OnDelete = _ => throw probe.Fault(); // the member row is the last thing the delete removes
        world.Feature.Provisioner.AreaLevelSource = _ => 60; // nothing else can be placed in this pass

        world.Ticks.Set(200, 10);
        await world.PassAsync();

        probe.AssertCountedOnce("the delete of an unplaced bot catch");
    }

    /// <summary>Throws <c>fault</c> from the population's closing information line, once; every other message passes.</summary>
    private sealed class ThrowOnPassLineLogger(Exception fault) : ILogger<PlayerbotPopulationFeature>
    {
        private int _thrown;

        public bool Thrown => Volatile.Read(ref _thrown) != 0;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Information || !formatter(state, exception).Contains("; state ", StringComparison.Ordinal)) return;
            if (Interlocked.Exchange(ref _thrown, 1) == 0) throw fault;
        }
    }
}
