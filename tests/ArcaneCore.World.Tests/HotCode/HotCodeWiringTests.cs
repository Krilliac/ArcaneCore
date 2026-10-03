using ArcaneCore.Game.Maps;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>How the dev runner's pieces are wired: the fault breaker is part of it and nothing else sets it.</summary>
public sealed class HotCodeWiringTests
{
    private static int BreakerLimit(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using ServiceProvider services = new ServiceCollection().AddWorldDaemon(configuration).BuildServiceProvider();
        return services.GetRequiredService<IOptions<WorldRuntimeOptions>>().Value.MaxConsecutiveUpdaterFaults;
    }

    [Fact]
    public void TheFaultBreaker_IsOffUnlessHotCodeIsEnabled()
    {
        Assert.Equal(0, BreakerLimit([]));
        Assert.Equal(0, BreakerLimit(new() { ["World:HotCode:MaxConsecutiveFaults"] = "7" })); // ignored while disabled
        Assert.Equal(50, BreakerLimit(new() { ["World:HotCode:Enabled"] = "true" }));
        Assert.Equal(7, BreakerLimit(new() { ["World:HotCode:Enabled"] = "true", ["World:HotCode:MaxConsecutiveFaults"] = "7" }));
        Assert.Equal(0, BreakerLimit(new() { ["World:HotCode:Enabled"] = "true", ["World:HotCode:MaxConsecutiveFaults"] = "0" }));
    }

    [Fact]
    public void AnExplicitWorldValue_BeatsTheHotCodeDefault()
    {
        Assert.Equal(9, BreakerLimit(new() { ["World:HotCode:Enabled"] = "true", ["World:MaxConsecutiveUpdaterFaults"] = "9" }));
    }

    [Fact]
    public async Task AnAppliedEdit_RunsTheCodeEditedCallback_OnTheWorldThread_BeforeAnythingElse()
    {
        using var world = new ThreadedWorld();
        var threads = new List<int>();
        var refresh = new HotCodeRefresh(
            new HotCodeState(), world, new OpcodeTable(), new CommandTableSource(new CommandTable([])),
            new TestCatalog(), new HotCodeAudit(null), NullLogger.Instance,
            onCodeEdited: () => threads.Add(Environment.CurrentManagedThreadId));

        refresh.OnMetadataUpdate(null);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (refresh.State.Snapshot().AppliedGeneration == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the refresh never finished");
            await Task.Delay(10);
        }

        Assert.Equal([world.ThreadId], threads);
    }
}
