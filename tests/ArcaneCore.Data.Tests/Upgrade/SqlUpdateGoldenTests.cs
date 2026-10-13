using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// The committed sql/updates files, with no database: a baseline and one file per later step for every provider, headers whose
/// body hash matches, a frozen manifest, and the newest file plus the snapshot equal to a fresh regeneration. Regenerate with
/// ARCANECORE_UPDATE_SQL=1 (see <see cref="SqlUpdateSupport.RegenerateCommand"/>). A missing file is a failure, never a skip.
/// </summary>
public sealed class SqlUpdateGoldenTests
{
    private static readonly string[] s_folders = ["sqlite", "mariadb", "postgresql"];

    public static IEnumerable<object[]> Components() => [["auth"], ["characters"], ["world"]];

    private static string[] SqlFiles(string component, string folder)
    {
        string dir = Path.Combine(SqlUpdateSupport.Root, component, folder);
        Assert.True(Directory.Exists(dir), $"sql/updates/{component}/{folder} is missing. Generate it: {SqlUpdateSupport.RegenerateCommand}");
        return [.. Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(Components))]
    public async Task EveryStepAboveTheBaseline_HasAFileForEveryProvider(string component)
    {
        await SqlUpdateSupport.RegenerateIfRequestedAsync();
        int current = UpdateComponent.For(component).Definition.CurrentVersion;
        SchemaSnapshot snapshot = SqlUpdateSupport.ReadSnapshot(component);
        Assert.Equal(current, snapshot.Version);
        foreach (string folder in s_folders)
        {
            string[] files = SqlFiles(component, folder);
            string[] baselines = [.. files.Where(f => f.StartsWith("base_v", StringComparison.Ordinal))];
            Assert.True(baselines.Length == 1, $"{component}/{folder}: expected exactly one baseline, found [{string.Join(", ", baselines)}]");
            int baseline = int.Parse(baselines[0]["base_v".Length..^".sql".Length]);
            Assert.InRange(baseline, 1, current);
            for (int version = baseline + 1; version <= current; version++)
            {
                string prefix = UpdateScriptWriter.StepPrefix(version);
                Assert.True(files.Count(f => f.StartsWith(prefix, StringComparison.Ordinal)) == 1,
                    $"{component}/{folder}: expected exactly one file for schema version {version} ({prefix}*.sql). Generate it: {SqlUpdateSupport.RegenerateCommand}");
            }

            Assert.Equal(1 + (current - baseline), files.Length);

            // The chain is unbroken: each file starts where the previous one ended.
            int at = 0;
            foreach (string file in SqlUpdateSupport.OrderedFiles(component, folder == "sqlite" ? DatabaseProvider.Sqlite : folder == "mariadb" ? DatabaseProvider.MariaDb : DatabaseProvider.PostgreSql))
            {
                UpdateFileHeader header = UpdateFileHeader.Parse(File.ReadAllText(file));
                Assert.Equal(component, header.Component);
                Assert.Equal(folder, header.Provider);
                Assert.Equal(at == 0 ? null : at, header.From);
                at = header.To;
            }

            Assert.Equal(current, at);
        }
    }

    [Theory]
    [MemberData(nameof(Components))]
    public async Task HeaderHashes_MatchTheBodies(string component)
    {
        await SqlUpdateSupport.RegenerateIfRequestedAsync();
        int checkedFiles = 0;
        foreach (string folder in s_folders)
        {
            foreach (string name in SqlFiles(component, folder))
            {
                UpdateFileHeader header = UpdateFileHeader.Parse(File.ReadAllText(Path.Combine(SqlUpdateSupport.Root, component, folder, name)));
                Assert.True(header.BodySha256 == header.ActualBodySha256, $"{component}/{folder}/{name}: the header hash does not match the body (edited by hand?)");
                checkedFiles++;
            }
        }

        Assert.True(checkedFiles >= 3);
    }

    [Theory]
    [MemberData(nameof(Components))]
    public async Task ReleasedFiles_AreFrozen_ByTheSnapshotManifest(string component)
    {
        await SqlUpdateSupport.RegenerateIfRequestedAsync();
        SchemaSnapshot snapshot = SqlUpdateSupport.ReadSnapshot(component);
        string[] onDisk = [.. s_folders.SelectMany(f => SqlFiles(component, f).Select(n => $"{f}/{n}"))];
        Assert.Equal(snapshot.Files.Keys.Order(StringComparer.Ordinal), onDisk.Order(StringComparer.Ordinal));
        foreach ((string path, string hash) in snapshot.Files)
        {
            string actual = UpdateScriptWriter.Hash(File.ReadAllText(Path.Combine(SqlUpdateSupport.Root, component, path)));
            Assert.True(actual == hash, $"{component}/{path} differs from the released file (manifest {hash}, file {actual}). A released update file is never edited: ship a new step instead.");
        }
    }

    [Theory]
    [MemberData(nameof(Components))]
    public async Task NewestFileAndSnapshot_EqualAFreshRegeneration(string component)
    {
        await SqlUpdateSupport.RegenerateIfRequestedAsync();
        SchemaSnapshot committed = SqlUpdateSupport.ReadSnapshot(component);
        UpdateSet fresh = await UpdateScriptWriter.GenerateAsync(UpdateComponent.For(component), committed);
        Assert.Equal(3, fresh.Files.Count);
        foreach (UpdateFile file in fresh.Files)
        {
            string path = Path.Combine(SqlUpdateSupport.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{file.Path} does not exist. Generate it: {SqlUpdateSupport.RegenerateCommand}");
            Assert.True(SqlUpdateSupport.ReadNormalized(path) == file.Content, $"{file.Path} is stale. Regenerate and review the diff: {SqlUpdateSupport.RegenerateCommand}");
        }

        string snapshotText = SqlUpdateSupport.ReadNormalized(Path.Combine(SqlUpdateSupport.Root, component, UpdateScriptWriter.SnapshotFileName));
        Assert.True(snapshotText == fresh.Snapshot.ToJson(), $"{component}/{UpdateScriptWriter.SnapshotFileName} is stale. Regenerate: {SqlUpdateSupport.RegenerateCommand}");
    }

    [Fact]
    public async Task ReservedGapVersions_RefuseTheExport()
    {
        UpdateComponent auth = UpdateComponent.For("auth");
        UpdateComponent gapped = auth with { Definition = new SchemaDefinition
        {
            Component = "auth", CurrentVersion = auth.Definition.CurrentVersion, Version1Tables = auth.Definition.Version1Tables,
            Steps = auth.Definition.Steps, ReservedGapVersions = [3],
        } };
        UpdateExportException ex = await Assert.ThrowsAsync<UpdateExportException>(() => UpdateScriptWriter.GenerateAsync(gapped, SqlUpdateSupport.ReadSnapshot("auth")));
        Assert.Contains("reserved", ex.Message);
    }

    [Fact]
    public async Task NonAdditiveModelDrift_IsRefusedLoudly()
    {
        SchemaSnapshot committed = SqlUpdateSupport.ReadSnapshot("auth");
        SchemaSnapshot retyped = Tamper(committed, tables => [.. tables.Select(t => t.Name != "account" ? t : t with
        {
            Columns = [.. t.Columns.Select(c => c.Name == "Username" ? c with { Nullable = true } : c)],
        })]);
        UpdateExportException ex = await Assert.ThrowsAsync<UpdateExportException>(() => UpdateScriptWriter.GenerateAsync(UpdateComponent.For("auth"), retyped));
        Assert.Contains("cannot carry", ex.Message);
        Assert.Contains("account.Username", ex.Message);
    }

    [Fact]
    public async Task ModelChangeWithoutAStep_IsRefused()
    {
        SchemaSnapshot committed = SqlUpdateSupport.ReadSnapshot("auth");
        SchemaSnapshot behind = Tamper(committed, tables => [.. tables.Select(t => t.Name != "account" ? t : t with
        {
            Columns = [.. t.Columns.Where(c => c.Name != "LastIp")],
        })]);
        UpdateExportException ex = await Assert.ThrowsAsync<UpdateExportException>(() => UpdateScriptWriter.GenerateAsync(UpdateComponent.For("auth"), behind));
        Assert.Contains("no schema step accounts for", ex.Message);
    }

    [Fact]
    public async Task ANewStep_GetsAGuardedFile_ForEveryProvider()
    {
        (UpdateComponent component, SchemaSnapshot snapshot) = StandInStep();
        UpdateSet set = await UpdateScriptWriter.GenerateAsync(component, snapshot);

        Assert.Equal(3, set.Files.Count);
        Assert.Equal(6, set.Snapshot.Version);
        Assert.All(set.Files, f => Assert.Contains("0006_InlineStep.sql", f.Path));
        string sqlite = set.Files.Single(f => f.Path.Contains("/sqlite/")).Content;
        string postgres = set.Files.Single(f => f.Path.Contains("/postgresql/")).Content;
        string maria = set.Files.Single(f => f.Path.Contains("/mariadb/")).Content;
        Assert.Contains("CREATE UNIQUE INDEX \"IX_account_Username\"", sqlite);
        Assert.Contains("CHECK (\"requires_auth_schema_version_5\" = 1)", sqlite);
        Assert.Contains("RAISE EXCEPTION", postgres);
        Assert.Contains("BEGIN NOT ATOMIC", maria);
        Assert.Contains("SIGNAL SQLSTATE '45000'", maria);
        foreach (string text in new[] { sqlite, postgres, maria })
        {
            Assert.Contains("UPDATE", text);
            Assert.Contains("= 6 WHERE", text);
            // the version row is written last
            Assert.True(text.LastIndexOf("= 6 WHERE", StringComparison.Ordinal) > text.LastIndexOf("INDEX", StringComparison.Ordinal));
            UpdateFileHeader header = UpdateFileHeader.Parse(text);
            Assert.Equal(5, header.From);
            Assert.Equal(6, header.To);
            Assert.Equal(header.BodySha256, header.ActualBodySha256);
        }

        Assert.NotNull(set.Snapshot.Providers["sqlite"].Previous);
    }

    [Fact]
    public async Task TheMysqlSplitter_HandlesDelimiter_InAGeneratedFile()
    {
        (UpdateComponent component, SchemaSnapshot snapshot) = StandInStep();
        UpdateSet set = await UpdateScriptWriter.GenerateAsync(component, snapshot);
        IReadOnlyList<string> statements = MysqlClientSplitter.Split(set.Files.Single(f => f.Path.Contains("/mariadb/")).Content);

        Assert.Equal(3, statements.Count);
        Assert.StartsWith("BEGIN NOT ATOMIC", statements[0], StringComparison.Ordinal);
        Assert.EndsWith("END", statements[0], StringComparison.Ordinal);
        Assert.StartsWith("CREATE UNIQUE INDEX", statements[1], StringComparison.Ordinal);
        Assert.StartsWith("UPDATE", statements[2], StringComparison.Ordinal);
        Assert.DoesNotContain(statements, s => s.Contains("DELIMITER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportUpdatesCommand_WritesTheCommittedFiles_AndNeedsOut()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcane-sqlupdates-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new DatabaseOptions();
            var output = new StringWriter();
            var error = new StringWriter();
            int code = await DbUpgradeCli.RunAsync(["export-updates", "--component", "auth", "--out", root], options, output, error, CancellationToken.None);
            Assert.True(code == 0, error.ToString());
            string sqlite = Path.Combine(root, "auth", "sqlite", UpdateScriptWriter.BaselineFileName(UpdateComponent.For("auth").Definition.CurrentVersion));
            Assert.True(File.Exists(sqlite));
            Assert.Equal(
                SqlUpdateSupport.ReadNormalized(Path.Combine(SqlUpdateSupport.Root, "auth", "sqlite", Path.GetFileName(sqlite))),
                SqlUpdateSupport.ReadNormalized(sqlite));

            var err2 = new StringWriter();
            int usage = await DbUpgradeCli.RunAsync(["export-updates"], options, new StringWriter(), err2, CancellationToken.None);
            Assert.Equal(DbUpgradeExitCodes.Usage, usage);
            Assert.Contains("--out", err2.ToString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SchemaSnapshot Tamper(SchemaSnapshot snapshot, Func<IReadOnlyList<SnapshotTable>, IReadOnlyList<SnapshotTable>> change)
        => snapshot with
        {
            Providers = snapshot.Providers.ToDictionary(p => p.Key, p => p.Value with { Tables = change(p.Value.Tables) }),
        };

    /// <summary>
    /// The auth snapshot without its account index and a definition with one more step that repairs the indexes of account:
    /// the file such a step produces, without changing the model.
    /// </summary>
    internal static (UpdateComponent Component, SchemaSnapshot Snapshot) StandInStep()
    {
        SchemaSnapshot committed = SqlUpdateSupport.ReadSnapshot("auth");
        SchemaSnapshot without = Tamper(committed, tables => [.. tables.Select(t => t.Name != "account" ? t : t with { Indexes = [] })]);
        UpdateComponent auth = UpdateComponent.For("auth");
        return (auth with { Definition = UpgradeTestSupport.WithIndexRepairStep(AuthDbContext.Schema, "account") }, without);
    }
}
