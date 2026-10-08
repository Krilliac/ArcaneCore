using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests.ClientData;

/// <summary>
/// The world database against the client DBCs (<see cref="DbcCrossReferences"/>) and <c>arcane-db dbc</c>: dangling ids are counted per
/// column, a missing table or DBC skips the check instead of failing it, and the tool's exit code says whether anything dangles.
/// </summary>
public sealed class DbcCrossReferenceTests
{
    [Fact]
    public void DbdForeignKeys_CountDanglingIdsAndRepeatedRows()
    {
        using var temp = new TempDirectory();
        DbdLayout area = ClientDbcDbdLayouts.All["AreaTable.dbc"];
        int continent = area.Columns.Single(c => c.Name == "ContinentID").Offset / 4;
        uint[] first = new uint[area.Fields];
        uint[] second = new uint[area.Fields];
        uint[] third = new uint[area.Fields];
        uint[] unset = new uint[area.Fields];
        first[0] = 1; first[continent] = 1;
        second[0] = 2; second[continent] = 99;
        third[0] = 3; third[continent] = 99;
        unset[0] = 4; unset[continent] = uint.MaxValue;
        File.WriteAllBytes(Path.Combine(temp.Path, "AreaTable.dbc"), SyntheticDbc.Image(area.Fields, area.RecordSize, first, second, third, unset));
        SyntheticDbc.Write(temp.Path, "Map.dbc", rows: 1);

        DbcReferenceResult result = DbcCrossReferences.RunDbc(temp.Path).Single(r => r.Reference.Name == "AreaTable.dbc.ContinentID");
        Assert.Equal(DbcReferenceStatus.Dangling, result.Status);
        Assert.Equal((2, 1, 2L), (result.ReferencedIds, result.DanglingIds, result.DanglingRows));
        Assert.Equal([99L], result.Samples);
    }

    [Fact]
    public void DbdArrayForeignKey_CountsRowsRatherThanRepeatedArrayElements()
    {
        using var temp = new TempDirectory();
        DbdLayout spell = ClientDbcDbdLayouts.All["Spell.dbc"];
        DbdField visual = spell.Columns.Single(c => c.Name == "SpellVisualID");
        Assert.Equal(2, visual.ArrayLength);
        uint[] row = new uint[spell.Fields];
        row[0] = 1;
        row[visual.Offset / 4] = 77;
        row[visual.Offset / 4 + 1] = 77;
        File.WriteAllBytes(Path.Combine(temp.Path, "Spell.dbc"), SyntheticDbc.Image(spell.Fields, spell.RecordSize, row));
        SyntheticDbc.Write(temp.Path, "SpellVisual.dbc", rows: 1);

        DbcReferenceResult result = DbcCrossReferences.RunDbc(temp.Path).Single(r => r.Reference.Name == "Spell.dbc.SpellVisualID");
        Assert.Equal((1, 1, 1L), (result.ReferencedIds, result.DanglingIds, result.DanglingRows));
        Assert.Equal([77L], result.Samples);
    }

    private static string NewWorld(string directory, params string[] statements)
    {
        string path = Path.Combine(directory, "world.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        foreach (string sql in statements)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        return path;
    }

    private static readonly string[] World =
    [
        "CREATE TABLE npc_trainer (entry INTEGER, spell INTEGER, reqskill INTEGER)",
        "INSERT INTO npc_trainer VALUES (1, 1, 0), (1, 2, 0), (2, 2, 0), (3, 99, 0), (4, 99, 0), (5, 0, 0)",
        "CREATE TABLE creature_spawn (Guid INTEGER, MapId INTEGER)",
        "INSERT INTO creature_spawn VALUES (1, 0), (2, 1), (3, 2)",
    ];

    private static readonly DbcReference[] References =
    [
        new("Spell.dbc", "npc_trainer", "spell"),
        new("Map.dbc", "creature_spawn", "MapId"),
        new("Map.dbc", "no_such_table", "MapId"),
        new("Faction.dbc", "npc_trainer", "entry"),
    ];

    [Fact]
    public async Task DanglingIds_AreCountedPerColumn_AndMissingTablesOrDbcsAreSkipped()
    {
        using var temp = new TempDirectory();
        SyntheticDbc.Write(temp.Path, "Spell.dbc", rows: 3); // ids 1, 2, 3
        SyntheticDbc.Write(temp.Path, "Map.dbc", rows: 1);   // id 1 (0 is never checked)
        string world = NewWorld(temp.Path, World);
        await using var connection = new SqliteConnection($"Data Source={world};Mode=ReadOnly;Pooling=False");

        IReadOnlyList<DbcReferenceResult> results = await DbcCrossReferences.RunAsync(connection, temp.Path, References);

        DbcReferenceResult spells = results[0];
        Assert.Equal(DbcReferenceStatus.Dangling, spells.Status);
        Assert.Equal((3, 1, 2L), (spells.ReferencedIds, spells.DanglingIds, spells.DanglingRows));
        Assert.Equal([99L], spells.Samples);
        Assert.Equal((DbcReferenceStatus.Dangling, 1, 1L), (results[1].Status, results[1].DanglingIds, results[1].DanglingRows));
        Assert.Equal(DbcReferenceStatus.Skipped, results[2].Status);
        Assert.StartsWith("not in this database", results[2].Reason, StringComparison.Ordinal);
        Assert.Equal(DbcReferenceStatus.Skipped, results[3].Status);
        Assert.Equal("Faction.dbc missing", results[3].Reason);

        IReadOnlyList<string> lines = DbcCrossReferences.Lines(results);
        Assert.Equal("DBC cross-references: 4 checked, 0 ok, 2 with dangling ids (2 ids, 3 rows), 2 skipped", lines[0]);
        Assert.Contains("npc_trainer.spell -> Spell.dbc: 1 of 3 ids dangling in 2 rows (e.g. 99)", lines);
    }

    [Fact]
    public void EveryReference_NamesADbcWithAReferenceLayout()
    {
        Assert.All(DbcCrossReferences.All, r => Assert.NotNull(ClientDbcLayouts.Find(r.Dbc)));
        Assert.Equal(DbcCrossReferences.All.Count, DbcCrossReferences.All.Select(r => r.Name + r.Dbc).Distinct().Count());
    }

    [Fact]
    public async Task ArcaneDbDbc_ReportsTheDirectoryAndTheDanglingIds_WithTheDriftExitCode()
    {
        using var temp = new TempDirectory();
        foreach (DbcReference reference in DbcCrossReferences.All.DistinctBy(r => r.Dbc))
        {
            SyntheticDbc.Write(temp.Path, reference.Dbc, rows: 3);
        }

        DbdLayout area = ClientDbcDbdLayouts.All["AreaTable.dbc"];
        uint[] areaRow = new uint[area.Fields];
        areaRow[0] = 1;
        areaRow[area.Columns.Single(c => c.Name == "ContinentID").Offset / 4] = 77;
        File.WriteAllBytes(Path.Combine(temp.Path, "AreaTable.dbc"), SyntheticDbc.Image(area.Fields, area.RecordSize, areaRow));

        string world = NewWorld(temp.Path, World);
        var database = new DatabaseOptions { World = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={world}" } };
        using var output = new StringWriter();
        using var error = new StringWriter();

        int code = await DbUpgradeCli.RunAsync(["dbc"], database, output, error, temp.Path, CancellationToken.None);

        Assert.Equal(DbUpgradeExitCodes.Drift, code);
        string text = output.ToString();
        Assert.Contains("  Spell.dbc: loaded 3 records (173 fields, ArcaneCore SpellDbcImporter)", text, StringComparison.Ordinal);
        Assert.Contains("npc_trainer.spell -> Spell.dbc: 1 of 3 ids dangling in 2 rows (e.g. 99)", text, StringComparison.Ordinal);
        Assert.Contains("creature_spawn.MapId -> Map.dbc: ok (2 ids)", text, StringComparison.Ordinal);
        Assert.Contains(DbcCrossReferences.ClientInternalTitle + ": ", text, StringComparison.Ordinal);
        Assert.Contains("AreaTable.dbc.ContinentID -> Map.dbc: 1 of 1 ids dangling in 1 rows (e.g. 77)", text, StringComparison.Ordinal);

        using var json = new StringWriter();
        Assert.Equal(DbUpgradeExitCodes.Drift, await DbUpgradeCli.RunAsync(["dbc", "--dbc-dir", temp.Path, "--json"], database, json, error, null, CancellationToken.None));
        using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
        System.Text.Json.JsonElement spell = document.RootElement.GetProperty("references").EnumerateArray()
            .Single(r => r.GetProperty("table").GetString() == "npc_trainer" && r.GetProperty("column").GetString() == "spell");
        Assert.Equal(1, spell.GetProperty("danglingIds").GetInt32());
        Assert.DoesNotContain(document.RootElement.GetProperty("references").EnumerateArray(), r => r.GetProperty("table").GetString() == "AreaTable.dbc");
        System.Text.Json.JsonElement areaReference = document.RootElement.GetProperty("clientReferences").EnumerateArray()
            .Single(r => r.GetProperty("table").GetString() == "AreaTable.dbc" && r.GetProperty("column").GetString() == "ContinentID");
        Assert.Equal(1, areaReference.GetProperty("danglingIds").GetInt32());
    }

    [Fact]
    public async Task ArcaneDbDbc_ClientInternalDanglingIds_AreReportedButAreNotDrift()
    {
        using var temp = new TempDirectory();
        foreach (DbcReference reference in DbcCrossReferences.All.DistinctBy(r => r.Dbc))
        {
            SyntheticDbc.Write(temp.Path, reference.Dbc, rows: 3);
        }

        DbdLayout area = ClientDbcDbdLayouts.All["AreaTable.dbc"];
        uint[] areaRow = new uint[area.Fields];
        areaRow[0] = 1;
        areaRow[area.Columns.Single(c => c.Name == "ContinentID").Offset / 4] = 77;
        File.WriteAllBytes(Path.Combine(temp.Path, "AreaTable.dbc"), SyntheticDbc.Image(area.Fields, area.RecordSize, areaRow));
        string world = NewWorld(temp.Path, "CREATE TABLE creature_spawn (Guid INTEGER, MapId INTEGER)", "INSERT INTO creature_spawn VALUES (1, 1), (2, 3)");
        var database = new DatabaseOptions { World = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={world}" } };
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(DbUpgradeExitCodes.Ok, await DbUpgradeCli.RunAsync(["dbc", "--dbc-dir", temp.Path], database, output, error, null, CancellationToken.None));
        string text = output.ToString();
        Assert.Contains("creature_spawn.MapId -> Map.dbc: ok (2 ids)", text, StringComparison.Ordinal);
        Assert.Contains("AreaTable.dbc.ContinentID -> Map.dbc: 1 of 1 ids dangling in 1 rows (e.g. 77)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArcaneDbDbc_WithoutADirectory_IsAUsageError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var database = new DatabaseOptions { ConnectionString = "Data Source=unused.db", Provider = DatabaseProvider.Sqlite };

        Assert.Equal(DbUpgradeExitCodes.Usage, await DbUpgradeCli.RunAsync(["dbc"], database, output, error, CancellationToken.None));
        Assert.Contains("no DBC directory", error.ToString(), StringComparison.Ordinal);
    }
}
