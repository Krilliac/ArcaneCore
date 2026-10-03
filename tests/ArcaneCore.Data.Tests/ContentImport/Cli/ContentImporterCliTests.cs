using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

/// <summary>
/// The content importer command line, driven in-process against SQLite databases in the temp
/// directory (never in a repository). All dumps and DBCs are synthetic and hand-written.
/// </summary>
public sealed class ContentImporterCliTests : IDisposable
{
    // cmangos classic-db z2815 column layout (trimmed): ModelId1/MinLevel, spawnMask, spawntimesecsmin/max.
    private const string CMangosDump = """
        -- synthetic cmangos-layout dump
        CREATE TABLE `db_version` (`version` varchar(120));
        INSERT INTO `db_version` VALUES ('Synthetic DB 1');
        CREATE TABLE `creature_template` (`Entry` int unsigned NOT NULL, `Name` varchar(100), `MinLevel` tinyint, `MaxLevel` tinyint, `ModelId1` int, `TrainerType` int, `LootId` int, PRIMARY KEY (`Entry`));
        INSERT INTO `creature_template` VALUES (1001,'Wolf',1,2,100,0,5),(1002,'Boar',2,3,101,0,0);
        CREATE TABLE `creature` (`guid` int unsigned NOT NULL, `id` int unsigned NOT NULL, `map` int, `spawnMask` tinyint, `position_x` float, `position_y` float, `position_z` float, `orientation` float, `spawntimesecsmin` int, `spawntimesecsmax` int, `spawndist` float, `MovementType` tinyint, PRIMARY KEY (`guid`));
        INSERT INTO `creature` VALUES (1,1001,0,1,10.5,20.5,30.5,0,60,120,0,0),(2,1002,1,1,1,2,3,0,60,60,5,1);
        CREATE TABLE `gameobject_template` (`entry` int unsigned NOT NULL, `type` int, `displayId` int, `name` varchar(100), `faction` int, `flags` int, `size` float, `data0` int, `data1` int, PRIMARY KEY (`entry`));
        INSERT INTO `gameobject_template` VALUES (2001,3,259,'Chest',0,0,1,57,2001);
        CREATE TABLE `gameobject` (`guid` int unsigned NOT NULL, `id` int unsigned NOT NULL, `map` int, `spawnMask` tinyint, `position_x` float, `position_y` float, `position_z` float, `orientation` float, `rotation0` float, `rotation1` float, `rotation2` float, `rotation3` float, `spawntimesecsmin` int, `spawntimesecsmax` int, PRIMARY KEY (`guid`));
        INSERT INTO `gameobject` VALUES (1,2001,0,1,1,2,3,0,0,0,0,1,300,300);
        CREATE TABLE `creature_loot_template` (`entry` int, `item` int, `ChanceOrQuestChance` float, `groupid` tinyint, `mincountOrRef` int, `maxcount` int, `condition_id` int);
        INSERT INTO `creature_loot_template` VALUES (5,117,40,0,1,2,0);
        """;

    private const string VMangosDump = """
        CREATE TABLE `creature_template` (`entry` int unsigned NOT NULL, `patch` tinyint, `name` varchar(100), `level_min` tinyint, `level_max` tinyint, `display_id1` int);
        INSERT INTO `creature_template` VALUES (1001,0,'Wolf',1,2,100),(1002,0,'Boar',2,3,101);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-cli-" + Guid.NewGuid().ToString("N"));

    public ContentImporterCliTests() => Directory.CreateDirectory(_directory);

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

    // --- usage and exit codes ----------------------------------------------------------------

    [Fact]
    public async Task NoArguments_AndHelp_PrintUsage()
    {
        (int none, _, string noneErr) = await RunAsync();
        (int help, string helpOut, _) = await RunAsync("--help");

        Assert.Equal(ExitCodes.Usage, none);
        Assert.Contains("usage", noneErr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExitCodes.Ok, help);
        Assert.Contains("plan", helpOut, StringComparison.Ordinal);
        Assert.Contains("import-dbc", helpOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("plan", "--no-such-flag")]
    [InlineData("plan", "--dialect")]
    [InlineData("plan", "--dialect", "oracle")]
    [InlineData("import", "--provider", "oracle")]
    [InlineData("plan", "--replace")]
    public async Task UnknownCommandOrFlag_ExitsWithUsageError(params string[] args)
    {
        (int code, _, string err) = await RunAsync(args);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public async Task PlanAndImport_RequireAtLeastOneDump()
    {
        Assert.Equal(ExitCodes.Usage, (await RunAsync("plan")).Code);
        Assert.Equal(ExitCodes.Usage, (await RunAsync("import", "--database", Db("x.db"))).Code);
    }

    [Fact]
    public async Task MissingInputFile_ExitsWithIoError()
    {
        (int code, _, string err) = await RunAsync("plan", Path.Combine(_directory, "nope.sql"));

        Assert.Equal(ExitCodes.Io, code);
        Assert.Contains("nope.sql", err, StringComparison.Ordinal);
    }

    // --- plan -----------------------------------------------------------------------------------

    [Fact]
    public async Task Plan_PrintsPerTableDialectAndMappedAndUnmappedColumns_AndWritesNoDatabase()
    {
        string dump = WriteDump("cmangos.sql", CMangosDump);

        (int code, string output, _) = await RunAsync("plan", dump);

        Assert.Equal(ExitCodes.Ok, code);
        Line template = TableLine(output, "creature_template");
        Assert.Contains("CMangosClassic", template.Text, StringComparison.Ordinal);
        Assert.Contains("rows 2", template.Text, StringComparison.Ordinal);
        Assert.Contains("TrainerType", template.Unmapped, StringComparison.Ordinal);
        Assert.DoesNotContain("TrainerType", template.Mapped, StringComparison.Ordinal);
        Assert.Contains("ModelId1", template.Mapped, StringComparison.Ordinal);
        Assert.Contains("Synthetic DB 1", output, StringComparison.Ordinal);
        Assert.Contains("sha256", output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.db"));
    }

    [Fact]
    public async Task Plan_ReportsTablesNoImporterReadsYet_AndStatementsNotApplied()
    {
        string dump = WriteDump("extra.sql", CMangosDump + "\nCREATE TABLE `npc_vendor` (`entry` int, `item` int);\nINSERT INTO `npc_vendor` VALUES (1,2),(1,3);\nUPDATE `npc_vendor` SET `item`=4;\n");

        (_, string output, _) = await RunAsync("plan", dump);

        Assert.Contains("not read by any importer", output, StringComparison.Ordinal);
        Assert.Contains("npc_vendor", output, StringComparison.Ordinal);
        Assert.Contains("UPDATE npc_vendor", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_WritesTheJsonReport_WhenAsked()
    {
        string dump = WriteDump("cmangos.sql", CMangosDump);
        string report = Path.Combine(_directory, "plan.json");

        (int code, _, _) = await RunAsync("plan", dump, "--report", report);

        Assert.Equal(ExitCodes.Ok, code);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("plan", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("CMangosClassic", json.RootElement.GetProperty("tables").GetProperty("creature_template").GetProperty("dialect").GetString());
        Assert.Equal("cmangos.sql", json.RootElement.GetProperty("inputs")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Plan_ReadsGzipDumps_ByContent()
    {
        string path = Path.Combine(_directory, "dump.sql.gz");
        using (var file = File.Create(path))
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        {
            gz.Write(Encoding.UTF8.GetBytes(CMangosDump));
        }

        (int code, string output, _) = await RunAsync("plan", path);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("rows 2", TableLine(output, "creature_template").Text, StringComparison.Ordinal);
        Assert.Contains("gzip", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_AcrossTwoFiles_SharesTheCreateTableRegistry()
    {
        string schema = WriteDump("schema.sql", "CREATE TABLE `creature_template` (`Entry` int unsigned NOT NULL, `Name` varchar(100), `MinLevel` tinyint, `MaxLevel` tinyint, `ModelId1` int);");
        string rows = WriteDump("rows.sql", "INSERT INTO `creature_template` VALUES (1,'A',1,1,5),(2,'B',1,1,6);");

        (int code, string output, _) = await RunAsync("plan", schema, rows);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("rows 2", TableLine(output, "creature_template").Text, StringComparison.Ordinal);
    }

    // --- schema and dialect guards -------------------------------------------------------------------

    [Fact]
    public async Task RenamedKeyColumn_ExitsWithSchemaError_NamingTheTable()
    {
        string dump = WriteDump("renamed.sql", CMangosDump.Replace("`Entry` int unsigned NOT NULL, `Name`", "`id` int unsigned NOT NULL, `Name`", StringComparison.Ordinal));

        (int code, _, string err) = await RunAsync("plan", dump);

        Assert.Equal(ExitCodes.Schema, code);
        Assert.Contains("creature_template", err, StringComparison.Ordinal);
        Assert.Contains("Entry", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongDialect_ExitsWithSchemaError()
    {
        string dump = WriteDump("vmangos.sql", VMangosDump);

        (int wrong, _, string err) = await RunAsync("plan", dump, "--dialect", "cmangos");
        (int right, _, _) = await RunAsync("plan", dump, "--dialect", "vmangos");
        (int auto, _, _) = await RunAsync("plan", dump);

        Assert.Equal(ExitCodes.Schema, wrong);
        Assert.Contains("vmangos", err, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Ok, right);
        Assert.Equal(ExitCodes.Ok, auto);
    }

    [Fact]
    public async Task MalformedDump_ExitsWithIoError()
    {
        string dump = WriteDump("broken.sql", "INSERT INTO `t` VALUES (1,'unterminated");

        (int code, _, string err) = await RunAsync("plan", dump);

        Assert.Equal(ExitCodes.Io, code);
        Assert.Contains("broken.sql", err, StringComparison.Ordinal);
    }

    // --- import ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_WritesTheCreatureGameObjectAndLootTables_AndTheReport()
    {
        string dump = WriteDump("cmangos.sql", CMangosDump);
        string database = Db("world.db");
        string report = Path.Combine(_directory, "import.json");

        (int code, string output, string err) = await RunAsync("import", dump, "--database", database, "--report", report);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(string.IsNullOrEmpty(err), err);
        Assert.Contains("creature_template", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(database);
        Assert.Equal(["Boar", "Wolf"], (await db.Set<CreatureTemplateRow>().AsNoTracking().ToListAsync()).Select(t => t.Name).Order());
        Assert.Equal(2, await db.Set<CreatureSpawnRow>().CountAsync());
        Assert.Equal(1, await db.Set<GameObjectTemplateRow>().CountAsync());
        Assert.Equal(1, await db.Set<GameObjectSpawnRow>().CountAsync());
        Assert.Equal(1, await db.Set<CreatureLootTemplateRow>().CountAsync());
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(report));
        JsonElement imported = json.RootElement.GetProperty("imported");
        Assert.Equal(2, imported.GetProperty("creature_template").GetInt64());
        Assert.Equal(2, imported.GetProperty("creature_spawn").GetInt64());
        Assert.False(json.RootElement.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task Import_CreatesTheCurrentWorldSchema_AndNoDevSeeds()
    {
        string database = Db("world.db");

        await RunAsync("import", WriteDump("cmangos.sql", CMangosDump), "--database", database);

        await using WorldDbContext db = Open(database);
        Assert.False(await db.PlayerCreateInfo.AnyAsync());
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, await SchemaVersionAsync(db));
    }

    [Fact]
    public async Task DryRun_WritesNothing_AndReturnsTheSameCounts()
    {
        string dump = WriteDump("cmangos.sql", CMangosDump);
        string database = Db("dry.db");
        string dryReport = Path.Combine(_directory, "dry.json");
        string realReport = Path.Combine(_directory, "real.json");

        (int dry, _, _) = await RunAsync("import", dump, "--database", database, "--dry-run", "--report", dryReport);
        Assert.Equal(ExitCodes.Ok, dry);
        Assert.False(File.Exists(database));

        (int real, _, _) = await RunAsync("import", dump, "--database", database, "--report", realReport);
        Assert.Equal(ExitCodes.Ok, real);

        using JsonDocument a = JsonDocument.Parse(File.ReadAllText(dryReport));
        using JsonDocument b = JsonDocument.Parse(File.ReadAllText(realReport));
        Assert.True(a.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(b.RootElement.GetProperty("imported").GetRawText(), a.RootElement.GetProperty("imported").GetRawText());
        Assert.Equal(b.RootElement.GetProperty("tables").GetRawText(), a.RootElement.GetProperty("tables").GetRawText());
    }

    [Fact]
    public async Task SecondImportWithoutReplace_Fails_AndKeepsTheFirst_ThenReplaceSucceeds()
    {
        string database = Db("world.db");
        string first = WriteDump("first.sql", CMangosDump);
        string second = WriteDump("second.sql", CMangosDump.Replace("'Wolf'", "'Dire Wolf'", StringComparison.Ordinal));
        Assert.Equal(ExitCodes.Ok, (await RunAsync("import", first, "--database", database)).Code);

        (int conflict, _, string err) = await RunAsync("import", second, "--database", database);
        Assert.Equal(ExitCodes.Database, conflict);
        Assert.False(string.IsNullOrWhiteSpace(err));
        await using (WorldDbContext db = Open(database))
        {
            Assert.Contains("Wolf", (await db.Set<CreatureTemplateRow>().AsNoTracking().ToListAsync()).Select(t => t.Name));
        }

        (int replaced, _, _) = await RunAsync("import", second, "--database", database, "--replace");
        Assert.Equal(ExitCodes.Ok, replaced);
        await using WorldDbContext verify = Open(database);
        Assert.Contains("Dire Wolf", (await verify.Set<CreatureTemplateRow>().AsNoTracking().ToListAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task Import_WithLockDbc_ImportsTheLocks()
    {
        string database = Db("world.db");
        string dbc = Path.Combine(_directory, "DBFilesClient");
        Directory.CreateDirectory(dbc);
        uint[] lockRecord = new uint[GameObjectLootDumpImporter.LockFieldCount];
        lockRecord[0] = 57;
        lockRecord[1] = 1;   // Type1
        lockRecord[9] = 5;   // Index1
        lockRecord[17] = 150; // Skill1
        File.WriteAllBytes(Path.Combine(dbc, "Lock.dbc"), Dbc(GameObjectLootDumpImporter.LockFieldCount, [lockRecord], "\0"));

        (int code, _, _) = await RunAsync("import", WriteDump("cmangos.sql", CMangosDump), "--database", database, "--dbc-dir", dbc);

        Assert.Equal(ExitCodes.Ok, code);
        await using WorldDbContext db = Open(database);
        Assert.Equal(57u, (await db.Set<LockTemplateRow>().AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task Import_WithoutLockDbc_Warns_AndWithAWrongLockDbc_FailsBeforeWriting()
    {
        string database = Db("world.db");
        (int ok, string output, _) = await RunAsync("import", WriteDump("cmangos.sql", CMangosDump), "--database", database);
        Assert.Equal(ExitCodes.Ok, ok);
        Assert.Contains("Lock.dbc", output, StringComparison.Ordinal);

        string dbc = Path.Combine(_directory, "bad");
        Directory.CreateDirectory(dbc);
        File.WriteAllBytes(Path.Combine(dbc, "Lock.dbc"), Dbc(4, [[1u, 2u, 3u, 4u]], "\0"));
        string other = Db("other.db");

        (int bad, _, string err) = await RunAsync("import", WriteDump("again.sql", CMangosDump), "--database", other, "--dbc-dir", dbc);

        Assert.Equal(ExitCodes.Io, bad);
        Assert.Contains("Lock.dbc", err, StringComparison.Ordinal);
        Assert.False(File.Exists(other));
    }

    // --- import-dbc ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportDbc_ReplacesTheSpellTables()
    {
        string database = Db("world.db");
        string dbc = Path.Combine(_directory, "DBFilesClient");
        Directory.CreateDirectory(dbc);
        uint[] spell = new uint[SpellDbcImporter.SpellFieldCount];
        spell[0] = 133;
        spell[120] = 1;
        File.WriteAllBytes(Path.Combine(dbc, "Spell.dbc"), Dbc(SpellDbcImporter.SpellFieldCount, [spell], "\0Fireball\0"));
        File.WriteAllBytes(Path.Combine(dbc, "SpellCastTimes.dbc"), Dbc(4, [[5u, 3500u, 0u, 3500u]], "\0"));
        File.WriteAllBytes(Path.Combine(dbc, "SpellDuration.dbc"), Dbc(4, [[21u, unchecked((uint)-1), 0u, unchecked((uint)-1)]], "\0"));
        File.WriteAllBytes(Path.Combine(dbc, "SpellRange.dbc"), Dbc(22, [[4u, 0u, BitConverter.SingleToUInt32Bits(30f), 0u, .. new uint[18]]], "\0"));
        File.WriteAllBytes(Path.Combine(dbc, "SpellRadius.dbc"), Dbc(4, [[7u, BitConverter.SingleToUInt32Bits(2f), 0u, BitConverter.SingleToUInt32Bits(2f)]], "\0"));

        (int code, string output, _) = await RunAsync("import-dbc", dbc, "--database", database);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("1 spells", output, StringComparison.Ordinal);
        await using WorldDbContext db = Open(database);
        Assert.Equal("Fireball", (await db.Set<SpellTemplateRow>().AsNoTracking().SingleAsync()).SpellName);
    }

    [Fact]
    public async Task ImportDbc_MissingDirectoryOrFile_ExitsWithIoError()
    {
        (int noDir, _, _) = await RunAsync("import-dbc", Path.Combine(_directory, "absent"), "--database", Db("w.db"));
        string empty = Path.Combine(_directory, "empty");
        Directory.CreateDirectory(empty);
        (int noFile, _, string err) = await RunAsync("import-dbc", empty, "--database", Db("w.db"));

        Assert.Equal(ExitCodes.Io, noDir);
        Assert.Equal(ExitCodes.Io, noFile);
        Assert.Contains("Spell.dbc", err, StringComparison.Ordinal);
    }

    // --- verify -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Verify_CountsTables_AndFlagsSpawnsWithoutTemplates()
    {
        string database = Db("world.db");
        Assert.Equal(ExitCodes.Ok, (await RunAsync("import", WriteDump("cmangos.sql", CMangosDump), "--database", database)).Code);

        (int clean, string output, _) = await RunAsync("verify", "--database", database);
        Assert.Equal(ExitCodes.Ok, clean);
        Assert.Contains("creature_template", output, StringComparison.Ordinal);

        await using (WorldDbContext db = Open(database))
        {
            db.Add(new CreatureSpawnRow { Guid = 99, Entry = 424242, MapId = 0 });
            db.Add(new CreatureSpawnRow { Guid = 100, Entry = 0, MapId = 0 });
            await db.SaveChangesAsync();
        }

        (int dangling, string bad, _) = await RunAsync("verify", "--database", database);
        Assert.Equal(ExitCodes.Verify, dangling);
        Assert.Contains("1 creature spawn", bad, StringComparison.Ordinal);
        Assert.Contains("entry 0", bad, StringComparison.Ordinal);
    }

    // --- repository path guard ------------------------------------------------------------------------------------

    [Fact]
    public async Task DatabaseInsideAGitWorkTree_IsRefusedUnlessIgnored()
    {
        string repo = Path.Combine(_directory, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "ignored/\n");
        Directory.CreateDirectory(Path.Combine(repo, "ignored"));
        string dump = WriteDump("cmangos.sql", CMangosDump);
        string tracked = Path.Combine(repo, "world.db");
        string ignored = Path.Combine(repo, "ignored", "world.db");

        (int refused, _, string err) = await RunAsync("import", dump, "--database", tracked);
        (int allowed, _, _) = await RunAsync("import", dump, "--database", ignored);

        Assert.Equal(ExitCodes.RepositoryPath, refused);
        Assert.Contains("git", err, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(tracked));
        Assert.Equal(ExitCodes.Ok, allowed);
        Assert.True(File.Exists(ignored));
    }

    [Fact]
    public async Task ReportInsideAGitWorkTree_IsRefusedToo()
    {
        string repo = Path.Combine(_directory, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        string report = Path.Combine(repo, "report.json");

        (int code, _, _) = await RunAsync("plan", WriteDump("cmangos.sql", CMangosDump), "--report", report);

        Assert.Equal(ExitCodes.RepositoryPath, code);
        Assert.False(File.Exists(report));
    }

    // --- secrets --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Server=db;Database=w;User Id=arcane;Password=hunter2;", "hunter2")]
    [InlineData("Host=db;Username=arcane;pwd=hunter2", "hunter2")]
    [InlineData("Server=db;Password='a;b'", "a;b")]
    public void ConnectionStrings_AreRedacted(string connectionString, string secret)
    {
        string redacted = ConnectionStringRedactor.Redact(connectionString);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("Server=db", redacted.Replace("Host=db", "Server=db", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ConnectionStringRedactor.Scrub("could not connect with Password=hunter2 to db", connectionString), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableServer_FailsWithDatabaseError_WithoutLeakingThePassword()
    {
        string dump = WriteDump("cmangos.sql", CMangosDump);

        (int code, string output, string err) = await RunAsync(
            "import", dump, "--provider", "mariadb", "--connection-string", "Server=127.0.0.1;Port=1;Database=w;User Id=arcane;Password=hunter2;Connection Timeout=2");

        Assert.Equal(ExitCodes.Database, code);
        Assert.DoesNotContain("hunter2", output + err, StringComparison.Ordinal);
    }

    // --- helpers ---------------------------------------------------------------------------------------------------------

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private string Db(string name) => Path.Combine(_directory, name);

    private string WriteDump(string name, string sql)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, sql);
        return path;
    }

    private static WorldDbContext Open(string path)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        return new WorldDbContext(options);
    }

    private static async Task<int> SchemaVersionAsync(WorldDbContext db)
        => (await db.Set<SchemaVersionRow>().AsNoTracking().SingleAsync()).Version;

    private readonly record struct Line(string Text, string Mapped, string Unmapped);

    /// <summary>The plan line of a table, with its mapped / unmapped column lists (the text after "mapped:" and "not imported:").</summary>
    private static Line TableLine(string output, string table)
    {
        string[] lines = output.Split('\n');
        int index = Array.FindIndex(lines, l => l.TrimStart().StartsWith(table + " ", StringComparison.Ordinal));
        Assert.True(index >= 0, $"no plan line for {table} in:\n{output}");
        var block = new StringBuilder(lines[index]);
        string mapped = string.Empty;
        string unmapped = string.Empty;
        for (int i = index + 1; i < lines.Length && lines[i].StartsWith("      ", StringComparison.Ordinal); i++)
        {
            string text = lines[i].Trim();
            if (text.StartsWith("mapped:", StringComparison.Ordinal))
            {
                mapped = text;
            }
            else if (text.StartsWith("not imported:", StringComparison.Ordinal))
            {
                unmapped = text;
            }
        }

        return new Line(block.ToString(), mapped, unmapped);
    }

    private static void Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static byte[] Dbc(int fields, IReadOnlyList<uint[]> records, string strings)
    {
        byte[] block = Encoding.UTF8.GetBytes(strings);
        var data = new byte[20 + (records.Count * fields * 4) + block.Length];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)(fields * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)block.Length);
        for (int r = 0; r < records.Count; r++)
        {
            for (int f = 0; f < records[r].Length; f++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20 + (((r * fields) + f) * 4)), records[r][f]);
            }
        }

        block.CopyTo(data, 20 + (records.Count * fields * 4));
        return data;
    }
}
