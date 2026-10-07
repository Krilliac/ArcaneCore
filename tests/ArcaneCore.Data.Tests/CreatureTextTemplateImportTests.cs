using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CreatureTextTemplateImportTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    private static CreatureDumpImporter Import(string rows)
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("INSERT INTO `dbscript_random_templates` (`id`,`type`,`target_id`,`chance`) VALUES " + rows + ";"));
        return importer;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportKeepsSignedTextWeightedChoicesAndIgnoresRelayTemplates(DatabaseProvider provider)
    {
        var connection = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        var report = await Import("(1,0,-2,25),(1,0,991,0),(1,1,42,0),(2,0,0,0)").WriteAsync(db, false);
        Assert.Equal(3, report.AiTextTemplates);
        var content = await new EfCreatureDataStore(db).LoadAsync();
        Assert.Equal(-2, content.Ai.SelectTemplateText(1, 25, _ => 0));
        Assert.Equal(991, content.Ai.SelectTemplateText(1, 26, _ => 0));
        Assert.Equal(0, content.Ai.SelectTemplateText(2, 100, _ => 0));
        Assert.Equal(3, await db.Set<CreatureTextTemplateRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ReplaceRemovesOldTemplatesAndFailedAppendPreservesExistingContent(DatabaseProvider provider)
    {
        var connection = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        await Import("(1,0,-1,0)").WriteAsync(db, false);
        await Assert.ThrowsAsync<DbUpdateException>(() => Import("(1,0,-1,0),(2,0,-2,0)").WriteAsync(db, false));
        Assert.Single(await db.Set<CreatureTextTemplateRow>().AsNoTracking().ToListAsync());
        await Import("(2,0,-2,0)").WriteAsync(db, true);
        Assert.Equal(2u, (await db.Set<CreatureTextTemplateRow>().SingleAsync()).Id);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
