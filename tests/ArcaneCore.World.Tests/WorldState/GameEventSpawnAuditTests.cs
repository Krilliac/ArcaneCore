using System.Globalization;
using System.IO.Compression;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Tests.Characters.Creation;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The once-per-start audit of game-event rows whose spawn is not in the world (<see cref="GameEventSpawnFeature"/>). The wave-9 rehearsal
/// logged "3148 creature guid(s) not in the creature spawns": every creature event guid of classic-db z2815, although the world held all
/// but 33 of them. The audit ran at attach, when the gameobject content was loaded but the creature content (installed from the world
/// thread) was still empty. The rows below are real classic-db z2815 rows.
/// </summary>
public sealed class GameEventSpawnAuditTests
{
    // classic-db z2815 `creature` rows (guid, id, map, x, y, z) listed in game_event_creature, and one guid of game_event_creature that is
    // in no spawn table of the dump.
    private static readonly CreatureSpawn Orgrimmar46973 = new() { Guid = 46973, Entry = 9550, MapId = 1, X = 1663.45f, Y = -4393.85f, Z = 22.234f };  // event 2
    private static readonly CreatureSpawn Ironforge4147 = new() { Guid = 4147, Entry = 12372, MapId = 0, X = -5524.53f, Y = -1354.1f, Z = 398.694f };  // event -27
    private const uint CreatureNotInDump = 31180;  // event 14

    // classic-db z2815 `gameobject` row 1 (event 1) and a game_event_gameobject guid (event 9, Noblegarden) that is in no spawn table.
    private static readonly GameObjectSpawn Durotar1 = new() { Guid = 1, Entry = 187653, MapId = 1, X = -959.702f, Y = -3739.06f, Z = 5.66216f };
    private const uint GameObjectNotInDump = 83091;

    private static GameEventContent EventRows() => new(
        [
            new GameEventRecord(1, 1, 525600, 20160, 0, 0, "Midsummer"), new GameEventRecord(2, 1, 525600, 30240, 0, 0, "Winter Veil"),
            new GameEventRecord(9, 1, 524160, 7200, 0, 0, "Noblegarden"), new GameEventRecord(14, 1, 10080, 1440, 0, 0, "Fishing"),
            new GameEventRecord(27, 1, 1440, 180, 0, 0, "Nights"),
        ],
        [.. new uint[] { 1, 2, 9, 14, 27 }.Select(e => new GameEventTimeRecord(e, "2090-01-01 00:00:00", "2090-12-31 22:59:59"))],
        [new GameEventSpawnRecord(46973, 2), new GameEventSpawnRecord(4147, -27), new GameEventSpawnRecord(CreatureNotInDump, 14)],
        [new GameEventSpawnRecord(1, 1), new GameEventSpawnRecord(GameObjectNotInDump, 9)],
        [], [], []);

    [Fact]
    public async Task TheStartupAudit_CountsOnlyTheEventGuidsThatAreInNoSpawnTable()
    {
        var logs = new LogCapture();
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([], [Orgrimmar46973, Ironforge4147], [], [], []));
        GameObjectTestStore.Current.Value = new GameObjectTestContext(new GameObjectContent([], [Durotar1], [], [], []), LootContent.Empty);
        GameEventTestStore.Current.Value = EventRows();
        WorldTestHost started;
        try
        {
            started = WorldTestHost.Start(configureServices: logs.Register);
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            GameObjectTestStore.Current.Value = null;
            GameEventTestStore.Current.Value = null;
        }

        await using WorldTestHost host = started;
        static bool IsAudit(string line) => line.Contains("game event rows without a spawn", StringComparison.Ordinal);
        await WorldTestHost.WaitForAsync(() => logs.Lines.Any(IsAudit), "the audit runs once both contents are installed");
        await host.OnWorldAsync(() => 0); // a few more ticks: the audit must not run a second time
        await host.OnWorldAsync(() => 0);
        string audit = Assert.Single(logs.Lines, IsAudit);
        Assert.Contains("1 creature guid(s) not in the creature spawns, 1 gameobject guid(s)", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void CountOrphans_OfAnEmptyCreatureContent_IsEveryCreatureGuid_WhichIsWhyTheAuditWaitsForTheInstall()
    {
        GameEventSpawns gate = Gate(EventRows());
        var creatures = new CreatureContent([], [Orgrimmar46973, Ironforge4147], [], [], []);
        var objects = new GameObjectContent([], [Durotar1], [], [], []);

        Assert.Equal((1, 1), GameEventSpawnFeature.CountOrphans(gate, creatures, objects));
        Assert.Equal((3, 1), GameEventSpawnFeature.CountOrphans(gate, CreatureContent.Empty, objects));
    }

    /// <summary>
    /// The whole classic-db z2815 dump: the event guids missing from its own <c>creature</c> and <c>gameobject</c> tables (33 and 1126) are
    /// dropped by the importer, so the audit has nothing to report; the wave-9 "3148" (now 3115) is the audit against an empty creature content.
    /// </summary>
    [ClassicDbEventsFact]
    public void RealDump_AfterTheImporterDropsTheOrphans_NoEventGuidIsMissingFromTheSpawnTables()
    {
        GameEventContent events;
        var creatures = new List<CreatureSpawn>();
        var objects = new List<GameObjectSpawn>();
        using (StreamReader reader = OpenDump())
        {
            var importer = new GameEventDumpImporter();
            importer.Read(reader);
            events = importer.BuildContent();
        }

        using (StreamReader reader = OpenDump())
        {
            foreach (object item in new MySqlDumpReader(reader).Read())
            {
                if (item is not DumpRow row || !row.TryGet(out string? guidText, "guid") || !row.TryGet(out string? mapText, "map"))
                {
                    continue;
                }

                uint guid = uint.Parse(guidText!, CultureInfo.InvariantCulture);
                uint map = uint.Parse(mapText!, CultureInfo.InvariantCulture);
                if (row.Table.Equals("creature", StringComparison.OrdinalIgnoreCase))
                {
                    creatures.Add(new CreatureSpawn { Guid = guid, Entry = 1, MapId = map, X = 0, Y = 0, Z = 0 });
                }
                else if (row.Table.Equals("gameobject", StringComparison.OrdinalIgnoreCase))
                {
                    objects.Add(new GameObjectSpawn { Guid = guid, Entry = 1, MapId = map, X = 0, Y = 0, Z = 0 });
                }
            }
        }

        Assert.Equal(66310, creatures.Count);
        Assert.Equal(47827, objects.Count);
        GameEventSpawns gate = Gate(events);
        var objectContent = new GameObjectContent([], objects, [], [], []);
        // the importer now drops the 33 and 1126 rows whose guid is in no spawn table (classic-db Updates/4498), so nothing is left over;
        // against an empty creature content every kept creature guid (3148 - 33) would still count
        Assert.Equal((0, 0), GameEventSpawnFeature.CountOrphans(gate, new CreatureContent([], creatures, [], [], []), objectContent));
        Assert.Equal((3115, 0), GameEventSpawnFeature.CountOrphans(gate, CreatureContent.Empty, objectContent));
    }

    private static StreamReader OpenDump()
        => new(new GZipStream(File.OpenRead(ClassicDbEventsFactAttribute.DumpPath), CompressionMode.Decompress));

    private static GameEventSpawns Gate(GameEventContent content)
    {
        var options = new GameEventOptions();
        DateTimeOffset now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        GameEventLoadResult load = GameEventLoader.Load(content, options, now, TimeZoneInfo.Utc);
        var service = new GameEventService(load, options, () => now, TimeZoneInfo.Utc, NullLogger.Instance);
        return new GameEventSpawns(service, service.Rows, () => Array.Empty<Map>());
    }
}
