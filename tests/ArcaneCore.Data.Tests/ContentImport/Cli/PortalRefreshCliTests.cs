using ArcaneCore.Data;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

public sealed class PortalRefreshCliTests : IDisposable
{
    private const string Dump = """
        CREATE TABLE `areatrigger_teleport` (`id` mediumint unsigned NOT NULL, `name` text, `required_level` tinyint unsigned NOT NULL DEFAULT '0', `required_item` mediumint unsigned, `required_item2` mediumint unsigned, `required_quest_done` mediumint unsigned, `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, `status_failed_text` text, `condition_id` int, PRIMARY KEY (`id`));
        INSERT INTO `areatrigger_teleport` VALUES (2848,'Onyxias Lair - Entering',50,16309,0,0,249,30.8916,-54.079,-5.02784,4.71239,'Need the amulet.',0),(3528,'The Molten Core Window Entrance',50,0,0,7848,409,1091.89,-466.985,-105.084,3.14159,'Need attunement.',0);
        INSERT INTO `game_tele` (`id`,`position_x`,`position_y`,`position_z`,`orientation`,`map`,`name`) VALUES (99,1,2,3,4,0,'Not imported by portal refresh');
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-portal-refresh-" + Guid.NewGuid().ToString("N"));

    public PortalRefreshCliTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string target = Path.GetFullPath(_directory);
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("arcanecore-portal-refresh-", StringComparison.Ordinal))
            throw new InvalidOperationException("refusing to remove a path outside the portal refresh test directory");
        Directory.Delete(target, recursive: true);
    }

    [Fact]
    public async Task RefreshPortals_ReplacesOnlyPortals_AndIsRepeatable()
    {
        string path = Path.Combine(_directory, "world.db");
        string dump = Path.Combine(_directory, "classic.sql");
        await File.WriteAllTextAsync(dump, Dump);
        await using (WorldDbContext db = Open(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<AreaTriggerTeleportRow>().AddRange(
                new AreaTriggerTeleportRow { Id = 2848, Name = "old", TargetMap = 249 },
                new AreaTriggerTeleportRow { Id = 7777, Name = "old-only", TargetMap = 0 });
            db.Set<GameTeleRow>().Add(new GameTeleRow { Id = 5, Name = "keep" });
            db.Set<AreaTriggerQuestRow>().Add(new AreaTriggerQuestRow { Id = 42, Quest = 7 });
            await db.SaveChangesAsync();
        }

        (int code, string output) = await RunAsync("refresh-portals", dump, "--database", path);
        Assert.Equal(0, code);
        Assert.Contains("2 portal(s) replaced", output, StringComparison.Ordinal);
        await using (WorldDbContext db = Open(path))
        {
            AreaTriggerTeleportRow[] portals = await db.Set<AreaTriggerTeleportRow>().OrderBy(p => p.Id).ToArrayAsync();
            Assert.Equal([2848u, 3528u], portals.Select(p => p.Id));
            Assert.Equal(16309u, portals[0].RequiredItem);
            Assert.Equal(7848u, portals[1].RequiredQuestDone);
            Assert.Equal("keep", Assert.Single(await db.Set<GameTeleRow>().ToArrayAsync()).Name);
            Assert.Equal(7u, Assert.Single(await db.Set<AreaTriggerQuestRow>().ToArrayAsync()).Quest);
        }

        Assert.Equal(0, (await RunAsync("refresh-portals", dump, "--database", path)).Code);
        await using (WorldDbContext db = Open(path))
            Assert.Equal(2, await db.Set<AreaTriggerTeleportRow>().CountAsync());
    }

    [Fact]
    public async Task DryRunAndMissingPortalRows_LeaveExistingWorldUntouched()
    {
        string path = Path.Combine(_directory, "world.db");
        string dump = Path.Combine(_directory, "classic.sql");
        await File.WriteAllTextAsync(dump, Dump);
        await using (WorldDbContext db = Open(path))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<AreaTriggerTeleportRow>().Add(new AreaTriggerTeleportRow { Id = 2848, Name = "old", TargetMap = 249 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, (await RunAsync("refresh-portals", dump, "--database", path, "--dry-run")).Code);
        string empty = Path.Combine(_directory, "empty.sql");
        await File.WriteAllTextAsync(empty, "INSERT INTO `game_tele` (`id`) VALUES (1);");
        Assert.Equal(3, (await RunAsync("refresh-portals", empty, "--database", path)).Code);
        await using (WorldDbContext db = Open(path))
        {
            AreaTriggerTeleportRow old = Assert.Single(await db.Set<AreaTriggerTeleportRow>().ToArrayAsync());
            Assert.Equal("old", old.Name);
            Assert.Equal(0u, old.RequiredItem);
        }
    }

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString() + error.ToString());
    }
}
