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
/// The other startup-resilience cases (fresh create, empty database, historic create, concurrency, repeated start, rolled-back version). The interrupted-upgrade-step theory, the slow one,
/// runs in the <c>SchemaStartupResilienceUpgradeSlice*Tests</c> classes. Split from the former single class; the setup and helpers are in <see cref="SchemaStartupResilienceTestBase"/>.
/// </summary>
public sealed class SchemaStartupResilienceTests : SchemaStartupResilienceTestBase
{
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

        await RunFaultsAsync(faults, FaultParallelism, async fault =>
        {
            (string label, Func<string, bool> when, int on, bool before) = fault;
            DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
            try
            {
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
            finally
            {
                await _databases.ReleaseAsync(connection);
            }
        });
    }

    [Theory]
    [MemberData(nameof(ProviderComponents))]
    public async Task ExistingEmptyDatabase_StartsLikeAFreshOne(DatabaseProvider provider, string component)
    {
        // The state a startup that died right after CREATE DATABASE leaves: the database exists, nothing in it.
        // (The fault harness does not count CREATE DATABASE as an injectable statement, so this is where that crash point is covered.)
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (DbContext empty = SchemaProbe.CreateContext(component, connection))
        {
            var creator = (IRelationalDatabaseCreator)empty.GetService<IDatabaseCreator>();
            Assert.False(await creator.ExistsAsync(), "the database should not exist yet");
            await creator.CreateAsync();
            Assert.True(await creator.ExistsAsync(), "the database should exist now");
            Assert.False(await creator.HasTablesAsync(), "the database should be empty");
        }

        var tap = new CommandTap();
        await SchemaProbe.EnsureCurrentAsync(component, connection, tap);
        Assert.True(tap.Ddl > 0, component + " issued no DDL against an empty database");
        await AssertConvergedAsync(component, connection, component + " restart over an existing empty database");

        // Same schema as an uninterrupted fresh start of a database that did not exist.
        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync(component, fresh);
        await using DbContext restarted = SchemaProbe.CreateContext(component, connection);
        await using DbContext uninterrupted = SchemaProbe.CreateContext(component, fresh);
        Assert.Equal(
            await SchemaProbe.ActualIndexesAsync(uninterrupted, SchemaProbe.ModelTables(uninterrupted)),
            await SchemaProbe.ActualIndexesAsync(restarted, SchemaProbe.ModelTables(restarted)));
        foreach (string table in SchemaProbe.ModelTables(restarted))
        {
            Assert.Equal(await SchemaProbe.ActualColumnsAsync(uninterrupted, table), await SchemaProbe.ActualColumnsAsync(restarted, table));
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
}
