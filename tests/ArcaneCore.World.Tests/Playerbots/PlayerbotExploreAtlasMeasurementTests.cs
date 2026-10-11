using System.Diagnostics;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.World.Playerbots;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Opt-in, read-only measurement against an imported world DB; never starts a world server.</summary>
public sealed class PlayerbotExploreAtlasMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureAtlasBuildFromRealWorldDb()
    {
        string? path = Environment.GetEnvironmentVariable("ARCANECORE_EXPLORE_MEASURE_WORLD_DB");
        if (string.IsNullOrWhiteSpace(path)) return;
        string uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro";
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={uri};Mode=ReadOnly;Pooling=False").Options;
        await using var db = new WorldDbContext(options);
        var content = await new EfCreatureDataStore(db).LoadAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var atlas = new PlayerbotExploreAtlas(content);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"spawns={content.SpawnCount} cells={atlas.CellCount} entries={atlas.EntryCount} threatCells={atlas.ThreatCellCount} buildMs={stopwatch.Elapsed.TotalMilliseconds:F2} allocatedBytes={allocated}");
        Assert.True(atlas.CellCount > 0);
    }
}
