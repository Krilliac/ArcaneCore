using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The <c>dungeon</c> scenario on the maps the content importer writes, end to end: a world database with classic-db's Deadmines portal
/// rows (triggers 78 and 119 and their teleports, as an older import left them), refreshed with <c>arcane-content-importer refresh</c>
/// from Map.dbc, AreaTable.dbc (build-5875 layouts, synthetic rows) and the dump's <c>instance_template</c>; the world then reads
/// <c>map_template</c> and <c>area_template</c> through the EF store. Before the importer wrote every map, map 36 never reached
/// <c>map_template</c> and the portal could not create the instance.
/// </summary>
public sealed class DungeonImportedMapsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-dungeon-maps-" + Guid.NewGuid().ToString("N"));

    public DungeonImportedMapsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Dungeon_OnTheRefreshedMapTables_TwoBotsEnterOneDeadminesInstance()
    {
        string world = await RefreshedWorldAsync();

        await using ScenarioTestWorld scenarioWorld = await ScenarioTestWorld.StartAsync(services =>
            services.AddSingleton<IMapDataStore>(new FileMapStore(world)));

        ScenarioReport report = await scenarioWorld.RunPassingAsync(new DungeonEntryScenario());

        Assert.Contains("the leader is in a new instance the group is bound to", report.ToString());
        Assert.Contains("the member lands in the leader's instance", report.ToString());
        MapTemplate deadmines = await scenarioWorld.Host.World.InvokeAsync(() =>
            ArcaneCore.Game.Maps.Templates.WorldMaps.Of(scenarioWorld.Host.World).Registry.Find(DungeonEntryScenario.Deadmines)!);
        Assert.Equal((MapType.Instance, 10u, 0, "instance_deadmines"), (deadmines.MapType, deadmines.PlayerLimit, deadmines.GhostEntranceMap, deadmines.ScriptName));
    }

    private async Task<string> RefreshedWorldAsync()
    {
        string path = Path.Combine(_directory, "world.db");
        await using (WorldDbContext db = Open(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            // classic-db z2815 areatrigger_teleport 78 and 119; the trigger volumes as DeadminesTestContent has them.
            db.Set<AreaTriggerTemplateRow>().AddRange(
                new AreaTriggerTemplateRow { Id = 78, MapId = 0, X = -11208.6f, Y = 1679.6f, Z = 24.6f, BoxX = 5f, BoxY = 10f, BoxZ = 8f, BoxOrientation = 1.5f, Name = "Deadmines Entrance" },
                new AreaTriggerTemplateRow { Id = 119, MapId = 36, X = -14.6f, Y = -390.5f, Z = 62.4f, Radius = 5f, Name = "Deadmines Exit" });
            db.Set<AreaTriggerTeleportRow>().AddRange(
                new AreaTriggerTeleportRow
                {
                    Id = 78, Name = "Deadmines - Entering", Message = "You must be at least level 10 to enter.", RequiredLevel = 10, TargetMap = 36,
                    TargetPositionX = -16.4f, TargetPositionY = -383.07f, TargetPositionZ = 61.78f, TargetOrientation = 1.9f,
                },
                new AreaTriggerTeleportRow
                {
                    Id = 119, Name = "Deadmines - Exiting", TargetMap = 0, TargetPositionX = -11208.7f, TargetPositionY = 1675.9f,
                    TargetPositionZ = 24.5733f, TargetOrientation = 4.71239f,
                });
            await db.SaveChangesAsync();
        }

        string dbc = Path.Combine(_directory, "dbc");
        Directory.CreateDirectory(dbc);
        // Map.dbc: id, directory, instance type (field 2), pvp, enUS name (field 4), ..., linked zone (field 19). Names: 1 "Azeroth",
        // 9 "Kalimdor", 18 "Deadmines".
        string[] strings = ["", "Azeroth", "Kalimdor", "Deadmines"];
        WriteDbc(Path.Combine(dbc, "Map.dbc"), 42, strings, Map(0, 0, 1, 0), Map(1, 0, 9, 0), Map(36, 1, 18, 1581));
        // AreaTable.dbc: id, map, parent zone, explore flag, ..., level (10), name (11), team (20): Elwynn Forest and The Deadmines.
        WriteDbc(Path.Combine(dbc, "AreaTable.dbc"), 25, strings, Area(12, 0, 12), Area(1581, 36, 0));

        string dump = Path.Combine(_directory, "world.sql");
        File.WriteAllText(dump,
            "INSERT INTO `instance_template` (`map`,`parent`,`levelMin`,`levelMax`,`maxPlayers`,`reset_delay`,`ghostEntranceMap`,`ghostEntranceX`,`ghostEntranceY`,`ScriptName`,`mountAllowed`) " +
            "VALUES (36,0,17,26,10,0,0,-11207.8,1681.15,'instance_deadmines',1);\n");

        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(["refresh", dump, "--database", path, "--dbc-dir", dbc], output, error, CancellationToken.None);
        Assert.True(code == ExitCodes.Ok, $"refresh exited {code}\n{error}{output}");
        return path;
    }

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static uint[] Map(uint id, uint type, uint name, uint linkedZone)
    {
        uint[] row = new uint[42];
        row[0] = id;
        row[1] = name;
        row[2] = type;
        row[4] = name;
        row[19] = linkedZone;
        return row;
    }

    private static uint[] Area(uint id, uint map, uint flag)
    {
        uint[] row = new uint[25];
        row[0] = id;
        row[1] = map;
        row[3] = flag;
        row[11] = 18;
        return row;
    }

    private static void WriteDbc(string path, int fields, string[] strings, params uint[][] rows)
    {
        byte[] block = Encoding.UTF8.GetBytes(string.Join("\0", strings) + "\0");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("WDBC"u8.ToArray());
        writer.Write((uint)rows.Length);
        writer.Write((uint)fields);
        writer.Write((uint)(fields * 4));
        writer.Write((uint)block.Length);
        foreach (uint[] row in rows)
        {
            foreach (uint value in row) writer.Write(value);
        }

        writer.Write(block);
    }

    /// <summary>The world's map store reading the refreshed SQLite world database (the EF store the server uses).</summary>
    private sealed class FileMapStore(string path) : IMapDataStore
    {
        public async Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            await using WorldDbContext db = Open(path);
            return await new EfMapDataStore(db).LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
