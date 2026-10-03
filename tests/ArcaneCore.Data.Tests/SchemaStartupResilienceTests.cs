using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Repeated, interrupted and concurrent startup. DDL on MariaDB is not transactional, so a
/// startup that dies halfway leaves a half-applied step; the next startup must converge on the
/// same schema as an uninterrupted one. A start-up race between the realm and the world daemon
/// (both run the auth bootstrap) must not corrupt or fail either.
/// </summary>
public sealed class SchemaStartupResilienceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    /// <summary>Every (provider, component, step) of the characters and world upgrade lines.</summary>
    public static IEnumerable<object[]> UpgradeSteps()
    {
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            foreach (SchemaStep step in CharacterDbContext.Schema.Steps)
            {
                yield return [provider[0], "characters", step.Version];
            }

            foreach (SchemaStep step in WorldDbContext.Schema.Steps)
            {
                yield return [provider[0], "world", step.Version];
            }
        }
    }

    public static IEnumerable<object[]> ProviderComponents()
    {
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            foreach (object[] component in SchemaProbe.Components())
            {
                yield return [provider[0], component[0]];
            }
        }
    }

    /// <summary>Fresh databases for every component on every provider; the SQLite-only populated baseline for the two components with upgrades.</summary>
    public static IEnumerable<object[]> ConcurrentCases()
    {
        foreach (object[] pc in ProviderComponents())
        {
            yield return [pc[0], false, pc[1]];
        }

        foreach (string component in new[] { "characters", "world" })
        {
            yield return [DatabaseProvider.Sqlite, true, component];
        }
    }

    [Theory]
    [MemberData(nameof(UpgradeSteps))]
    public async Task InterruptedUpgradeStep_ResumesOnRestart(DatabaseProvider provider, string component, int stepVersion)
    {
        // How many DDL statements does this step issue? Measured, not assumed.
        DatabaseConnectionOptions probe = await PrepareLegacyAsync(provider, component, stepVersion - 1, stepVersion);
        var counter = new CommandTap();
        await EnsureAsync(component, probe, stepVersion, counter);
        int ddl = counter.Ddl;
        Assert.True(ddl > 0, $"{component} step {stepVersion} issued no DDL");

        Func<string, bool> isVersionWrite = sql => sql.Contains(component + "_schema", StringComparison.Ordinal)
            && CommandTap.IsWrite(sql, "UPDATE", "INSERT");

        var faults = new List<(string Label, Func<string, bool> When, int On)>();
        for (int k = 1; k <= ddl; k++)
        {
            faults.Add(($"after DDL #{k}", CommandTap.IsDdl, k));
        }

        faults.Add(("after the version write", isVersionWrite, 1));

        foreach ((string label, Func<string, bool> when, int on) in faults)
        {
            DatabaseConnectionOptions connection = await PrepareLegacyAsync(provider, component, stepVersion - 1, stepVersion);
            var tap = new CommandTap(when, on);
            await AssertInjectedAsync(() => EnsureAsync(component, connection, stepVersion, tap), $"{provider} {component} step {stepVersion} {label} (probe saw {ddl} DDL)", tap);

            // A restart under the current definition must reach the same schema as an uninterrupted start.
            try
            {
                await SchemaProbe.EnsureCurrentAsync(component, connection);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"{component} step {stepVersion} interrupted {label}: restart failed: {ex.GetType().Name}: {ex.Message}");
            }

            await AssertConvergedAsync(component, connection, $"{component} step {stepVersion} {label}");
        }
    }

    [Theory]
    [MemberData(nameof(ProviderComponents))]
    public async Task InterruptedFreshCreate_ResumesOnRestart(DatabaseProvider provider, string component)
    {
        var counter = new CommandTap();
        await SchemaProbe.EnsureCurrentAsync(component, await _databases.CreateAsync(provider), counter);
        int ddl = counter.Ddl;
        Assert.True(ddl > 0);

        // Die after each DDL statement, and just before the statement that records the version
        // (the crash the historic bootstrapper left an empty version table for).
        Func<string, bool> isVersionInsert = sql => sql.Contains(component + "_schema", StringComparison.Ordinal)
            && CommandTap.IsWrite(sql, "INSERT");
        var faults = new List<(string Label, Func<string, bool> When, int On, bool Before)>();
        for (int k = 1; k <= ddl; k++)
        {
            faults.Add(($"after DDL #{k}", CommandTap.IsDdl, k, false));
        }

        faults.Add(("before the version write", isVersionInsert, 1, true));

        foreach ((string label, Func<string, bool> when, int on, bool before) in faults)
        {
            DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
            var tap = new CommandTap(when, on, faultBeforeStatement: before);
            await AssertInjectedAsync(() => SchemaProbe.EnsureCurrentAsync(component, connection, tap), $"{provider} {component} fresh create {label} (probe saw {ddl} DDL)", tap);
            try
            {
                await SchemaProbe.EnsureCurrentAsync(component, connection);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"{component} fresh create interrupted {label}: restart failed: {ex.GetType().Name}: {ex.Message}");
            }

            await AssertConvergedAsync(component, connection, $"{component} fresh create {label}");
        }
    }

    [Theory]
    [MemberData(nameof(ProviderComponents))]
    public async Task DatabaseLeftByHistoricFreshCreate_WithNoVersionRow_ResumesOnRestart(DatabaseProvider provider, string component)
    {
        // What the historic bootstrapper left when it died between CreateTables and the version
        // write: every table, an empty version table.
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (DbContext db = SchemaProbe.CreateContext(component, connection))
        {
            await TestContexts.CreateTablesAsync(db);
            Assert.Empty(await db.Set<SchemaVersionRow>().AsNoTracking().ToListAsync());
        }

        await SchemaProbe.EnsureCurrentAsync(component, connection);
        await AssertConvergedAsync(component, connection, component + " legacy empty version table");
    }

    [Theory]
    [MemberData(nameof(ConcurrentCases))]
    public async Task ConcurrentStartup_SameComponent_Serializes(DatabaseProvider provider, bool populatedBaseline, string component)
    {
        DatabaseConnectionOptions connection;
        if (populatedBaseline)
        {
            connection = await _databases.CreateAsync(provider);
            await CandidateBaseline.CreateAsync(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection.ConnectionString).DataSource,
                CandidateBaseline.Variant.HistoricUpgraded);
        }
        else
        {
            connection = await _databases.CreateAsync(provider);
        }

        var barrier = new StartupBarrier(2, TimeSpan.FromSeconds(1.5));
        var first = new CommandTap(barrier: barrier);
        var second = new CommandTap(barrier: barrier);
        await Task.WhenAll(
            Task.Run(() => SchemaProbe.EnsureCurrentAsync(component, connection, first)),
            Task.Run(() => SchemaProbe.EnsureCurrentAsync(component, connection, second)));

        await AssertConvergedAsync(component, connection, component + " concurrent");
        await using DbContext db = SchemaProbe.CreateContext(component, connection);
        Assert.Single(await db.Set<SchemaVersionRow>().AsNoTracking().ToListAsync());

        // One process did the work; the other found it done and issued no DDL.
        Assert.Equal(1, new[] { first, second }.Count(t => t.Ddl > 0));
    }

    [Theory]
    [MemberData(nameof(ProviderComponents))]
    public async Task RepeatedStartup_IssuesNoDdlAfterTheFirst(DatabaseProvider provider, string component)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        for (int pass = 1; pass <= 3; pass++)
        {
            var tap = new CommandTap();
            await SchemaProbe.EnsureCurrentAsync(component, connection, tap);
            Assert.Equal(pass == 1, tap.Ddl > 0);
        }

        await AssertConvergedAsync(component, connection, component + " repeated");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RolledBackVersion_OnCompleteDatabase_ConvergesWithoutLosingRows(DatabaseProvider provider)
    {
        // The state a newer-schema database is in when an older binary's step list is replayed
        // over it (and what a crashed upgrade of a non-transactional engine looks like): every
        // table exists, the columns later steps add are missing, the version row is behind.
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        foreach (string component in new[] { "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, connection);
        }

        await using (CharacterDbContext chars = TestContexts.Create<CharacterDbContext>(connection))
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(connection))
        {
            chars.Characters.Add(new CharacterRecord { AccountId = 1, Name = "Kept" });
            world.ClassInfo.Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
            await chars.SaveChangesAsync();
            await world.SaveChangesAsync();

            foreach (AddColumnChange add in WorldDbContext.Schema.Steps.SelectMany(s => s.Changes).OfType<AddColumnChange>())
            {
                await SchemaProbe.ExecuteAsync(world, $"ALTER TABLE {TestContexts.Quote(world, add.Table)} DROP COLUMN {TestContexts.Quote(world, add.Column)}");
            }

            await SchemaProbe.ExecuteAsync(chars, $"UPDATE {TestContexts.Quote(chars, "characters_schema")} SET {TestContexts.Quote(chars, "Version")} = 2");
            await SchemaProbe.ExecuteAsync(world, $"UPDATE {TestContexts.Quote(world, "world_schema")} SET {TestContexts.Quote(world, "Version")} = 1");
        }

        foreach (string component in new[] { "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, connection);
            await AssertConvergedAsync(component, connection, component + " rolled back");
        }

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(connection);
        await using WorldDbContext afterWorld = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal("Kept", (await after.Characters.SingleAsync()).Name);
        Assert.Equal(60u, (await afterWorld.ClassInfo.SingleAsync()).BaseHealth);
    }

    [Fact]
    public void Shorten_TakesTheLengthAfterLineEndingsShrink()
    {
        // "\r\n" becomes one space, so the replaced text is shorter than the original: slicing it with the
        // original length throws ArgumentOutOfRangeException and hides the failure being reported.
        Assert.Equal("a b", Shorten("a\r\nb", 90));
        Assert.Equal("a b c", Shorten("a\r\nb\r\nc", 90));
        Assert.Equal("a b", Shorten("a\r\nb\r\ncdefgh", 3));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    /// <summary>The startup must die of the injected fault (EF wraps it in a DbUpdateException for SaveChanges), not of anything else.</summary>
    private static async Task AssertInjectedAsync(Func<Task> startup, string label, CommandTap tap)
    {
        Exception? thrown = null;
        try
        {
            await startup();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (thrown is null)
        {
            throw new Xunit.Sdk.XunitException(
                $"{label}: startup completed without the injected fault. EF commands seen ({tap.Commands.Count}): " +
                string.Join(" || ", tap.Commands.Select(c => Shorten(c, 90))));
        }

        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is InjectedFaultException)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("startup failed, but not of the injected fault: " + thrown);
    }

    /// <summary>One line, at most <paramref name="max"/> characters. The length is taken after the line endings are replaced: they can change it.</summary>
    internal static string Shorten(string text, int max)
    {
        string line = text.ReplaceLineEndings(" ");
        return line[..Math.Min(max, line.Length)];
    }

    private static async Task AssertConvergedAsync(string component, DatabaseConnectionOptions connection, string label)
    {
        await using DbContext db = SchemaProbe.CreateContext(component, connection);
        Assert.Equal(SchemaProbe.SchemaOf(component).CurrentVersion, (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version);
        await SchemaProbe.AssertIndexParityAsync(db, label);
        await SchemaProbe.AssertColumnParityAsync(db, label);
    }

    /// <summary>Run one prefix of the current definition (versions up to <paramref name="version"/>) through a context carrying <paramref name="tap"/>.</summary>
    private static async Task EnsureAsync(string component, DatabaseConnectionOptions connection, int version, CommandTap tap)
    {
        SchemaDefinition prefix = SchemaProbe.ThroughVersion(SchemaProbe.SchemaOf(component), version);
        await using DbContext db = component == "characters"
            ? SchemaProbe.CreateWith<CharacterDbContext>(connection, tap)
            : SchemaProbe.CreateWith<WorldDbContext>(connection, tap);
        await SchemaBootstrapper.EnsureAsync(db, prefix);
    }

    /// <summary>A database of <paramref name="component"/> at <paramref name="version"/>, started from its historical version-1 model.</summary>
    /// <param name="provider">The engine.</param>
    /// <param name="component">characters or world.</param>
    /// <param name="version">The version the database is left at.</param>
    /// <param name="nextStep">The step about to run. When it only repairs indexes, the indexes it repairs are dropped first: a database prepared by the current bootstrapper would not need the repair. The columns it adds are dropped first for the same reason.</param>
    private async Task<DatabaseConnectionOptions> PrepareLegacyAsync(DatabaseProvider provider, string component, int version, int nextStep)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        if (component == "characters")
        {
            await using (CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection))
            {
                await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
                legacy.Characters.Add(new CharacterV1Row { AccountId = 7, Name = "Existing", PlayedTime = 123 });
                await legacy.SaveChangesAsync();
            }
        }
        else
        {
            await using MapWorldV1Context legacy = TestContexts.Create<MapWorldV1Context>(connection);
            await SchemaBootstrapper.EnsureAsync(legacy, MapWorldV1Context.Schema);
            legacy.Set<ClassInfoRow>().Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
            await legacy.SaveChangesAsync();
        }

        if (version > 1)
        {
            await EnsureAsync(component, connection, version, new CommandTap());
        }

        SchemaStep next = SchemaProbe.SchemaOf(component).Steps.Single(s => s.Version == nextStep);
        foreach (EnsureIndexesChange repair in next.Changes.OfType<EnsureIndexesChange>())
        {
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            await SchemaProbe.DropIndexesAsync(db, repair.Table);
        }

        // A table created by an earlier step of this harness already has the columns the current model gives it,
        // including those a later step adds. A real database upgraded across that step lacks them, so remove them.
        foreach (AddColumnChange add in next.Changes.OfType<AddColumnChange>())
        {
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            if (await SchemaCatalog.ColumnExistsAsync(db, add.Table, add.Column))
            {
                await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, add.Table)} DROP COLUMN {TestContexts.Quote(db, add.Column)}");
            }
        }

        return connection;
    }
}
