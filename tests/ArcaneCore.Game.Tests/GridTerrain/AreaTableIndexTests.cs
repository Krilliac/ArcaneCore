using System.Diagnostics;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// <see cref="AreaTable.GetByAreaFlagAndMap"/> answers from lookups built at load time (it scanned every area on each call, once per player
/// per zone update and on every bot probe), with vmangos' answer: the first entry of the flag on the map, else the last entry of the flag
/// anywhere, else the map's linked zone.
/// </summary>
public sealed class AreaTableIndexTests
{
    private static readonly MapRegistry Maps = new([
        new MapTemplate(0, 0, MapType.Common, 7, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
        new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        new MapTemplate(36, 0, MapType.Instance, 9, 10, 0, 0, 0, 0, "Deadmines", ""),
    ]);

    /// <summary>vmangos <c>AreaEntry::GetByAreaFlagAndMap</c> as the scan it is, in entry order (the oracle).</summary>
    private static AreaTemplate? Scan(IReadOnlyList<AreaTemplate> byEntry, uint flag, uint mapId, AreaTable table)
    {
        AreaTemplate? anyMap = null;
        foreach (AreaTemplate area in byEntry)
        {
            if (flag != 0 && flag == area.ExploreFlag)
            {
                if (area.MapId == mapId) return area;
                anyMap = area;
            }
        }

        return anyMap ?? (Maps.Find(mapId) is { } map ? table.GetById(map.LinkedZone) : null);
    }

    /// <summary>
    /// Every (flag, map) of a table full of duplicate flags, given in shuffled order: the same entry as the scan, so the first-on-the-map,
    /// last-on-any-map and linked-zone rules and the entry order all hold; flag 0 and unknown flags fall to the linked zone.
    /// </summary>
    [Fact]
    public void TheIndexedLookup_AnswersLikeTheEntryOrderScan_ForEveryFlagAndMap()
    {
        var random = new Random(5875);
        var areas = new List<AreaTemplate> { new(7, 0, 0, 0, 0, 1, "Linked EK", 0, 0), new(9, 36, 0, 0, 0, 1, "Linked DM", 0, 0) };
        for (uint entry = 100; entry < 700; entry++)
            areas.Add(new AreaTemplate(entry, (uint)(random.Next(3) switch { 0 => 0, 1 => 1, _ => 36 }), entry % 7 == 0 ? 0 : entry / 7,
                (uint)random.Next(0, 120), 0, 1, "A" + entry, 0, 0));
        var table = new AreaTable(areas.OrderBy(_ => random.Next()), Maps);
        IReadOnlyList<AreaTemplate> byEntry = table.All;

        int duplicates = byEntry.Where(a => a.ExploreFlag != 0).GroupBy(a => a.ExploreFlag).Count(g => g.Count() > 1);
        Assert.True(duplicates > 50); // the rules are exercised, not just the single-entry case
        foreach (uint mapId in new uint[] { 0, 1, 36, 489 })
        {
            for (uint flag = 0; flag < 130; flag++)
            {
                Assert.Same(Scan(byEntry, flag, mapId, table), table.GetByAreaFlagAndMap(flag, mapId));
                AreaTemplate? expected = Scan(byEntry, flag, mapId, table);
                Assert.Equal(expected is null ? (0u, 0u) : (expected.IsZone ? expected.Entry : expected.ZoneId, expected.Entry),
                    table.GetZoneAndAreaId(flag, mapId));
            }
        }
    }

    /// <summary>
    /// A lookup no longer walks the table: 5,000 lookups of a flag no area has (the scan's worst case, every entry read) over 100,000 areas
    /// take well under the 100 ms bound (a scan does 500 million comparisons), and allocate nothing.
    /// </summary>
    [Fact]
    public void ALookup_DoesNotScanTheTable_AndAllocatesNothing()
    {
        var areas = new List<AreaTemplate> { new(7, 0, 0, 0, 0, 1, "Linked EK", 0, 0) };
        for (uint entry = 10; entry < 100_010; entry++)
            areas.Add(new AreaTemplate(entry, entry % 2, 7, entry, 0, 1, "A", 0, 0));
        var table = new AreaTable(areas, Maps);
        Assert.Equal((7u, 7u), table.GetZoneAndAreaId(999_999, 0)); // warm: the linked zone

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        uint sum = 0;
        for (int i = 0; i < 5_000; i++)
            sum += table.GetZoneAndAreaId(999_999, 0).AreaId;
        clock.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.Equal(7u * 5_000, sum);
        Assert.Equal(0, bytes);
        Assert.True(clock.ElapsedMilliseconds < 100, $"5,000 lookups took {clock.ElapsedMilliseconds} ms: the table is being scanned");
    }
}
