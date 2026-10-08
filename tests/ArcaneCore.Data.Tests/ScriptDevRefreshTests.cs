using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The content importer's <c>refresh</c> brings an already imported world up to the ScriptDev2 dungeon scripts of wave 7: their
/// <c>script_texts</c> (and the carried <c>gossip_texts</c> line) in <c>creature_ai_texts</c> and the <c>script_waypoint</c> and
/// <c>waypoint_path</c> copies in <c>creature_movement_template</c>, without touching EventAI texts or ordinary entry paths.
/// </summary>
public sealed class ScriptDevRefreshTests : IAsyncLifetime
{
    private const string Dump = """
        INSERT INTO `script_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`broadcast_text_id`) VALUES
        (-1036000,'Synthetic line',5775,1,0,0,12345);
        INSERT INTO `gossip_texts` (`entry`,`content_default`) VALUES
        (-3090000,'Start the event.'),
        (-3000001,'Not a ported gossip line.');
        INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`) VALUES
        (3849,0,12,-241.15,2154.67,90.62,1.15,2000,0);
        INSERT INTO `waypoint_path` (`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
        (3678,1,-104.288,234.408,-91.6416,1.124,13000,0,NULL);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-sd2-refresh-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Refresh_ReplacesTheScriptDevTextsAndPathCopies_AndLeavesTheRestAlone_Idempotently()
    {
        string world = Path.Combine(_directory, "world.db");
        await using (WorldDbContext db = OpenFile(world))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureAiTextRow>().AddRange(
                new CreatureAiTextRow { Entry = -500, Content = "EventAI line" },
                new CreatureAiTextRow { Entry = -1036000, Content = "stale" });
            db.Set<CreatureMovementTemplateRow>().AddRange(
                new CreatureMovementTemplateRow { Entry = 3849, PathId = 0, Point = 1, X = 1 },
                new CreatureMovementTemplateRow { Entry = 3849, PathId = CreatureContent.ScriptWaypointPathBit, Point = 99, X = 9 });
            await db.SaveChangesAsync();
        }

        string dump = Path.Combine(_directory, "world.sql");
        File.WriteAllText(dump, Dump);
        for (int run = 0; run < 2; run++)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = await ContentImporterCli.RunAsync(["refresh", dump, "--database", world, "--cooldown-unit", "ms"], output, error, CancellationToken.None);
            Assert.True(code == ExitCodes.Ok, error.ToString() + output);
            Assert.Contains("  creature_ai_texts (script_texts, gossip_texts)  2", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("  creature_movement_template (script_waypoint, waypoint_path)  2", output.ToString(), StringComparison.Ordinal);

            await using WorldDbContext db = OpenFile(world);
            CreatureAiTextRow[] texts = [.. (await db.Set<CreatureAiTextRow>().ToListAsync()).OrderBy(t => t.Entry)];
            Assert.Equal([(-3090000, "Start the event."), (-1036000, "Synthetic line"), (-500, "EventAI line")], texts.Select(t => (t.Entry, t.Content)));
            CreatureMovementTemplateRow[] paths = [.. (await db.Set<CreatureMovementTemplateRow>().ToListAsync()).OrderBy(p => p.Entry).ThenBy(p => p.PathId)];
            Assert.Equal([(0u, CreatureContent.WaypointPathBit | 3678u, 1u), (3849u, 0u, 1u), (3849u, CreatureContent.ScriptWaypointPathBit, 12u)],
                paths.Select(p => (p.Entry, p.PathId, p.Point)));
        }
    }

    private static WorldDbContext OpenFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
}
