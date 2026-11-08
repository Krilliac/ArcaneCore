using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The interrupted-upgrade-step theory runs its faults on clones of a replayed preparation, so a clone must be that preparation: the same definitions and rows as a separate replay,
/// and the same path through the next step. These tests also prove the comparison can fail, because a comparison that sees nothing would pass every clone.
/// </summary>
public sealed class SchemaStartupResilienceCloneTests : SchemaStartupResilienceTestBase
{
    /// <summary>On every provider and for both upgrade lines: the last step, and the first step that adds a column or repairs indexes, which change the preparation.</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            foreach (string component in new[] { "characters", "world" })
            {
                SchemaStep[] steps = [.. SchemaProbe.SchemaOf(component).Steps.Where(s => s.Version > 1)];
                int[] versions =
                [
                    steps[^1].Version,
                    .. steps.Where(s => s.Changes.OfType<AddColumnChange>().Any()).Take(1).Select(s => s.Version),
                    .. steps.Where(s => s.Changes.OfType<EnsureIndexesChange>().Any()).Take(1).Select(s => s.Version),
                ];
                foreach (int version in versions.Distinct())
                {
                    yield return [provider[0], component, version];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Clone_EqualsASeparateReplay_AndTakesTheSamePathThroughTheStep(DatabaseProvider provider, string component, int stepVersion)
    {
        DatabaseConnectionOptions source = await PrepareLegacyAsync(provider, component, stepVersion - 1, stepVersion);
        DatabaseConnectionOptions clone = await _databases.CloneAsync(await TestDatabases.CaptureAsync(source));
        DatabaseConnectionOptions replay = await PrepareLegacyAsync(provider, component, stepVersion - 1, stepVersion);

        await AssertSameDatabaseAsync(replay, clone, $"{provider} {component} before step {stepVersion}");

        var onReplay = new CommandTap();
        var onClone = new CommandTap();
        await EnsureAsync(component, replay, stepVersion, onReplay);
        await EnsureAsync(component, clone, stepVersion, onClone);
        Assert.Equal(onReplay.Commands.Where(CommandTap.IsDdl), onClone.Commands.Where(CommandTap.IsDdl));
        await AssertSameDatabaseAsync(replay, clone, $"{provider} {component} after step {stepVersion}");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Compare_SeesALostRowAndAnExtraColumn_AndAChangedCloneLeavesItsSourceAlone(DatabaseProvider provider)
    {
        DatabaseConnectionOptions source = await PrepareFirstStepAsync(provider);
        TestDatabases.DatabaseImage image = await TestDatabases.CaptureAsync(source);
        IReadOnlyList<string> before = await TestDatabases.DescribeAsync(source);
        Assert.Contains(before, line => line.StartsWith("row ", StringComparison.Ordinal) && line.Contains("Existing", StringComparison.Ordinal));

        DatabaseConnectionOptions lostRow = await _databases.CloneAsync(image);
        await AssertSameDatabaseAsync(source, lostRow, "untouched clone");
        await ExecuteAsync(lostRow, db => $"DELETE FROM {TestContexts.Quote(db, "characters")}");
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => AssertSameDatabaseAsync(source, lostRow, "clone without the row"));

        DatabaseConnectionOptions extraColumn = await _databases.CloneAsync(image);
        await ExecuteAsync(extraColumn, db => $"ALTER TABLE {TestContexts.Quote(db, "characters")} ADD COLUMN clone_probe INT NULL");
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => AssertSameDatabaseAsync(source, extraColumn, "clone with a column"));

        Assert.Equal(before, await TestDatabases.DescribeAsync(source));
    }

    /// <summary>The clone comes from the engine on SQLite and PostgreSQL, so a view is kept; the MariaDB copy is table by table, so it must refuse what it cannot copy instead of losing it.</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Clone_KeepsAView_OrRefusesIt(DatabaseProvider provider)
    {
        DatabaseConnectionOptions source = await PrepareFirstStepAsync(provider);
        await ExecuteAsync(source, _ => "CREATE VIEW clone_probe_view AS SELECT 1 AS one");

        if (provider is DatabaseProvider.MariaDb or DatabaseProvider.MySql)
        {
            NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => TestDatabases.CaptureAsync(source));
            Assert.Contains("clone_probe_view", ex.Message, StringComparison.Ordinal);
            return;
        }

        DatabaseConnectionOptions clone = await _databases.CloneAsync(await TestDatabases.CaptureAsync(source));
        await AssertSameDatabaseAsync(source, clone, "clone with a view");
        Assert.Contains(await TestDatabases.DescribeAsync(clone), line => line.Contains("clone_probe_view", StringComparison.Ordinal));
    }

    private Task<DatabaseConnectionOptions> PrepareFirstStepAsync(DatabaseProvider provider)
    {
        int first = SchemaProbe.SchemaOf("characters").Steps.Where(s => s.Version > 1).Min(s => s.Version);
        return PrepareLegacyAsync(provider, "characters", first - 1, first);
    }

    private static async Task ExecuteAsync(DatabaseConnectionOptions connection, Func<DbContext, string> sql)
    {
        await using DbContext db = SchemaProbe.CreateContext("characters", connection);
        await SchemaProbe.ExecuteAsync(db, sql(db));
    }
}
