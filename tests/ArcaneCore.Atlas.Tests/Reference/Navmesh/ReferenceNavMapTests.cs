using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Navmesh;
using DotRecast.Detour;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Navmesh;

/// <summary>An <see cref="ILogger"/> that remembers what was logged, so a test can assert that a refusal was not an error.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

public sealed class ReferenceNavMapTests : IDisposable
{
    private readonly SyntheticMap _data = new();
    private readonly CapturingLogger _log = new();

    public void Dispose() => _data.Dispose();

    private static TileKey Key(int x, int y) => new(0, x, y);

    private BuiltTile Add(int x, int y, int cells = 4, Func<int, int, bool>? present = null, int? maxLinks = null, int? detourX = null, int? detourY = null)
        => _data.Add(new TileSpec
        {
            TerrainX = x,
            TerrainY = y,
            Cells = cells,
            Present = present ?? ((_, _) => true),
            MaxLinkCount = maxLinks,
            DetourX = detourX,
            DetourY = detourY,
        });

    private ReferenceNavMeshService Service(int maxResident = ReferenceNavMeshService.DefaultMaxResidentTiles) => new(_data.Mmaps, _log, maxResident);

    private static ReferenceNavMap Map(ReferenceNavMeshService service)
    {
        NavMapOpen open = service.OpenMap(0);
        Assert.Equal(NavTileState.Ok, open.State);
        return Assert.IsType<ReferenceNavMap>(open.Map);
    }

    [Fact]
    public void OpenMap_WithoutAnMmapsDirectory_IsMissing()
    {
        using var service = new ReferenceNavMeshService(null, _log);

        NavMapOpen open = service.OpenMap(0);

        Assert.Null(open.Map);
        Assert.Equal(NavTileState.Missing, open.State);
        Assert.Contains("mmaps", open.Detail, StringComparison.Ordinal);
        Assert.Empty(service.MapIds());
        Assert.Empty(service.AvailableTiles(0));
    }

    [Fact]
    public void OpenMap_WithoutAParamsFile_IsMissing_AndIsFoundOnceTheFileAppears()
    {
        using var empty = new SyntheticMap(writeParams: false);
        using var service = new ReferenceNavMeshService(empty.Mmaps, _log);

        Assert.Equal(NavTileState.Missing, service.OpenMap(0).State);

        File.WriteAllBytes(Path.Combine(empty.Mmaps, "000.mmap"), SyntheticNav.BuildParams(8));

        Assert.Equal(NavTileState.Ok, service.OpenMap(0).State);
        Assert.Equal(new uint[] { 0 }, service.MapIds());
    }

    [Fact]
    public void OpenMap_WithUnusableParameters_IsRejected_WithTheReason_AtWarning()
    {
        using var broken = new SyntheticMap(writeParams: false);
        File.WriteAllBytes(Path.Combine(broken.Mmaps, "000.mmap"), new byte[10]);
        using var service = new ReferenceNavMeshService(broken.Mmaps, _log);

        NavMapOpen open = service.OpenMap(0);

        Assert.Null(open.Map);
        Assert.Equal(NavTileState.Rejected, open.State);
        Assert.Contains("28 bytes", open.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void AvailableTiles_ListsTileFiles_AndIgnoresOtherNames()
    {
        Add(32, 32);
        Add(5, 60);
        File.WriteAllBytes(Path.Combine(_data.Mmaps, "0001234.mmtile"), [1]);
        File.WriteAllBytes(Path.Combine(_data.Mmaps, "000ab12.mmtile"), [1]);
        File.WriteAllBytes(Path.Combine(_data.Mmaps, "0006464.mmtile"), [1]);
        File.WriteAllBytes(Path.Combine(_data.Mmaps, "00032.mmtile"), [1]);
        using ReferenceNavMeshService service = Service();

        IReadOnlyCollection<TileKey> tiles = service.AvailableTiles(0);

        Assert.Equal(new[] { Key(5, 60), Key(12, 34), Key(32, 32) }, tiles);
        Assert.Empty(service.AvailableTiles(1));
    }

    [Fact]
    public void Read_LoadsTilesOnDemand_AndReportsEachOutcome()
    {
        BuiltTile a = Add(32, 32);
        Add(33, 32);
        _data.WriteRaw(31, 32, [1, 2, 3, 4]);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);
        Assert.Equal(0, map.ResidentCount);

        var outcomes = map.Read([Key(32, 32), Key(33, 32), Key(31, 32), Key(30, 32)], view => view.Tiles.ToDictionary(t => t.Key, t => t.Value));

        Assert.Equal(NavTileState.Ok, outcomes[Key(32, 32)].State);
        Assert.Equal(NavTileState.Ok, outcomes[Key(33, 32)].State);
        Assert.Equal(NavTileState.Rejected, outcomes[Key(31, 32)].State);
        Assert.Contains("shorter than", outcomes[Key(31, 32)].Detail, StringComparison.Ordinal);
        Assert.Equal(NavTileState.Missing, outcomes[Key(30, 32)].State);
        Assert.Equal(2, map.ResidentCount);

        (int polys, TileKey? terrain, DtMeshHeader header) = map.Read([Key(32, 32)], view =>
        {
            ResidentNavTile resident = view.Resident(Key(32, 32))!;
            return (resident.Tile.data.polys.Length, view.TerrainTileOf(resident.Tile), resident.Tile.data.header);
        });
        Assert.Equal(a.PolyCount, polys);
        Assert.Equal(Key(32, 32), terrain);
        Assert.Equal((a.DetourX, a.DetourY), (header.x, header.y));
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void Peek_AnswersWithoutReading()
    {
        Add(32, 32);
        _data.WriteRaw(31, 32, [9]);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);

        Assert.Null(map.Peek(Key(32, 32)));
        Assert.Equal(NavTileState.Missing, map.Peek(Key(30, 32))!.State);
        Assert.Null(map.Peek(Key(31, 32)));
        Assert.Equal(0, map.ResidentCount);

        map.Read([Key(32, 32), Key(31, 32)], _ => 0);

        Assert.Equal(NavTileState.Ok, map.Peek(Key(32, 32))!.State);
        Assert.Equal(NavTileState.Rejected, map.Peek(Key(31, 32))!.State);
    }

    [Fact]
    public void Read_FindsATileAgainOnceItsFileIsFixed()
    {
        _data.WriteRaw(32, 32, [1, 2, 3]);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);
        Assert.Equal(NavTileState.Rejected, map.Read([Key(32, 32)], v => v.Tiles[Key(32, 32)].State));

        Add(32, 32);

        Assert.Equal(NavTileState.Ok, map.Read([Key(32, 32)], v => v.Tiles[Key(32, 32)].State));
    }

    [Fact]
    public void Read_LinksNeighbouringTiles_AcrossTheirSharedBorder()
    {
        Add(32, 32);
        Add(32, 31);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);

        (NavLinkCounts left, NavLinkCounts right) = map.Read([Key(32, 32), Key(32, 31)], view =>
        {
            ResidentNavTile l = view.Resident(Key(32, 32))!;
            ResidentNavTile r = view.Resident(Key(32, 31))!;
            return (NavLinkInventory.Count(NavLinkInventory.Collect(view, l), l.Tile), NavLinkInventory.Count(NavLinkInventory.Collect(view, r), r.Tile));
        });

        // A 4 x 4 grid has 24 shared edges (48 directed links) and 4 border polygons facing the neighbour.
        Assert.Equal(48, left.Internal);
        Assert.Equal(4, left.External);
        Assert.Equal(0, left.Stale);
        Assert.Equal(48, right.Internal);
        Assert.Equal(4, right.External);
        Assert.Equal(0, right.Stale);
    }

    [Fact]
    public void Read_EvictsTheLeastRecentlyUsedTiles_AndUnlinksTheirNeighbours()
    {
        for (int x = 20; x < 32; x++)
        {
            Add(x, 32);
        }

        using ReferenceNavMeshService service = Service(maxResident: 9);
        ReferenceNavMap map = Map(service);
        for (int x = 20; x < 32; x++)
        {
            map.Read([Key(x, 32)], _ => 0);
        }

        Assert.Equal(9, map.ResidentCount);
        Assert.Equal(NavTileState.Ok, map.Peek(Key(31, 32))!.State);

        // Tiles 20-22 were the oldest. Tile 23 is still there; its link to 22 went with 22, the one to 24 stayed.
        (bool resident22, NavLinkCounts counts) = map.Read([Key(23, 32)], view =>
        {
            ResidentNavTile r = view.Resident(Key(23, 32))!;
            return (view.Resident(Key(22, 32)) is not null, NavLinkInventory.Count(NavLinkInventory.Collect(view, r), r.Tile));
        });

        Assert.False(resident22);
        Assert.Equal(4, counts.External);
        Assert.Equal(0, counts.Stale);
        Assert.Equal(9, map.ResidentCount);
    }

    [Fact]
    public void Read_KeepsEveryRequestedTile_EvenPastTheLimit()
    {
        var keys = new List<TileKey>();
        for (int x = 20; x < 32; x++)
        {
            Add(x, 32);
            keys.Add(Key(x, 32));
        }

        using ReferenceNavMeshService service = Service(maxResident: 9);
        ReferenceNavMap map = Map(service);

        int resident = map.Read(keys, view => keys.Count(k => view.Resident(k) is not null));

        Assert.Equal(12, resident);
    }

    [Fact]
    public void Read_RefusesATileClaimingAnotherTilesDetourSlot()
    {
        BuiltTile first = Add(32, 32);
        Add(33, 32, detourX: first.DetourX, detourY: first.DetourY);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);

        var outcomes = map.Read([Key(32, 32), Key(33, 32)], view => view.Tiles.ToDictionary(t => t.Key, t => t.Value));

        Assert.Equal(NavTileState.Ok, outcomes[Key(32, 32)].State);
        Assert.Equal(NavTileState.Rejected, outcomes[Key(33, 32)].State);
        Assert.Contains("already holds", outcomes[Key(33, 32)].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void Read_RefusesATileWhoseLinkTableIsTooSmall_AndKeepsTheMeshUsable()
    {
        Add(32, 32);
        Add(32, 31, maxLinks: 2);
        Add(32, 30);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);

        (NavTileOutcome bad, NavLinkCounts goodCounts, NavLinkCounts otherCounts, ResidentNavTile? residentBad) = map.Read([Key(32, 32), Key(32, 31), Key(32, 30)], view =>
        {
            ResidentNavTile good = view.Resident(Key(32, 32))!;
            ResidentNavTile other = view.Resident(Key(32, 30))!;
            return (view.Tiles[Key(32, 31)], NavLinkInventory.Count(NavLinkInventory.Collect(view, good), good.Tile), NavLinkInventory.Count(NavLinkInventory.Collect(view, other), other.Tile), view.Resident(Key(32, 31)));
        });

        Assert.Equal(NavTileState.Rejected, bad.State);
        Assert.Contains("link table", bad.Detail, StringComparison.Ordinal);
        Assert.Null(residentBad);

        // The bad tile sits between the other two: neither has a link into it, and both keep their own internal links.
        Assert.Equal(0, goodCounts.External);
        Assert.Equal(0, goodCounts.Stale);
        Assert.Equal(48, goodCounts.Internal);
        Assert.Equal(0, otherCounts.External);
        Assert.Equal(48, otherCounts.Internal);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(NavTileState.Rejected, map.Peek(Key(32, 31))!.State);
    }

    [Fact]
    public void Read_RejectsATileOfAnotherMapOrOutsideTheGrid()
    {
        Add(32, 32);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);

        Assert.Throws<ArgumentException>(() => map.Read([new TileKey(1, 32, 32)], _ => 0));
        Assert.Throws<ArgumentException>(() => map.Read([new TileKey(0, 64, 0)], _ => 0));
        Assert.Throws<ArgumentException>(() => map.Read([TileKey.Global(0)], _ => 0));
    }

    [Fact]
    public async Task Read_FromManyThreads_ReturnsConsistentTiles()
    {
        for (int x = 28; x < 32; x++)
        {
            for (int y = 28; y < 32; y++)
            {
                Add(x, y);
            }
        }

        using ReferenceNavMeshService service = Service(maxResident: 9);
        ReferenceNavMap map = Map(service);
        int failures = 0;
        Task[] workers = Enumerable.Range(0, 3).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 150; i++)
            {
                int x = 28 + ((i + worker) % 4);
                int y = 28 + (((i * 3) + worker) % 4);
                TileKey key = Key(x, y);
                (NavTileState state, int polys, int internalLinks) = map.Read([key], view =>
                {
                    ResidentNavTile? resident = view.Resident(key);
                    return resident is null
                        ? (view.Tiles[key].State, 0, 0)
                        : (view.Tiles[key].State, resident.Tile.data.polys.Length, NavLinkInventory.Count(NavLinkInventory.Collect(view, resident), resident.Tile).Internal);
                });
                if (state != NavTileState.Ok || polys != 16 || internalLinks != 48)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(0, failures);
        Assert.InRange(map.ResidentCount, 1, 9);
    }

    [Fact]
    public async Task Read_OfTheSameTileFromManyThreads_GivesEveryoneTheTile()
    {
        Add(32, 32);
        using ReferenceNavMeshService service = Service();
        ReferenceNavMap map = Map(service);
        using var gate = new ManualResetEventSlim();

        Task<NavTileState>[] readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            return map.Read([Key(32, 32)], view => view.Tiles[Key(32, 32)].State);
        })).ToArray();
        gate.Set();

        NavTileState[] states = await Task.WhenAll(readers);

        Assert.All(states, s => Assert.Equal(NavTileState.Ok, s));
        Assert.Equal(1, map.ResidentCount);
    }
}
