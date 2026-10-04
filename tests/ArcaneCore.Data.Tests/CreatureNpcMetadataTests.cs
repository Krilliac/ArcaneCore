using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CreatureNpcMetadataTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task VMangosTrainerColumns_SurviveImportAndTemplateLoad(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `creature_template` (`entry`,`patch`,`name`,`level_min`,`level_max`,`gossip_menu_id`,`trainer_type`,`trainer_class`,`trainer_race`,`trainer_spell`) VALUES
            (900001,0,'Weapon master',10,10,42,2,1,1,201);
            """));
        await importer.WriteAsync(db, replace: false);
        db.ChangeTracker.Clear();

        CreatureTemplateRow row = await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync();
        Assert.Equal((42u, 2u, (byte)1, (byte)1, 201u),
            (row.GossipMenuId, row.TrainerType, row.TrainerClass, row.TrainerRace, row.TrainerSpell));

        var content = await new EfCreatureDataStore(db).LoadAsync();
        Assert.Equal(1, content.TemplateCount);
        var template = content.FindTemplate(900001)!;
        Assert.Equal((42u, 2u, (byte)1, (byte)1, 201u),
            (template.GossipMenuId, template.TrainerType, template.TrainerClass, template.TrainerRace, template.TrainerSpell));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
