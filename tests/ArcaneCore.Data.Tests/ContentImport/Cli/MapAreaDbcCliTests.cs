using System.Text;
using ArcaneCore.Data.Content.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

public sealed class MapAreaDbcCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-map-cli-" + Guid.NewGuid().ToString("N"));

    public MapAreaDbcCliTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CliImportsEveryMapAndAreaAndRefusesNonreplaceCollisions()
    {
        string map = Input("Map.dbc", 42, Map(0), Map(1), Map(33, type: 1));
        uint[] zone = Area(12, 0, 0, 12);
        uint[] northshire = Area(125, 0, 12, 125);
        string area = Input("AreaTable.dbc", 25, zone, northshire, Area(5000, 33, 0, 5000));
        string database = Path.Combine(_root, "world.db");
        string report = Path.Combine(_root, "counts.json");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await ContentImporterCli.RunAsync(["import-map-dbc", map, area, "--database", database, "--report", report], output, error, default));
        Assert.True(output.ToString().Contains("Imported 3 maps and 3 areas", StringComparison.Ordinal), output.ToString());
        Assert.Contains("\"mappedAreas\": 3", File.ReadAllText(report));
        await using var connection = new SqliteConnection("Data Source=" + database);
        await connection.OpenAsync();
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM area_template WHERE Entry=125 AND ZoneId=12"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM area_template WHERE Entry=5000 AND MapId=33"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT MapType FROM map_template WHERE Entry=33"));
        Assert.Equal(4, await ContentImporterCli.RunAsync(["import-map-dbc", map, area, "--database", database], new StringWriter(), new StringWriter(), default));
        Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM map_template"));
    }

    /// <summary>The dungeon columns Map.dbc does not carry come from the dump given with --dump (classic-db instance_template).</summary>
    [Fact]
    public async Task CliTakesTheDungeonColumnsFromTheDump()
    {
        string map = Input("Map.dbc", 42, Map(0), Map(1), Map(33, type: 1));
        string area = Input("AreaTable.dbc", 25, Area(12, 0, 0, 12));
        string dump = Path.Combine(_root, "instances.sql");
        File.WriteAllText(dump,
            "INSERT INTO `instance_template` (`map`,`parent`,`levelMin`,`levelMax`,`maxPlayers`,`reset_delay`,`ghostEntranceMap`,`ghostEntranceX`,`ghostEntranceY`,`ScriptName`,`mountAllowed`) " +
            "VALUES (33,0,20,26,10,0,0,-230.989,1571.57,'instance_shadowfang_keep',0);\n");
        string database = Path.Combine(_root, "world.db");
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, await ContentImporterCli.RunAsync(["import-map-dbc", map, area, "--dump", dump, "--database", database], output, error, default));

        await using var connection = new SqliteConnection("Data Source=" + database);
        await connection.OpenAsync();
        Assert.Equal(10L, await ScalarAsync(connection, "SELECT PlayerLimit FROM map_template WHERE Entry=33"));
        Assert.Equal("instance_shadowfang_keep", await ScalarAsync(connection, "SELECT ScriptName FROM map_template WHERE Entry=33"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT GhostEntranceMap FROM map_template WHERE Entry=33"));
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task InvalidParentIsRefusedBeforeDestinationCreation()
    {
        string map = Input("Map.dbc", 42, Map(0), Map(1));
        string area = Input("AreaTable.dbc", 25, Area(125, 0, 999, 125));
        string database = Path.Combine(_root, "invalid.db");
        Assert.Equal(3, await ContentImporterCli.RunAsync(["import-map-dbc", map, area, "--database", database], new StringWriter(), new StringWriter(), default));
        Assert.False(File.Exists(database));
    }

    private string Input(string name, int fields, params uint[][] rows)
    {
        string path = Path.Combine(_root, name);
        byte[] strings = Encoding.UTF8.GetBytes("\0fixture\0");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("WDBC"));
        writer.Write((uint)rows.Length);
        writer.Write((uint)fields);
        writer.Write((uint)(fields * 4));
        writer.Write((uint)strings.Length);
        foreach (uint[] row in rows)
            foreach (uint value in row) writer.Write(value);
        writer.Write(strings);
        return path;
    }

    private static uint[] Map(uint id, uint type = 0)
    {
        uint[] row = new uint[42];
        row[0] = id;
        row[2] = type;
        row[4] = 1;
        return row;
    }

    private static uint[] Area(uint id, uint map, uint parent, uint flag)
    {
        uint[] row = new uint[25];
        row[0] = id; row[1] = map; row[2] = parent; row[3] = flag; row[11] = 1;
        return row;
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(_root, "world.db"));
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_root, recursive: true);
    }
}
