using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Battlegrounds;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Data.World.Procs;
using ArcaneCore.Data.World.Rest;
using ArcaneCore.Data.World.Transports;
using ArcaneCore.Data.World.WorldState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// The content importer's <c>refresh</c> command: a world database an older importer built (creatures, portals) gains the tables the
/// current world reads (safe locations, battlegrounds, exploration XP, taverns, transports, proc events, relay scripts, area trigger
/// volumes) without its other tables changing, and a second run leaves the same rows. The rows are real classic-db z2815 tuples
/// (shortened); the DBCs are synthetic files with the build-5875 layouts. Driven in-process against SQLite files in the temp directory.
/// </summary>
public sealed class RefreshCliTests : IDisposable
{
    private const string Dump = """
        INSERT INTO `db_version` (`version`,`creature_ai_version`,`cache_id`,`required_z2815_01_mangos_spawn_group_formation`) VALUES ('Classic DB version 1.12.1 \"Melting Pot v2\". For Classic core z2815.','ACID',NULL,NULL);
        INSERT INTO `world_safe_locs` (`id`,`map`,`x`,`y`,`z`,`o`,`name`) VALUES (610,30,-818.557,-619.255,54.0389,2.02458,'Alterac Valley, Horde Safe'),(611,30,873.002,-491.284,96.5419,3.92699,'Alterac Valley, Alliance Safe'),(769,489,1519.53,1481.87,352.024,3.14159,'Warsong Gulch - Alliance Enter Loc'),(770,489,933.331,1433.72,345.536,0,'Warsong Gulch - Horde Enter Loc');
        INSERT INTO `game_graveyard_zone` (`id`,`ghost_loc`,`link_kind`,`faction`) VALUES (610,2597,0,67);
        INSERT INTO `battleground_template` (`id`,`MinPlayersPerTeam`,`MaxPlayersPerTeam`,`MinLvl`,`MaxLvl`,`AllianceStartLoc`,`HordeStartLoc`,`StartMaxDist`,`PlayerSkinReflootId`) VALUES (1,20,40,51,60,611,610,100,0),(2,5,10,10,60,769,770,75,0);
        INSERT INTO `battlemaster_entry` (`entry`,`bg_template`) VALUES (347,1),(2302,2);
        CREATE TABLE `exploration_basexp` (
          `level` tinyint NOT NULL DEFAULT '0',
          `basexp` mediumint NOT NULL DEFAULT '0',
          PRIMARY KEY (`level`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3 ROW_FORMAT=FIXED COMMENT='Exploration System';
        INSERT INTO `exploration_basexp` VALUES (0,0),(1,5),(60,660);
        INSERT INTO `areatrigger_tavern` (`id`,`name`) VALUES (71,'Westfall - Sentinel Hill Inn'),(562,'Elwynn Forest - Goldshire');
        INSERT INTO `transports` (`entry`,`name`,`period`) VALUES (176231,'Menethil Harbor and Theramore Isle',329313);
        INSERT INTO `spell_proc_event` (`entry`,`SchoolMask`,`SpellFamilyName`,`SpellFamilyMask0`,`SpellFamilyMask1`,`SpellFamilyMask2`,`procFlags`,`procEx`,`ppmRate`,`CustomChance`,`Cooldown`) VALUES (324,0,0,0,0,0,0,65536,0,0,3);
        INSERT INTO `dbscripts_on_relay` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`) VALUES (19958,0,0,32,1,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'pause'),(19958,4000,0,32,0,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'unpause');
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-refresh-" + Guid.NewGuid().ToString("N"));

    public RefreshCliTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private static WorldDbContext Open(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    /// <summary>A world as an older importer left it: one creature template, one portal, everything this command fills empty.</summary>
    private async Task<string> OldWorldAsync(bool withShip = true)
    {
        string path = PathOf("world.db");
        await using WorldDbContext db = Open(path);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1328, Name = "Elly Langston" });
        db.Set<AreaTriggerTeleportRow>().Add(new AreaTriggerTeleportRow { Id = 78, Name = "Deadmines Entrance", TargetMap = 36 });
        if (withShip)
        {
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 176231, Type = 15, Name = "Proudmore's Treasure" });
        }

        await db.SaveChangesAsync();
        return path;
    }

    /// <summary>A WDBC file: the given four-byte fields per record and an empty string block.</summary>
    private string Dbc(string name, int fields, params object[][] rows)
    {
        string directory = PathOf("dbc");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        byte[] strings = Encoding.UTF8.GetBytes("\0Arathi Basin - Alliance Entrance\0");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("WDBC"));
        writer.Write((uint)rows.Length);
        writer.Write((uint)fields);
        writer.Write((uint)(fields * 4));
        writer.Write((uint)strings.Length);
        foreach (object[] row in rows)
        {
            Assert.Equal(fields, row.Length);
            foreach (object value in row)
            {
                switch (value)
                {
                    case float f: writer.Write(f); break;
                    case int i: writer.Write((uint)i); break;
                    default: writer.Write((uint)value); break;
                }
            }
        }

        writer.Write(strings);
        return directory;
    }

    private string Dbcs()
    {
        // AreaTrigger.dbc (id, map, x, y, z, radius, box x/y/z/orientation): the Deadmines portal 78 and the Sentinel Hill and Goldshire inns.
        Dbc("AreaTrigger.dbc", 10,
            [71, 0, -10646.7f, 1105.23f, 35.6f, 15f, 0f, 0f, 0f, 0f],
            [78, 0, -11208.5f, 1685.34f, 25.7612f, 7f, 0f, 0f, 0f, 0f],
            [562, 0, -9462.89f, 15.2f, 56.9f, 0f, 20f, 15f, 10f, 0.5f]);

        // WorldSafeLocs.dbc (id, map, x, y, z, eight names, flags): 611 again (the dump's row with its facing must win) and 890, which the
        // dump lacks (name at string offset 1).
        return Dbc("WorldSafeLocs.dbc", 14,
            [611, 30, 1f, 2f, 3f, 1, 0, 0, 0, 0, 0, 0, 0, 0],
            [890, 529, 1313.9f, 1310.74f, -9.01043f, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
    }

    [Fact]
    public async Task Refresh_FillsTheTablesAnOlderWorldLacks_AndLeavesItsOtherTablesAlone()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("spell_proc_event cooldown unit: seconds", output, StringComparison.Ordinal);
        Assert.Contains("WorldSafeLocs.dbc: 2 row(s), 1 not in the dump added", output, StringComparison.Ordinal);
        Assert.True(output.Contains("refresh: every checked reference resolves", StringComparison.Ordinal), output);
        await using WorldDbContext db = Open(world);
        Assert.Equal([610u, 611u, 769u, 770u, 890u], await db.Set<WorldSafeLocRow>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync());
        WorldSafeLocRow alliance = await db.Set<WorldSafeLocRow>().SingleAsync(r => r.Id == 611);
        Assert.Equal((873.002f, 3.92699f), (alliance.X, alliance.Orientation)); // the dump's row, not the DBC's
        Assert.Equal("Arathi Basin - Alliance Entrance", (await db.Set<WorldSafeLocRow>().SingleAsync(r => r.Id == 890)).Name);
        Assert.Equal(1, await db.Set<GraveyardZoneRow>().CountAsync());
        Assert.Equal(2, await db.Set<BattlegroundTemplateRow>().CountAsync());
        Assert.Equal(2, await db.Set<BattlemasterEntryRow>().CountAsync());
        Assert.Equal(660u, (await db.Set<ExplorationBaseXpRow>().SingleAsync(r => r.Level == 60)).BaseXp);
        Assert.Equal([71u, 562u], await db.Set<AreaTriggerTavernRow>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync());
        TransportRow ship = await db.Set<TransportRow>().SingleAsync();
        Assert.Equal((176231u, (ushort)0, 329313u), (ship.Entry, ship.Build, ship.Period));
        Assert.Equal(3000u, (await new EfSpellProcEventStore(db).LoadAsync()).Find(324)!.Cooldown); // 3 s in z2815
        Assert.Equal(2, await db.Set<RelayScriptRow>().CountAsync(r => r.Id == 19958));
        AreaTriggerTemplateRow inn = await db.Set<AreaTriggerTemplateRow>().SingleAsync(r => r.Id == 562);
        Assert.Equal((20f, 15f, 10f, 0.5f), (inn.BoxX, inn.BoxY, inn.BoxZ, inn.BoxOrientation));
        Assert.Equal(3, await db.Set<AreaTriggerTemplateRow>().CountAsync());

        // The rest of the old world is as it was.
        Assert.Equal([1328u], await db.Set<CreatureTemplateRow>().Select(r => r.Entry).ToListAsync());
        Assert.Equal(1, await db.Set<AreaTriggerTeleportRow>().CountAsync());
    }

    [Fact]
    public async Task Refresh_RunTwice_LeavesTheSameRows()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string dbc = Dbcs();
        Assert.Equal(ExitCodes.Ok, (await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc)).Code);
        string first = await SnapshotAsync(world);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", dbc);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Equal(first, await SnapshotAsync(world));
    }

    private static async Task<string> SnapshotAsync(string world)
    {
        await using WorldDbContext db = Open(world);
        var text = new StringBuilder();
        foreach (WorldSafeLocRow r in await db.Set<WorldSafeLocRow>().OrderBy(r => r.Id).ToListAsync())
        {
            text.Append($"loc {r.Id} {r.MapId} {r.X} {r.Y} {r.Z} {r.Orientation} {r.Name}\n");
        }

        text.Append($"links {await db.Set<GraveyardZoneRow>().CountAsync()} bg {await db.Set<BattlegroundTemplateRow>().CountAsync()} ");
        text.Append($"masters {await db.Set<BattlemasterEntryRow>().CountAsync()} xp {await db.Set<ExplorationBaseXpRow>().CountAsync()} ");
        text.Append($"inns {await db.Set<AreaTriggerTavernRow>().CountAsync()} ships {await db.Set<TransportRow>().CountAsync()} ");
        text.Append($"procs {await db.Set<SpellProcEventRow>().CountAsync()} relay {await db.Set<RelayScriptRow>().CountAsync()} ");
        text.Append($"triggers {await db.Set<AreaTriggerTemplateRow>().CountAsync()} creatures {await db.Set<CreatureTemplateRow>().CountAsync()}");
        return text.ToString();
    }

    [Fact]
    public async Task Refresh_WithoutTheAreaTriggerDbc_NamesThePortalsThatStillHaveNoTrigger()
    {
        string world = await OldWorldAsync(withShip: false);
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world);

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("check: 1 areatrigger_teleport row(s) have no areatrigger_template row: 78", output, StringComparison.Ordinal);
        Assert.Contains("check: 2 areatrigger_tavern row(s) have no areatrigger_template row: 71, 562", output, StringComparison.Ordinal);
        Assert.Contains("check: 1 transports row(s) name no gameobject_template of type 15: 176231", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_DryRun_WritesNothing()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        byte[] before = await File.ReadAllBytesAsync(world);

        (int code, string output, string error) = await RunAsync("refresh", dump, "--database", world, "--dbc-dir", Dbcs(), "--dry-run");

        Assert.True(code == ExitCodes.Ok, error + output);
        Assert.Contains("would replace:", output, StringComparison.Ordinal);
        Assert.Contains("  world_safe_locs  5", output, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(world));
    }

    [Fact]
    public async Task Refresh_RefusesAMissingDatabase_InsteadOfCreatingAnEmptyWorld()
    {
        string dump = PathOf("world.sql");
        File.WriteAllText(dump, Dump);
        string missing = PathOf("no-such-world.db");

        (int code, _, string error) = await RunAsync("refresh", dump, "--database", missing);

        Assert.Equal(ExitCodes.Io, code);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Refresh_ProcEventsWithoutAClassicDbRevision_NeedAnExplicitCooldownUnit()
    {
        string world = await OldWorldAsync();
        string dump = PathOf("procs.sql");
        File.WriteAllText(dump, "INSERT INTO `spell_proc_event` (`entry`,`procFlags`,`Cooldown`) VALUES (324,0,3);\n");

        (int code, _, string error) = await RunAsync("refresh", dump, "--database", world);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--cooldown-unit", error, StringComparison.Ordinal);

        (int explicitCode, string output, string explicitError) = await RunAsync("refresh", dump, "--database", world, "--cooldown-unit", "ms");
        Assert.True(explicitCode == ExitCodes.Ok, explicitError + output);
        await using WorldDbContext db = Open(world);
        Assert.Equal(3u, (await new EfSpellProcEventStore(db).LoadAsync()).Find(324)!.Cooldown);
    }

    [Theory]
    [InlineData("Classic DB version 1.12.1 \"Melting Pot v2\". For Classic core z2815.", ProcCooldownUnit.Seconds)]
    [InlineData("Classic DB version 1.12.1. For Classic core z2829.", ProcCooldownUnit.Milliseconds)]
    [InlineData("Classic DB For Classic core z2900", ProcCooldownUnit.Milliseconds)]
    public void CooldownUnit_Auto_FollowsTheClassicDbRevision(string version, ProcCooldownUnit expected)
        => Assert.Equal(expected, ContentImporterCli.ResolveCooldownUnit(null, version));

    [Fact]
    public void AreaTriggerDbc_WithAnotherLayout_IsRefused()
    {
        string directory = Dbc("AreaTrigger.dbc", 9, [78, 0, 1f, 2f, 3f, 7f, 0f, 0f, 0f]);
        Assert.Throws<InvalidDataException>(() => AreaTriggerDbcReader.Load(Path.Combine(directory, "AreaTrigger.dbc")));
    }
}
