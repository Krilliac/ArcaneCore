using ArcaneCore.Data;
using ArcaneCore.Data.Content;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

internal static class RealWorldDatabaseCopy
{
    public static async Task<IConfiguration> CreateAsync(string destination)
    {
        File.Copy(Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!, destination);
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={destination};Pooling=False",
        }).Build();

        // Imported-world fixtures can predate the current schema. Upgrade only this temporary copy.
        await using ServiceProvider services = new ServiceCollection().AddLogging().AddWorldDatabase(config).BuildServiceProvider();
        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        return config;
    }

    public static async Task RestoreRaidPortalRequirementsAsync(string destination)
    {
        // ClassicDB_1_12_1_z2815.sql.gz, areatrigger_teleport rows 2848 and 3528.
        // Older imported worlds have the later schema columns but retain their zero defaults.
        await using var db = new SqliteConnection($"Data Source={destination};Pooling=False");
        await db.OpenAsync();
        await RestoreAsync(2848, 249, "RequiredItem", 16309);
        await RestoreAsync(3528, 409, "RequiredQuestDone", 7848);

        async Task RestoreAsync(int id, int targetMap, string column, int required)
        {
            int actual;
            await using (var read = db.CreateCommand())
            {
                read.CommandText = $"SELECT TargetMap, RequiredLevel, {column} FROM areatrigger_teleport WHERE Id = $id";
                read.Parameters.AddWithValue("$id", id);
                await using var rows = await read.ExecuteReaderAsync();
                Assert.True(await rows.ReadAsync(), $"missing imported portal {id}");
                Assert.Equal(targetMap, rows.GetInt32(0));
                Assert.Equal(50, rows.GetInt32(1));
                actual = rows.GetInt32(2);
                Assert.True(actual == 0 || actual == required, $"portal {id} has unexpected {column}={actual}");
            }
            if (actual == required) return;

            await using var update = db.CreateCommand();
            update.CommandText = $"UPDATE areatrigger_teleport SET {column} = $required WHERE Id = $id";
            update.Parameters.AddWithValue("$required", required);
            update.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
    }
}
