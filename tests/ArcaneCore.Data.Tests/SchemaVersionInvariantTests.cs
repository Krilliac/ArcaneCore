using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The version-row invariants of the bootstrapper (docs/ops/invariants.md): a version below 0 is a damaged
/// table and the start fails closed on it, counted as an invariant failure. SQLite only: the check runs
/// before any provider-specific statement.
/// </summary>
public sealed class SchemaVersionInvariantTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    [Fact]
    public async Task NegativeVersionRow_FailsClosed_AndIsCounted()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (var db = new WidgetContext(TestContexts.Options<WidgetContext>(cs)))
        {
            await SchemaBootstrapper.EnsureAsync(db, WidgetContext.Schema);
            SchemaVersionRow row = await db.Set<SchemaVersionRow>().SingleAsync();
            Assert.Equal(1, row.Version);
            row.Version = -1;
            await db.SaveChangesAsync();
        }

        // Invariant.FailureCount is process-wide and xunit runs other test classes of this assembly in parallel,
        // so the exact count is taken from a capture scoped to this test's call flow (it follows the awaits).
        using InvariantCapture capture = Invariant.Capture();
        await using (var db = new WidgetContext(TestContexts.Options<WidgetContext>(cs)))
        {
            SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaBootstrapper.EnsureAsync(db, WidgetContext.Schema));
            Assert.Contains("damaged", ex.Message, StringComparison.Ordinal);
            Assert.Contains("-1", ex.Message, StringComparison.Ordinal);
        }

        InvariantFailureEvent failure = Assert.Single(capture.Failures);
        Assert.Equal("Check", failure.Kind);
        Assert.Equal("SchemaBootstrapper.cs", failure.File);
        Assert.Contains("holds version -1", failure.Message, StringComparison.Ordinal);
        Assert.Contains(Invariant.Failures(), f => f.File == "SchemaBootstrapper.cs" && f.LastMessage!.Contains("holds version -1", StringComparison.Ordinal));

        // Nothing was changed by the refused start.
        await using (var db = new WidgetContext(TestContexts.Options<WidgetContext>(cs)))
        {
            Assert.Equal(-1, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private sealed class Widget
    {
        public int Id { get; set; }
    }

    private sealed class WidgetContext(DbContextOptions<WidgetContext> options) : DbContext(options)
    {
        public static readonly SchemaDefinition Schema = new()
        {
            Component = "invariant_widgets",
            CurrentVersion = 1,
            Version1Tables = ["invariant_widget"],
        };

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
            modelBuilder.Entity<Widget>(e =>
            {
                e.ToTable("invariant_widget");
                e.Property(w => w.Id).ValueGeneratedNever();
            });
        }
    }
}
