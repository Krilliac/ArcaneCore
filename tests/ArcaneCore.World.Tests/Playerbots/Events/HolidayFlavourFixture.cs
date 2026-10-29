using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Playerbots.Events;
using ArcaneCore.World.Tests.Docs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>A store that serves the shipped rows, for the worlds that run the festival code.</summary>
internal sealed class ShippedHolidayFlavourStore : IHolidayFlavourStore
{
    public Task<IReadOnlyList<HolidayFlavourRecord>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(HolidayFlavourFixture.Records);
}

/// <summary>The shipped <c>holiday_flavour</c> rows (<c>sql/content/world/sqlite/0002_holiday_flavour.sql</c>), installed on a clean SQLite world database and read back through the store.</summary>
internal static class HolidayFlavourFixture
{
    private static readonly Lazy<IReadOnlyList<HolidayFlavourRecord>> s_records = new(() => LoadAsync().GetAwaiter().GetResult());

    /// <summary>The shipped rows.</summary>
    internal static IReadOnlyList<HolidayFlavourRecord> Records => s_records.Value;

    /// <summary>The flavours built from the shipped rows.</summary>
    internal static PlayerbotFestivalFlavours Shipped => new(Records);

    internal static async Task<IReadOnlyList<HolidayFlavourRecord>> LoadAsync()
    {
        string script = RepoRoot.ReadText("sql/content/world/sqlite/0002_holiday_flavour.sql");
        string path = Path.Combine(Path.GetTempPath(), $"arcanecore-holiday-flavour-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path};Pooling=False";
        try
        {
            var options = new DbContextOptionsBuilder<ArcaneCore.Data.Content.WorldDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new ArcaneCore.Data.Content.WorldDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            for (int run = 0; run < 2; run++)
            {
                // Twice: the file is idempotent (it deletes its own ids, then inserts them).
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = script;
                await command.ExecuteNonQueryAsync();
            }

            await using var read = new ArcaneCore.Data.Content.WorldDbContext(options);
            return await new EfHolidayFlavourStore(read).LoadAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
