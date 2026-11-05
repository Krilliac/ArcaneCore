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
/// Startup-resilience cases for a database that already exists: one that holds nothing (the state a start that died right after CREATE DATABASE leaves) and one left by the historic fresh create (every table, no
/// version row). Split from <see cref="SchemaStartupResilienceTests"/> so each class stays under the gate's class budget; the setup and helpers are in <see cref="SchemaStartupResilienceTestBase"/>.
/// </summary>
public sealed class SchemaStartupResilienceExistingDatabaseTests : SchemaStartupResilienceTestBase
{
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
}
