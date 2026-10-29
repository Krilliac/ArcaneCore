using System.Globalization;
using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Tuning;
using ArcaneCore.Kernel.Tuning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <c>sql/content/world/&lt;provider&gt;/0002_tuning_tables.sql</c> carries the rows of <see cref="TuningDefaults"/> for a manual install. The
/// files are generated from the defaults (<c>ARCANECORE_UPDATE_SQL=1</c> rewrites them, as for <c>sql/updates</c>), kept equal by a golden
/// comparison, and the SQLite file is applied twice to a clean world database: the rows are the defaults and nothing is duplicated.
/// </summary>
public sealed class TuningContentSqlTests : IAsyncLifetime
{
    private const string FileName = "0002_tuning_tables.sql";

    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ArcaneCore.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string PathOf(string provider) => System.IO.Path.Combine(Root(), "sql", "content", "world", provider, FileName);

    [Theory]
    [InlineData("sqlite")]
    [InlineData("mariadb")]
    [InlineData("postgresql")]
    public void ContentFile_IsTheGeneratedOneOfTheDefaults(string provider)
    {
        string expected = Generate(provider);
        if (Environment.GetEnvironmentVariable("ARCANECORE_UPDATE_SQL") == "1")
        {
            File.WriteAllText(PathOf(provider), expected, new UTF8Encoding(false));
        }

        Assert.Equal(expected, File.ReadAllText(PathOf(provider)).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SqliteFile_AppliedTwice_LeavesTheDefaultRowsOnce()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            string script = File.ReadAllText(PathOf("sqlite"));
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();
            for (int pass = 0; pass < 2; pass++)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = script;
                await command.ExecuteNonQueryAsync();
            }
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        TuningContent content = await new EfTuningContentStore(read).LoadAsync();
        Assert.Equal(TuningDefaults.Content.RowCount, content.RowCount);
        Assert.Equal(TuningDefaults.Content.Values, content.Values);
        Assert.Equal(TuningDefaults.Content.Loot.OrderBy(r => r.Source, StringComparer.Ordinal), content.Loot);
        Assert.Equal(TuningDefaults.Content.Ships.OrderBy(r => r.Entry), content.Ships);
        Assert.Equal(TuningDefaults.Content.Elevators, content.Elevators);
        Assert.Equal(TuningDefaults.Content.Trams, content.Trams);
    }

    private static string Generate(string provider)
    {
        bool maria = provider == "mariadb";
        string Q(string name) => maria ? $"`{name}`" : $"\"{name}\"";
        string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        string S(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        TuningContent c = TuningDefaults.Content;
        var sb = new StringBuilder();
        sb.Append("-- ArcaneCore content: the tuning tables' shipped rows (auction bot value and loot matrices, bot transport networks). Idempotent: deletes the rows of the shipped keys, then inserts them.\n");
        sb.Append("-- Needs the world schema with the tuning tables (sql/content/README.md). Generated from TuningDefaults by TuningContentSqlTests; do not edit by hand.\n");
        sb.Append($"-- provider: {provider}\n\n");
        sb.Append(maria ? "START TRANSACTION;\n\n" : "BEGIN;\n\n");

        sb.Append($"DELETE FROM {Q("auction_bot_value")};\n");
        sb.Append($"DELETE FROM {Q("auction_bot_loot")} WHERE {Q("source")} IN ({string.Join(", ", c.Loot.Select(r => S(r.Source)))});\n");
        sb.Append($"DELETE FROM {Q("playerbot_ship_route")} WHERE {Q("entry")} IN ({string.Join(", ", c.Ships.Select(r => r.Entry))});\n");
        sb.Append($"DELETE FROM {Q("playerbot_elevator_region")} WHERE {Q("id")} IN ({string.Join(", ", c.Elevators.Select(r => r.Id))});\n");
        sb.Append($"DELETE FROM {Q("playerbot_tram_network")} WHERE {Q("map_id")} IN ({string.Join(", ", c.Trams.Select(r => r.MapId))});\n\n");

        sb.Append($"INSERT INTO {Q("auction_bot_value")} ({Q("quality")}, {Q("item_class")}, {Q("percent")}) VALUES\n");
        sb.Append(string.Join(",\n", c.Values.Select(r => $"({r.Quality}, {r.ItemClass}, {r.Percent})"))).Append(";\n\n");

        sb.Append($"INSERT INTO {Q("auction_bot_loot")} ({Q("source")}, {Q("min_templates")}, {Q("max_templates")}, {Q("min_rolls")}, {Q("max_rolls")}) VALUES\n");
        sb.Append(string.Join(",\n", c.Loot.Select(r => $"({S(r.Source)}, {r.MinTemplates}, {r.MaxTemplates}, {r.MinRolls}, {r.MaxRolls})"))).Append(";\n\n");

        sb.Append($"INSERT INTO {Q("playerbot_ship_route")} ({Q("entry")}, {Q("means")}, {Q("access")}, {Q("name")}) VALUES\n");
        sb.Append(string.Join(",\n", c.Ships.Select(r => $"({r.Entry}, {r.Means}, {r.Access}, {S(r.Name)})"))).Append(";\n\n");

        sb.Append($"INSERT INTO {Q("playerbot_elevator_region")} ({Q("id")}, {Q("map_id")}, {Q("x")}, {Q("y")}, {Q("low_zone")}, {Q("low_area")}, {Q("high_zone")}, {Q("high_area")}, {Q("mesa_top")}, {Q("vault_low")}, {Q("name")}) VALUES\n");
        sb.Append(string.Join(",\n", c.Elevators.Select(r =>
            $"({r.Id}, {r.MapId}, {F(r.X)}, {F(r.Y)}, {r.LowZone}, {r.LowArea}, {r.HighZone}, {r.HighArea}, {(r.MesaTop ? 1 : 0)}, {(r.VaultLow ? 1 : 0)}, {S(r.Name)})"))).Append(";\n\n");

        sb.Append($"INSERT INTO {Q("playerbot_tram_network")} ({Q("map_id")}, {Q("pair_yards")}) VALUES\n");
        sb.Append(string.Join(",\n", c.Trams.Select(r => $"({r.MapId}, {F(r.PairYards)})"))).Append(";\n\n");

        sb.Append("COMMIT;\n");
        return sb.ToString();
    }
}
