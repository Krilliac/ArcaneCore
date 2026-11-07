using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The game object DB scripts, <c>dbscripts_on_go_use</c> and <c>dbscripts_on_go_template_use</c>, end to end: synthetic rows in the classic-db
/// column layout go through the creature importer into a world SQLite database built from nothing (the schema bootstrapper, so the tables
/// come from the world schema modules a clean install creates), the creature store loads them into the catalog, and the composed world's map
/// runs both scripts. The rows are fixtures, not dump data. Written without a build: unproven until the gate.
/// </summary>
public sealed class GameObjectDbScriptImportWorldTests
{
    private const uint TemplateScript = 50000;
    private const uint GuidScript = 50001;
    private const uint TemplateVariable = 19040;
    private const uint GuidVariable = 19041;

    // SCRIPT_COMMAND_SET_WORLDSTATE (53): dataint is the variable, dataint2 its value.
    private const string Rows = """
        INSERT INTO `dbscripts_on_go_template_use` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`) VALUES
        (50000,0,0,53,0,0,0,0,0,0,19040,1,0,0,0,0,0,0,0,0,0,'template use');
        INSERT INTO `dbscripts_on_go_use` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`) VALUES
        (50001,0,0,53,0,0,0,0,0,0,19041,1,0,0,0,0,0,0,0,0,0,'guid use');
        """;

    private static async Task<CreatureContent> ImportAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcane-goscripts-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var db = new WorldDbContext(options))
            {
                await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
                var importer = new CreatureDumpImporter();
                importer.Read(new StringReader(Rows));
                await importer.WriteAsync(db, replace: false);
            }

            await using var read = new WorldDbContext(options);
            return await new EfCreatureDataStore(read).LoadAsync();
        }
        finally
        {
            File.Delete(path); // Pooling=False: no pooled connection holds the file
        }
    }

    [Fact]
    public async Task ImportedRows_LoadIntoTheCatalogOfEachKind_FromACleanDatabase()
    {
        CreatureContent creatures = await ImportAsync();

        RelayScriptStep template = Assert.Single(creatures.Ai.DbScripts.Get(DbScriptKind.GameObjectTemplate, TemplateScript));
        Assert.Equal((53u, (int)TemplateVariable, 1), (template.Command, template.DataInt, template.DataInt2));
        RelayScriptStep guid = Assert.Single(creatures.Ai.DbScripts.Get(DbScriptKind.GameObject, GuidScript));
        Assert.Equal((53u, (int)GuidVariable, 1), (guid.Command, guid.DataInt, guid.DataInt2));
        Assert.Empty(creatures.Ai.DbScripts.Get(DbScriptKind.GameObject, TemplateScript));
        Assert.Empty(creatures.Ai.DbScripts.Get(DbScriptKind.GameObjectTemplate, GuidScript));
    }

    [Fact]
    public async Task LoadedScripts_RunOnTheMapOfAComposedWorld()
    {
        CreatureContent creatures = await ImportAsync();
        CreatureTestStore.Current.Value = new CreatureTestContext(creatures);
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("GOSCRIPT", "Goscript");
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Goscript")!;
                CreatureMapSystem system = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!);
                Assert.True(system.StartDbScript(DbScriptKind.GameObjectTemplate, TemplateScript, player, null));
                Assert.True(system.StartDbScript(DbScriptKind.GameObject, GuidScript, player, null));
                Assert.Equal(1, ConditionRuntimeState.For(host.World).GetMapVariable(player.Map!, TemplateVariable));
                Assert.Equal(1, ConditionRuntimeState.For(host.World).GetMapVariable(player.Map!, GuidVariable));
            });
        }
    }
}
