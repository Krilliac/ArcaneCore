using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>The export refused: the update files cannot be produced faithfully (non-additive model change, reserved gap, damaged snapshot).</summary>
public sealed class UpdateExportException(string message) : Exception(message);

/// <summary>A component the update files are produced for: its schema, and a context per provider that never connects.</summary>
/// <param name="Name">auth, characters or world (the folder under sql/updates).</param>
/// <param name="Definition">The schema the code needs.</param>
/// <param name="Kind">The data-module component, to name the owner of a step.</param>
/// <param name="CreateOffline">A context for the provider with a placeholder connection (only its model and SQL generator are used).</param>
internal sealed record UpdateComponent(
    string Name, SchemaDefinition Definition, DatabaseComponent Kind, Func<DatabaseProvider, DbContext> CreateOffline,
    Func<int, (string Owner, string FileName)>? OwnerResolver = null)
{
    public static IReadOnlyList<UpdateComponent> All { get; } =
    [
        new("auth", AuthDbContext.Schema, DatabaseComponent.Auth, p => Offline<AuthDbContext>(p, o => new AuthDbContext(o))),
        new("characters", CharacterDbContext.Schema, DatabaseComponent.Characters, p => Offline<CharacterDbContext>(p, o => new CharacterDbContext(o))),
        new("world", WorldDbContext.Schema, DatabaseComponent.World, p => Offline<WorldDbContext>(p, o => new WorldDbContext(o))),
    ];

    public static UpdateComponent For(string name)
        => All.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentOutOfRangeException(nameof(name), name, "component must be auth, characters or world");

    /// <summary>
    /// A context that is never opened. MariaDB is pinned to 11.4 (docker-compose.yml's image) instead of auto-detected, so
    /// the generated DDL does not depend on a server; PostgreSQL and SQLite generate without one.
    /// </summary>
    internal static T Offline<T>(DatabaseProvider provider, Func<DbContextOptions<T>, T> create) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                builder.UseSqlite("Data Source=arcanecore-offline.db");
                break;
            case DatabaseProvider.MariaDb:
                builder.UseMySql("Server=offline.invalid;Database=arcanecore", new MariaDbServerVersion(new Version(11, 4, 0)));
                break;
            case DatabaseProvider.PostgreSql:
                builder.UseNpgsql("Host=offline.invalid;Database=arcanecore");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "update files exist for sqlite, mariadb and postgresql");
        }

        return create(builder.Options);
    }
}

/// <summary>A generated update file.</summary>
/// <param name="Path">Relative to the update root: <c>auth/sqlite/0006_Name.sql</c>.</param>
/// <param name="Content">LF-normalised text.</param>
internal sealed record UpdateFile(string Path, string Content);

/// <summary>What one export run produces for one component: the files it generated (the newest ones) and the snapshot to commit.</summary>
internal sealed record UpdateSet(IReadOnlyList<UpdateFile> Files, SchemaSnapshot Snapshot);

/// <summary>The parsed header of an update file.</summary>
/// <param name="From">The schema version the file requires, null for a baseline.</param>
internal sealed record UpdateFileHeader(string Component, string Provider, int? From, int To, string Owner, string BodySha256, string Body)
{
    private static readonly Regex s_header = new(@"^-- (?<key>[a-z0-9-]+): (?<value>.*)$", RegexOptions.CultureInvariant);

    /// <summary>The hash the header should hold for <see cref="Body"/>.</summary>
    public string ActualBodySha256 => UpdateScriptWriter.Hash(Body);

    public static UpdateFileHeader Parse(string content)
    {
        string text = UpdateScriptWriter.Normalize(content);
        string[] lines = text.Split('\n');
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        for (; i < lines.Length && lines[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            Match m = s_header.Match(lines[i]);
            if (m.Success)
            {
                values[m.Groups["key"].Value] = m.Groups["value"].Value;
            }
        }

        string Get(string key) => values.GetValueOrDefault(key) ?? throw new FormatException($"update file header lacks '{key}'");
        string from = Get("from");
        int? fromVersion = from.StartsWith("none", StringComparison.Ordinal)
            ? null
            : int.Parse(from, NumberStyles.None, CultureInfo.InvariantCulture);
        string body = string.Join('\n', lines.Skip(i + 1));
        return new UpdateFileHeader(
            Get("component"), Get("provider"), fromVersion, int.Parse(Get("to"), NumberStyles.None, CultureInfo.InvariantCulture),
            Get("owner"), Get("body-sha256"), body);
    }
}

/// <summary>
/// Writes the operator-applied SQL update files (<c>sql/updates/&lt;component&gt;/&lt;provider&gt;/</c>): per component and provider
/// one baseline (<c>base_vNNNN.sql</c>, an empty database to that version) and one file per schema step after it
/// (<c>NNNN_&lt;Owner&gt;.sql</c>), all produced by the planner and EF's own SQL generator without a database, against the
/// snapshot (<see cref="SchemaSnapshot"/>) of the schema after the previous file.
/// <para>
/// Every file records a header (component, provider, from, to, owner, body hash), checks the prior version first, runs in one
/// transaction where the engine has transactional DDL (SQLite, PostgreSQL) and writes the version row last. MariaDB DDL commits
/// implicitly, so a failed MariaDB file can leave earlier statements applied: recover with <c>arcane-db upgrade</c>, which converges.
/// </para>
/// <para>
/// A step is generated from the current model, so it is faithful only for additive changes. A model that differs from the snapshot
/// in any other way (a column's type or nullability, a dropped column) is refused, as is any export while a reserved placeholder
/// version exists.
/// </para>
/// </summary>
internal static class UpdateScriptWriter
{
    public const string SnapshotFileName = "schema-snapshot.json";

    /// <summary>The providers the files exist for, with their folder names.</summary>
    public static IReadOnlyList<(DatabaseProvider Provider, string Folder)> Providers { get; } =
    [
        (DatabaseProvider.Sqlite, "sqlite"),
        (DatabaseProvider.MariaDb, "mariadb"),
        (DatabaseProvider.PostgreSql, "postgresql"),
    ];

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(Normalize(text)))).ToLowerInvariant();

    /// <summary>The file name of the baseline at <paramref name="version"/>.</summary>
    public static string BaselineFileName(int version) => $"base_v{version.ToString("0000", CultureInfo.InvariantCulture)}.sql";

    /// <summary>The file name prefix of the step to <paramref name="version"/>.</summary>
    public static string StepPrefix(int version) => version.ToString("0000", CultureInfo.InvariantCulture) + "_";

    /// <summary>Generate (without a database) the update files a component needs beyond <paramref name="existing"/>, and the snapshot after them.</summary>
    /// <param name="component">The component.</param>
    /// <param name="existing">The committed snapshot; null exports the baseline at the current version.</param>
    /// <exception cref="UpdateExportException">The export is refused; nothing is produced.</exception>
    public static async Task<UpdateSet> GenerateAsync(UpdateComponent component, SchemaSnapshot? existing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(component);
        SchemaDefinition definition = component.Definition;
        if (definition.ReservedGapVersions.Count > 0)
        {
            throw new UpdateExportException(
                $"{component.Name}: schema version(s) {string.Join(", ", definition.ReservedGapVersions)} are reserved placeholders for modules this build lacks; " +
                "update files written now would skip their steps. Export from a tree that has every module.");
        }

        if (existing is not null && existing.Component != component.Name)
        {
            throw new UpdateExportException($"{SnapshotFileName} is for '{existing.Component}', not '{component.Name}'");
        }

        int current = definition.CurrentVersion;
        if (existing is not null && existing.Version > current)
        {
            throw new UpdateExportException($"{component.Name}: the snapshot is at schema version {existing.Version}, newer than this build ({current}).");
        }

        var files = new List<UpdateFile>();
        var providers = new Dictionary<string, ProviderSnapshot>(StringComparer.Ordinal);
        var manifest = new Dictionary<string, string>(existing?.Files ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach ((DatabaseProvider provider, string folder) in Providers)
        {
            using DbContext db = component.CreateOffline(provider);
            SnapshotCatalog model = SnapshotCatalog.FromModel(db);
            var generated = new List<UpdateFile>();
            ProviderSnapshot snapshot;
            if (existing is null)
            {
                SnapshotCatalog after = await BaselineAsync(component, db, folder, model, generated, cancellationToken).ConfigureAwait(false);
                snapshot = new ProviderSnapshot(after.ToTables(), null);
            }
            else
            {
                ProviderSnapshot have = existing.Providers.GetValueOrDefault(folder)
                    ?? throw new UpdateExportException($"{SnapshotFileName} has no '{folder}' section");
                if (existing.Version == current)
                {
                    // The newest file again: its source state is the snapshot's previous state (null: the baseline).
                    SnapshotCatalog after = have.Previous is null
                        ? await BaselineAsync(component, db, folder, model, generated, cancellationToken).ConfigureAwait(false)
                        : await StepAsync(component, db, folder, model, SnapshotCatalog.From(have.Previous), current, generated, cancellationToken).ConfigureAwait(false);
                    RequireSameAsModel(component, folder, SnapshotCatalog.From(have.Tables), model);
                    RequireSameAsModel(component, folder, after, model);
                    snapshot = have;
                }
                else
                {
                    SnapshotCatalog state = SnapshotCatalog.From(have.Tables);
                    SnapshotCatalog previous = state;
                    for (int version = existing.Version + 1; version <= current; version++)
                    {
                        previous = state;
                        state = await StepAsync(component, db, folder, model, state, version, generated, cancellationToken).ConfigureAwait(false);
                    }

                    RequireSameAsModel(component, folder, state, model);
                    snapshot = new ProviderSnapshot(state.ToTables(), previous.ToTables());
                }
            }

            providers[folder] = snapshot;
            foreach (UpdateFile file in generated)
            {
                files.Add(file);
                string key = file.Path[(component.Name.Length + 1)..];
                // A released file keeps the hash it was released with; only files this run creates are recorded.
                if (!manifest.ContainsKey(key))
                {
                    manifest[key] = Hash(file.Content);
                }
            }
        }

        return new UpdateSet(files, new SchemaSnapshot(component.Name, current, providers, manifest));
    }

    /// <summary>Generate and write the files and snapshot of <paramref name="components"/> under <paramref name="root"/> (<c>sql/updates</c>).</summary>
    /// <returns>The paths written.</returns>
    public static async Task<IReadOnlyList<string>> WriteAsync(
        string root, IEnumerable<UpdateComponent> components, CancellationToken cancellationToken = default)
    {
        var written = new List<string>();
        var sets = new List<(UpdateComponent Component, UpdateSet Set)>();
        foreach (UpdateComponent component in components)
        {
            string snapshotPath = System.IO.Path.Combine(root, component.Name, SnapshotFileName);
            SchemaSnapshot? existing = File.Exists(snapshotPath) ? SchemaSnapshot.Parse(await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false)) : null;
            sets.Add((component, await GenerateAsync(component, existing, cancellationToken).ConfigureAwait(false)));
        }

        // Every component generated before the first byte is written: a refusal leaves the folder as it was.
        var utf8 = new UTF8Encoding(false);
        foreach ((UpdateComponent component, UpdateSet set) in sets)
        {
            foreach (UpdateFile file in set.Files)
            {
                string path = System.IO.Path.Combine(root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file.Content, utf8, cancellationToken).ConfigureAwait(false);
                written.Add(path);
            }

            string snapshotPath = System.IO.Path.Combine(root, component.Name, SnapshotFileName);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, set.Snapshot.ToJson(), utf8, cancellationToken).ConfigureAwait(false);
            written.Add(snapshotPath);
        }

        return written;
    }

    private static async Task<SnapshotCatalog> BaselineAsync(
        UpdateComponent component, DbContext db, string folder, SnapshotCatalog modelCatalog, List<UpdateFile> into, CancellationToken ct)
    {
        SchemaPlan plan = await SchemaPlanner.PlanOfflineAsync(db, component.Definition, null, 0, component.Definition.CurrentVersion, ct).ConfigureAwait(false);
        RequireAcceptable(component, plan);
        PlannedStep step = plan.Steps.Single();
        var after = SnapshotCatalog.Empty();
        after.Apply(step, modelCatalog);

        var statements = new List<string>();
        IMigrationsSqlGenerator generator = db.GetService<IMigrationsSqlGenerator>();
        IModel model = db.GetService<IDesignTimeModel>().Model;
        foreach (AlterDatabaseOperation alter in SchemaChangeDecider.ReadModel(db).AlterDatabase)
        {
            statements.AddRange(generator.Generate([alter], model).Select(c => c.CommandText));
        }

        // The order CreateFreshAsync uses: the version table, its "creating" row (version 0), the model tables; Compose ends with
        // the UPDATE to the real version. An interrupted baseline then leaves what an interrupted bootstrap leaves, and
        // 'arcane-db upgrade' resumes it.
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        string versionTable = component.Definition.VersionTable;
        PlannedAction[] creates = [.. step.Actions.Where(a => a.Decision == ChangeDecision.Create)];
        statements.AddRange(creates.Where(a => a.Table == versionTable).SelectMany(a => a.Script ?? []));
        statements.Add($"INSERT INTO {sql.DelimitIdentifier(versionTable)} ({sql.DelimitIdentifier("Id")}, {sql.DelimitIdentifier("Version")}) VALUES (1, {SchemaBootstrapper.CreatingVersion});");
        statements.AddRange(creates.Where(a => a.Table != versionTable).SelectMany(a => a.Script ?? []));
        string owner = $"baseline: every module through schema version {component.Definition.CurrentVersion}";
        into.Add(new UpdateFile(
            $"{component.Name}/{folder}/{BaselineFileName(component.Definition.CurrentVersion)}",
            Compose(component, db, folder, from: null, to: component.Definition.CurrentVersion, owner, statements,
                [.. creates.Where(a => a.Object == ChangeObject.Table).Select(a => a.Table)])));
        return after;
    }

    private static async Task<SnapshotCatalog> StepAsync(
        UpdateComponent component, DbContext db, string folder, SnapshotCatalog modelCatalog, SnapshotCatalog from, int version, List<UpdateFile> into, CancellationToken ct)
    {
        SchemaPlan plan;
        try
        {
            plan = await SchemaPlanner.PlanOfflineAsync(db, component.Definition, from, version - 1, version, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            throw new UpdateExportException($"{component.Name}: {ex.Message}");
        }

        RequireAcceptable(component, plan);
        PlannedStep step = plan.Steps.Single(s => s.Version == version);
        var after = SnapshotCatalog.From(from.ToTables());
        after.Apply(step, modelCatalog);

        var statements = new List<string>();
        statements.AddRange(step.Actions.Where(a => a.Decision == ChangeDecision.Create).SelectMany(a => a.Script ?? []));
        (string owner, string name) = OwnerOf(component, version);
        into.Add(new UpdateFile(
            $"{component.Name}/{folder}/{StepPrefix(version)}{name}.sql",
            Compose(component, db, folder, from: version - 1, to: version, owner, statements)));
        return after;
    }

    private static void RequireAcceptable(UpdateComponent component, SchemaPlan plan)
    {
        if (plan.Refusal is not null)
        {
            throw new UpdateExportException($"{component.Name}: {plan.Refusal}");
        }

        if (plan.Blockers.Count > 0)
        {
            throw new UpdateExportException($"{component.Name}: {plan.Blockers[0].Detail}");
        }
    }

    private static void RequireSameAsModel(UpdateComponent component, string folder, SnapshotCatalog afterFiles, SnapshotCatalog model)
    {
        IReadOnlyList<CatalogDifference> differences = afterFiles.Differences(model);
        if (differences.Count == 0)
        {
            return;
        }

        string list = string.Join("; ", differences.Take(12).Select(d => d.Description)) + (differences.Count > 12 ? $"; ... ({differences.Count} in all)" : string.Empty);
        throw new UpdateExportException(differences.Any(d => !d.ModelAhead)
            ? $"{component.Name} ({folder}): the model changed in a way a SQL update file cannot carry faithfully (only new tables, columns and indexes can): {list}. " +
              "Ship such a change as a new schema step that adds a new column or table, never by editing an existing one."
            : $"{component.Name} ({folder}): the model has changes no schema step accounts for: {list}. Add a step (an IDataModule or an inline SchemaStep) that creates them, then export again.");
    }

    /// <summary>The owner shown in a step file's header and the module part of its file name.</summary>
    private static (string Owner, string FileName) OwnerOf(UpdateComponent component, int version)
    {
        if (component.OwnerResolver is { } resolve)
        {
            return resolve(version);
        }

        IDataModule[] owners = [.. DataModules.For(component.Kind).Where(m => m.SchemaVersion == version && m is not IReservedSchemaGap)];
        if (owners.Length != 1)
        {
            return ("inline step of the database context", "InlineStep");
        }

        Type type = owners[0].GetType();
        string full = type.FullName ?? type.Name;
        const string prefix = "ArcaneCore.Data.";
        string owner = full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..] : full;
        return (owner, Regex.Replace(type.Name, "[^A-Za-z0-9_]", "_"));
    }

    private static string Compose(
        UpdateComponent component, DbContext db, string folder, int? from, int to, string owner, IReadOnlyList<string> statements,
        IReadOnlyList<string>? baselineTables = null)
    {
        DatabaseProvider provider = Providers.Single(p => p.Folder == folder).Provider;
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        string versionTable = sql.DelimitIdentifier(component.Definition.VersionTable);
        string versionColumn = sql.DelimitIdentifier("Version");
        string idColumn = sql.DelimitIdentifier("Id");
        string label = from is null
            ? $"{component.Name} baseline (schema version {to})"
            : $"{component.Name} update {to.ToString("0000", CultureInfo.InvariantCulture)} (schema version {from} to {to})";

        var blocks = new List<string>();
        bool transactional = provider is DatabaseProvider.Sqlite or DatabaseProvider.PostgreSql;
        if (transactional)
        {
            blocks.Add("BEGIN;");
        }

        blocks.Add(from is { } required
            ? $"-- guard: refuse any database that is not recorded at schema version {required}\n" +
              Guard(provider, sql, versionTable, versionColumn, idColumn, component.Name, required, label).TrimEnd('\n')
            : "-- guard: refuse any database that already holds a table of this component (nothing is changed)\n" +
              BaselineGuard(provider, sql, baselineTables ?? throw new InvalidOperationException("a baseline needs its table list"), component.Name, label).TrimEnd('\n'));
        foreach (string statement in statements)
        {
            string text = Normalize(statement).Trim();
            blocks.Add(text.EndsWith(';') ? text : text + ";");
        }

        // A baseline records version 0 ("creating") right after the version table, as the bootstrapper does, so an interrupted
        // create is resumable; the real version is written last.
        int previous = from ?? 0;
        blocks.Add($"-- the version row is written last\nUPDATE {versionTable} SET {versionColumn} = {to.ToString(CultureInfo.InvariantCulture)} " +
                   $"WHERE {idColumn} = 1 AND {versionColumn} = {previous.ToString(CultureInfo.InvariantCulture)};");

        if (transactional)
        {
            blocks.Add("COMMIT;");
        }

        string bodyText = string.Join("\n\n", blocks) + "\n";
        string apply = provider switch
        {
            DatabaseProvider.Sqlite => "sqlite3 -bail <database-file> < this-file.sql",
            DatabaseProvider.PostgreSql => "psql -v ON_ERROR_STOP=1 -d <database> -f this-file.sql",
            _ => "mysql <database> < this-file.sql   (the mysql/mariadb client; stops at the first error)",
        };
        var header = new StringBuilder();
        header.Append("-- ArcaneCore SQL update, generated by 'arcane-db export-updates'. Do not edit; see sql/updates/README.md.\n");
        header.Append($"-- component: {component.Name}\n");
        header.Append($"-- provider: {folder}\n");
        header.Append($"-- from: {(from is null ? "none (an empty database)" : from.Value.ToString(CultureInfo.InvariantCulture))}\n");
        header.Append($"-- to: {to.ToString(CultureInfo.InvariantCulture)}\n");
        header.Append($"-- owner: {owner}\n");
        header.Append($"-- body-sha256: {Hash(bodyText)}\n");
        header.Append($"-- apply: {apply}\n");
        return header + "\n" + bodyText;
    }

    /// <summary>Refuse (before anything, including ALTER DATABASE) a database that already holds the version table or any table of the component.</summary>
    private static string BaselineGuard(DatabaseProvider provider, ISqlGenerationHelper sql, IReadOnlyList<string> tables, string component, string label)
    {
        string list = string.Join(", ", tables.OrderBy(t => t, StringComparer.Ordinal).Select(t => "'" + t.Replace("'", "''", StringComparison.Ordinal) + "'"));
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
            {
                string column = sql.DelimitIdentifier($"requires_no_{component}_tables");
                string table = sql.DelimitIdentifier("arcane_update_guard");
                return $"CREATE TEMP TABLE {table} ({column} INTEGER CHECK ({column} = 1));\n" +
                       $"INSERT INTO {table} SELECT CASE WHEN (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ({list})) = 0 THEN 1 ELSE 0 END;\n" +
                       $"DROP TABLE {table};\n";
            }

            case DatabaseProvider.PostgreSql:
                return "DO $$\nBEGIN\n" +
                       $"  IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name IN ({list})) THEN\n" +
                       $"    RAISE EXCEPTION '{label}: the database already holds tables of this component';\n" +
                       "  END IF;\nEND\n$$;\n";

            default:
                return "DELIMITER //\nBEGIN NOT ATOMIC\n" +
                       $"  IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ({list})) THEN\n" +
                       $"    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '{label}: the database already holds tables of this component';\n" +
                       "  END IF;\nEND//\nDELIMITER ;\n";
        }
    }

    private static string Guard(
        DatabaseProvider provider, ISqlGenerationHelper sql, string versionTable, string versionColumn, string idColumn,
        string component, int required, string label)
    {
        string req = required.ToString(CultureInfo.InvariantCulture);
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
            {
                string column = sql.DelimitIdentifier($"requires_{component}_schema_version_{req}");
                string table = sql.DelimitIdentifier("arcane_update_guard");
                return $"CREATE TEMP TABLE {table} ({column} INTEGER CHECK ({column} = 1));\n" +
                       $"INSERT INTO {table} SELECT CASE WHEN (SELECT {versionColumn} FROM {versionTable} WHERE {idColumn} = 1) = {req} THEN 1 ELSE 0 END;\n" +
                       $"DROP TABLE {table};\n";
            }

            case DatabaseProvider.PostgreSql:
                return "DO $$\nBEGIN\n" +
                       $"  IF (SELECT {versionColumn} FROM {versionTable} WHERE {idColumn} = 1) IS DISTINCT FROM {req} THEN\n" +
                       $"    RAISE EXCEPTION '{label}: the database is not at schema version {req}';\n" +
                       "  END IF;\nEND\n$$;\n";

            default:
                return "DELIMITER //\nBEGIN NOT ATOMIC\n" +
                       $"  IF IFNULL((SELECT {versionColumn} FROM {versionTable} WHERE {idColumn} = 1), -1) <> {req} THEN\n" +
                       $"    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '{label}: the database is not at schema version {req}';\n" +
                       "  END IF;\nEND//\nDELIMITER ;\n";
        }
    }
}
