using System.IO.Compression;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>A test that needs the (GPL, never committed) classic-db dump and is skipped, visibly, without it.</summary>
public sealed class ClassicDbEventsFactAttribute : FactAttribute
{
    public static string DumpPath { get; } = Environment.GetEnvironmentVariable("ARCANE_CLASSICDB_DUMP")
        ?? @"D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz";

    public ClassicDbEventsFactAttribute()
    {
        if (!File.Exists(DumpPath))
        {
            Skip = $"classic-db dump not found at {DumpPath} (set ARCANE_CLASSICDB_DUMP)";
        }
    }
}

/// <summary>
/// The real classic-db event table through the whole load path (dump importer, dialect detection, load rules, schedule, service): which events
/// run at fixed instants, checked against an independent calculation of the mangos-classic rule (a separate implementation in a script over the
/// same rows: exclusive start, the elapsed-time recurrence, leap days that keep holidays on their calendar date, serverside events never
/// scheduled, <c>linkedTo</c> gating). The computed schedules (yearly, lunar new year, Easter) are all out of season at these instants.
/// </summary>
public sealed class GameEventClassicDbTests
{
    private static GameEventContent ReadRealTables()
    {
        using FileStream file = File.OpenRead(ClassicDbEventsFactAttribute.DumpPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var importer = new GameEventDumpImporter();
        importer.Read(reader);
        return importer.BuildContent();
    }

    private static ushort[] ActiveAt(GameEventContent content, DateTimeOffset now)
    {
        var options = new GameEventOptions();
        GameEventLoadResult load = GameEventLoader.Load(content, options, now, TimeZoneInfo.Utc);
        var service = new GameEventService(load, options, () => now, TimeZoneInfo.Utc, NullLogger.Instance);
        service.Initialize(new HashSet<ushort>());
        return [.. service.ActiveEvents.Order()];
    }

    [ClassicDbEventsFact]
    public void RealTables_LoadAsTheCMangosDialect_WithTheDocumentedCounts()
    {
        GameEventContent content = ReadRealTables();
        var options = new GameEventOptions();
        GameEventLoadResult load = GameEventLoader.Load(content, options, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        Assert.Equal(GameEventDialect.CMangos, load.Dialect);
        Assert.Equal(GameEventStartBoundary.Exclusive, load.Boundary);
        Assert.Equal(67, load.Definitions.Count);
        Assert.Equal(1, load.Definitions.Count(d => d.ScheduleType == GameEventScheduleType.LunarNewYear));
        Assert.Equal(1, load.Definitions.Count(d => d.ScheduleType == GameEventScheduleType.Easter));
        Assert.Equal(3, load.Definitions.Count(d => d.ScheduleType == GameEventScheduleType.Yearly));
        Assert.Equal(26, load.Definitions.Count(d => d.ScheduleType == GameEventScheduleType.Serverside));
        Assert.Equal((ushort)12, load.Definitions.Single(d => d.Id == 24).LinkedTo);
        Assert.Equal((ushort)12, load.Definitions.Single(d => d.Id == 25).LinkedTo);

        // the rows that name a missing event do not exist in this dump; the spawn rows are checked by the data tests
        Assert.DoesNotContain(load.Issues, i => i.Contains("does not exist in game_event", StringComparison.Ordinal));
        // event 85 is type 1 with no game_event_time row: it never runs, and nothing complains
        GameEventDefinition eightyFive = load.Definitions.Single(d => d.Id == 85);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), eightyFive.Start);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0), eightyFive.End);
    }

    [ClassicDbEventsFact]
    public void TheDailyAndWeeklyWindows_AreOpenAtTheFixedInstants_ThatAnIndependentCalculationSays()
    {
        GameEventContent content = ReadRealTables();

        // 2026-10-03 12:00 UTC: DayTime 7AM-8PM (400) is open, the night events (27, 401) are not
        Assert.Equal<ushort>([4, 20, 30, 33, 35, 38, 400, 1024], ActiveAt(content, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)));

        // 23:00: the night is open (27) and so is event 24, which only starts while event 12 runs and is therefore NOT started (linkedTo 12 gate)
        ushort[] night = ActiveAt(content, new DateTimeOffset(2026, 10, 3, 23, 0, 0, TimeSpan.Zero));
        Assert.Equal<ushort>([4, 20, 27, 30, 33, 35, 38, 1024], night);
        Assert.DoesNotContain((ushort)24, night);
        Assert.DoesNotContain((ushort)400, night);

        // 2026-10-04 06:30: still the night window of the 3rd (it runs past midnight), and other weekly windows (13: Fire invasion, 39) are open
        Assert.Equal<ushort>([4, 13, 20, 27, 30, 33, 35, 38, 39], ActiveAt(content, new DateTimeOffset(2026, 10, 4, 6, 30, 0, TimeSpan.Zero)));

        // 2026-06-25 12:00: Midsummer Fire Festival (1) runs, as do 11, 32 and 36
        ushort[] summer = ActiveAt(content, new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal<ushort>([1, 11, 32, 36, 400, 1024], summer);
    }

    [ClassicDbEventsFact]
    public void TheComputedSchedules_AreInSeasonWhereTheyShouldBe()
    {
        GameEventContent content = ReadRealTables();

        // Easter 2026 is April 5th: event 9 (Noblegarden, five days) is open on the 6th and closed on the 12th
        Assert.Contains((ushort)9, ActiveAt(content, new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero)));
        Assert.DoesNotContain((ushort)9, ActiveAt(content, new DateTimeOffset(2026, 4, 12, 12, 0, 0, TimeSpan.Zero)));

        // the lunar new year 2026 is February 17th: event 7 (Lunar Festival, 20 days) is open on the 20th of February
        Assert.Contains((ushort)7, ActiveAt(content, new DateTimeOffset(2026, 2, 20, 12, 0, 0, TimeSpan.Zero)));
        Assert.DoesNotContain((ushort)7, ActiveAt(content, new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero)));

        // Feast of Winter Veil (2) runs across New Year: open on December 30th and on January 2nd of the following year
        ushort[] december = ActiveAt(content, new DateTimeOffset(2026, 12, 30, 12, 0, 0, TimeSpan.Zero));
        ushort[] january = ActiveAt(content, new DateTimeOffset(2027, 1, 2, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains((ushort)2, december);
        Assert.Contains((ushort)2, january);
        Assert.DoesNotContain((ushort)2, ActiveAt(content, new DateTimeOffset(2027, 1, 6, 12, 0, 0, TimeSpan.Zero)));
    }
}
