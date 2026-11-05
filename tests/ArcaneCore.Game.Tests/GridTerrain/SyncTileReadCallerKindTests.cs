using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The caller kind a synchronous tile read is filed under (<see cref="SyncTileReadCounters.CallerKind"/>) in a real <see cref="WorldRuntime"/>:
/// the serial maps loop, a map worker in the parallel phase with and without the serial gate, and the world thread running a map itself
/// while it holds the gate. Reads go through the production <c>Record(file, ...)</c> overload, which classifies the calling thread.
/// </summary>
public sealed class SyncTileReadCallerKindTests
{
    private static WorldRuntime CreateRuntime(int threads)
        => new(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Parallel = new ParallelMapOptions { MapUpdateThreads = threads },
            },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    /// <summary>Notes the caller kind, makes a read and notes the totals this thread gained, once per update; optionally inside the serial gate.</summary>
    private abstract class ReadingUpdater(WorldRuntime world, bool gated) : IMapUpdater
    {
        public readonly List<(string? Thread, SyncReadCaller Kind, SyncReadCaller KindAfterGate, long GateReadsGained, long ReadsGained)> Seen = [];

        public int WorkMs { get; init; }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }

        public void Update(Map map, uint diffMs)
        {
            if (WorkMs > 0)
            {
                Thread.Sleep(WorkMs);
            }

            SyncFileReadTotals before = SyncTileReadCounters.Current;
            SyncReadCaller kind;
            SyncReadCaller after;
            if (gated)
            {
                using (world.EnterSerial())
                {
                    kind = SyncTileReadCounters.CallerKind();
                    SyncTileReadCounters.Record(SyncReadFile.Terrain, 10, map.MapId, 1, 1);
                }

                after = SyncTileReadCounters.CallerKind();
            }
            else
            {
                kind = SyncTileReadCounters.CallerKind();
                SyncTileReadCounters.Record(SyncReadFile.Terrain, 10, map.MapId, 1, 1);
                after = kind;
            }

            SyncFileReadTotals gained = SyncTileReadCounters.Current - before;
            Seen.Add((Thread.CurrentThread.Name, kind, after, gained.ReadsHoldingGate, gained.Reads));
        }
    }

    [MapAccess(MapAccess.MapLocal)]
    private sealed class UngatedReader(WorldRuntime world) : ReadingUpdater(world, gated: false);

    [MapAccess(MapAccess.MapLocal)]
    private sealed class GatedReader(WorldRuntime world) : ReadingUpdater(world, gated: true);

    private static bool IsWorker(string? name) => name is not null && name.StartsWith("map-worker", StringComparison.Ordinal);

    private static async Task RunStartedWorld(WorldRuntime world)
    {
        world.UseManualClockFrom(0);
        world.Start();
        await world.AdvanceClockAsync(50 * 40);
        world.Stop();
    }

    [Fact]
    public void ARead_OutsideAnyMapUpdate_IsOfTheOutsideKind_AndNotCountedAsHoldingTheGate()
    {
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        SyncTileReadCounters.Record(SyncReadFile.Terrain, 10, 0, 1, 1);

        Assert.Equal(SyncReadCaller.OutsideMapUpdate, SyncTileReadCounters.CallerKind());
        SyncFileReadTotals gained = SyncTileReadCounters.Current - before;
        Assert.Equal(1, gained.Reads);
        Assert.Equal(0, gained.ReadsHoldingGate);
    }

    [Fact]
    public void TheSerialLoop_FilesAReadInsideAMapUpdateUnderTheWorldThread_AndNothingOutsideIt()
    {
        using WorldRuntime world = CreateRuntime(1);
        var reader = new UngatedReader(world);
        world.GetMap(0).AddUpdater(reader);

        world.RunTick(50);

        var seen = Assert.Single(reader.Seen);
        Assert.Equal(SyncReadCaller.WorldThread, seen.Kind);
        Assert.Equal(0, seen.GateReadsGained);
        Assert.Equal(1, seen.ReadsGained);
        Assert.Equal(SyncReadCaller.OutsideMapUpdate, SyncTileReadCounters.CallerKind()); // the scope was left
    }

    [Fact]
    public async Task AMapWorker_WithoutTheGate_IsAMapWorker_AndTheWorldThreadWithoutIt_IsTheWorldThread()
    {
        using WorldRuntime world = CreateRuntime(3);
        var readers = new[] { new UngatedReader(world) { WorkMs = 2 }, new UngatedReader(world) { WorkMs = 2 }, new UngatedReader(world) { WorkMs = 2 } };
        world.GetMap(0).AddUpdater(readers[0]);
        world.GetMap(1).AddUpdater(readers[1]);
        world.GetMap(30, 4).AddUpdater(readers[2]);

        await RunStartedWorld(world);

        var all = readers.SelectMany(r => r.Seen).ToList();
        Assert.Contains(all, s => IsWorker(s.Thread));
        Assert.All(all, s =>
        {
            Assert.Equal(IsWorker(s.Thread) ? SyncReadCaller.MapWorker : SyncReadCaller.WorldThread, s.Kind);
            Assert.Equal(0, s.GateReadsGained);
            Assert.Equal(1, s.ReadsGained);
        });
    }

    [Fact]
    public async Task AnyThreadHoldingTheGateInAMapUpdate_IsFiledAsHoldingIt_AndLeavingTheGateEndsIt()
    {
        using WorldRuntime world = CreateRuntime(3);
        var readers = new[] { new GatedReader(world) { WorkMs = 2 }, new GatedReader(world) { WorkMs = 2 }, new GatedReader(world) { WorkMs = 2 } };
        world.GetMap(0).AddUpdater(readers[0]);
        world.GetMap(1).AddUpdater(readers[1]);
        world.GetMap(30, 4).AddUpdater(readers[2]);

        await RunStartedWorld(world);

        var all = readers.SelectMany(r => r.Seen).ToList();
        Assert.Contains(all, s => IsWorker(s.Thread));
        Assert.All(all, s =>
        {
            Assert.Equal(SyncReadCaller.MapWorkerHoldingGate, s.Kind); // worker threads and the world thread alike
            Assert.Equal(1, s.GateReadsGained);
            Assert.Equal(1, s.ReadsGained);
            Assert.Equal(IsWorker(s.Thread) ? SyncReadCaller.MapWorker : SyncReadCaller.WorldThread, s.KindAfterGate);
        });
    }
}
