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
/// Startup-resilience cases for repeated, concurrent and rolled-back starts of a complete schema. Split from <see cref="SchemaStartupResilienceTests"/> so each class stays under the gate's class budget;
/// the setup and helpers are in <see cref="SchemaStartupResilienceTestBase"/>.
/// </summary>
public sealed class SchemaStartupResilienceRestartTests : SchemaStartupResilienceTestBase
{
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
}
