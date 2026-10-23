using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The bot scenario tables (<see cref="GameEventPlayerbotDataModule"/>) and the shipped content under <c>sql/content/world</c>: the schema step
/// creates the four tables, the store reads them back in key order, and the Undercity siege content file installs on a clean database and
/// installs again to the same rows (DELETE then INSERT, keyed on the event).
/// </summary>
public sealed class GameEventPlayerbotDataTests : IAsyncLifetime
{
    /// <summary>The event the shipped Undercity siege scenario uses (the custom range 900-999 is reserved for shipped bot scenarios).</summary>
    private const uint UndercityEvent = 900;

    private static readonly string[] s_providers = ["sqlite", "mariadb", "postgresql"];

    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static string ContentFile(string provider)
    {
        string root = RepositorySource.FindRoot() ?? throw new Xunit.Sdk.XunitException("the ArcaneCore checkout was not found");
        return Path.Combine(root, "sql", "content", "world", provider, "0001_undercity_siege.sql");
    }

    [Fact]
    public void WorldStep_IsTheFourScenarioTables()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == GameEventPlayerbotDataModule.Version);
        Assert.Equal(
            ["game_event_playerbot_group", "game_event_playerbot_post", "game_event_playerbot_siege", "game_event_playerbot_waypoint"],
            step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= GameEventPlayerbotDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is GameEventPlayerbotDataModule);
    }

    [Fact]
    public async Task Store_ReadsEveryTable_InKeyOrder()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.AddRange(
                new GameEventPlayerbotGroupRow { Event = 7, GroupId = 2, NamePrefix = "Bb", Count = 3, Team = 1, ClassSpec = "2:1", Level = 60, Provision = true, Role = 1 },
                new GameEventPlayerbotGroupRow { Event = 7, GroupId = 1, NamePrefix = "Aa", Count = 2, Team = 0, ClassSpec = "1:1", Role = 2, Comment = "first" },
                new GameEventPlayerbotPostRow { Event = 7, GroupId = 2, PostId = 2, Map = 1, X = 4, Y = 5, Z = 6, Radius = 40 },
                new GameEventPlayerbotPostRow { Event = 7, GroupId = 2, PostId = 1, Map = 1, X = 1, Y = 2, Z = 3, Radius = 30 },
                new GameEventPlayerbotSiegeRow { Event = 7, GroupId = 1, ObjectiveEntry = 10181, Raids = 2, MinReadyPct = 90 },
                new GameEventPlayerbotWaypointRow { Event = 7, GroupId = 1, Seq = 2, Map = 0, X = 2, Y = 2, Z = 2 },
                new GameEventPlayerbotWaypointRow { Event = 7, GroupId = 1, Seq = 1, Map = 0, X = 1, Y = 1, Z = 1 });
            await db.SaveChangesAsync();
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        GameEventPlayerbotContent content = await new EfGameEventPlayerbotStore(read).LoadAsync();
        Assert.Equal([1u, 2u], content.Groups.Select(g => g.GroupId));
        Assert.Equal("first", content.Groups[0].Comment);
        Assert.Equal((3, (byte)1, "2:1", (byte)60, true), (content.Groups[1].Count, content.Groups[1].Team, content.Groups[1].ClassSpec, content.Groups[1].Level, content.Groups[1].Provision));
        Assert.Equal([1u, 2u], content.Posts.Select(p => p.PostId));
        Assert.Equal((10181u, (byte)2, (byte)90), (content.Sieges[0].ObjectiveEntry, content.Sieges[0].Raids, content.Sieges[0].MinReadyPct));
        Assert.Equal([1u, 2u], content.Waypoints.Select(w => w.Seq));
    }

    [Fact]
    public async Task UndercityContent_InstallsOnACleanDatabase_AndAgainToTheSameRows()
    {
        string script = await File.ReadAllTextAsync(ContentFile("sqlite"));
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        (int Events, int Groups, int Posts, int Sieges, int Waypoints) first = await Run(cs, script);
        Assert.Equal((1, 6, 5, 1, 10), first);
        (int Events, int Groups, int Posts, int Sieges, int Waypoints) second = await Run(cs, script);
        Assert.Equal(first, second);

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        GameEventPlayerbotContent content = await new EfGameEventPlayerbotStore(read).LoadAsync();
        Assert.All(content.Groups, g => Assert.Equal(UndercityEvent, g.Event));
        Assert.Equal(["Siegebot", "Ucltop", "Uclbot", "Uccanal", "Ucgate", "Ucroyal"], content.Groups.OrderBy(g => g.GroupId).Select(g => g.NamePrefix));
        Assert.Equal(80, content.Groups.Single(g => g.NamePrefix == "Siegebot").Count);
        Assert.All(content.Groups.Where(g => g.NamePrefix != "Siegebot"), g => Assert.Equal((10, (byte)1, (byte)60, (byte)1), (g.Count, g.Team, g.Level, g.Role)));
    }

    [Fact]
    public async Task UndercityContent_LeavesOtherEventsRowsAlone()
    {
        string script = await File.ReadAllTextAsync(ContentFile("sqlite"));
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new GameEventPlayerbotGroupRow { Event = 901, GroupId = 1, NamePrefix = "Other", Count = 1, ClassSpec = "1:1" });
            await db.SaveChangesAsync();
        }

        await Run(cs, script);
        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        Assert.Equal(1, await read.Set<GameEventPlayerbotGroupRow>().CountAsync(g => g.Event == 901));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("mariadb")]
    [InlineData("postgresql")]
    public async Task EveryProviderShipsTheContent_ThatClearsEveryTableBeforeInserting(string provider)
    {
        Assert.Contains(provider, s_providers);
        string script = await File.ReadAllTextAsync(ContentFile(provider));
        foreach (string table in new[] { "game_event_playerbot_waypoint", "game_event_playerbot_siege", "game_event_playerbot_post", "game_event_playerbot_group", "game_event" })
        {
            int delete = IndexOfDelete(script, table);
            int insert = script.IndexOf("INSERT INTO " + Quote(provider, table), StringComparison.Ordinal);
            Assert.True(delete >= 0, $"{provider}: no DELETE FROM {table}");
            Assert.True(insert > delete, $"{provider}: INSERT INTO {table} must follow its DELETE");
        }
    }

    private static int IndexOfDelete(string script, string table)
    {
        foreach (string quote in new[] { "\"", "`", string.Empty })
        {
            int at = script.IndexOf($"DELETE FROM {quote}{table}{quote}", StringComparison.Ordinal);
            if (at >= 0) return at;
        }

        return -1;
    }

    private static string Quote(string provider, string table) => provider == "mariadb" ? $"`{table}`" : $"\"{table}\"";

    private static async Task<(int Events, int Groups, int Posts, int Sieges, int Waypoints)> Run(DatabaseConnectionOptions cs, string script)
    {
        await using var connection = new SqliteConnection(cs.ConnectionString);
        await connection.OpenAsync();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = script;
            await command.ExecuteNonQueryAsync();
        }

        return (await Count(connection, "game_event"), await Count(connection, "game_event_playerbot_group"), await Count(connection, "game_event_playerbot_post"),
            await Count(connection, "game_event_playerbot_siege"), await Count(connection, "game_event_playerbot_waypoint"));
    }

    private static async Task<int> Count(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE {(table == "game_event" ? "\"entry\"" : "\"event\"")} = {UndercityEvent}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
