using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Schema;

/// <summary>The single row of a component's schema-version table.</summary>
public sealed class SchemaVersionRow
{
    public int Id { get; set; } = 1;

    public int Version { get; set; }
}

/// <summary>A change that brings a component's schema to <see cref="SchemaStep.Version"/>.</summary>
public abstract record SchemaChange;

/// <summary>Create a table exactly as the current model defines it.</summary>
public sealed record CreateTableChange(string Table) : SchemaChange;

/// <summary>Add a column exactly as the current model defines it (NOT NULL columns get the CLR default).</summary>
public sealed record AddColumnChange(string Table, string Column) : SchemaChange;

/// <summary>The changes that move a schema from <c>Version - 1</c> to <see cref="Version"/>.</summary>
public sealed record SchemaStep(int Version, IReadOnlyList<SchemaChange> Changes);

/// <summary>What a component's schema looks like and how it evolved.</summary>
public sealed class SchemaDefinition
{
    /// <summary>Component name, also the version table prefix ("auth" → auth_schema).</summary>
    public required string Component { get; init; }

    /// <summary>The version the current code requires.</summary>
    public required int CurrentVersion { get; init; }

    /// <summary>
    /// Tables of the version-1 schema. A database that has all of them but no version table
    /// was created by M1–M4 (EnsureCreated) and is adopted as version 1.
    /// </summary>
    public required IReadOnlyList<string> Version1Tables { get; init; }

    /// <summary>Upgrade steps for versions 2..CurrentVersion, in order.</summary>
    public IReadOnlyList<SchemaStep> Steps { get; init; } = [];

    public string VersionTable => Component + "_schema";
}

/// <summary>Raised when a database cannot be brought to the version the code requires.</summary>
public sealed class SchemaMismatchException(string message) : Exception(message);

/// <summary>
/// Creates, adopts and upgrades one component's schema inside a database that other
/// components may share. Each component owns a <c>&lt;component&gt;_schema</c> table with one
/// version row; anything unexpected fails closed (ROADMAP § Persistence).
/// <para>
/// This replaces <c>EnsureCreated</c>, which skips a context entirely as soon as the database
/// has any table — so a second context sharing the database never got its tables.
/// </para>
/// </summary>
public static class SchemaBootstrapper
{
    /// <summary>Configure the version table for a context's model.</summary>
    public static void MapVersionTable(ModelBuilder modelBuilder, SchemaDefinition definition)
    {
        modelBuilder.Entity<SchemaVersionRow>(entity =>
        {
            entity.ToTable(definition.VersionTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });
    }

    public static async Task EnsureAsync(
        DbContext db, SchemaDefinition definition, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
        }

        int? version = await TryReadVersionAsync(db, definition, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            version = await CreateOrAdoptAsync(db, creator, definition, logger, cancellationToken).ConfigureAwait(false);
        }

        if (version > definition.CurrentVersion)
        {
            throw new SchemaMismatchException(
                $"The {definition.Component} database is at schema version {version}, newer than this build " +
                $"({definition.CurrentVersion}). Run a newer ArcaneCore or restore a matching backup.");
        }

        foreach (SchemaStep step in definition.Steps.Where(s => s.Version > version).OrderBy(s => s.Version))
        {
            if (step.Version != version + 1)
            {
                throw new SchemaMismatchException(
                    $"No upgrade path for the {definition.Component} schema from version {version} to {step.Version}.");
            }

            await ApplyStepAsync(db, step, cancellationToken).ConfigureAwait(false);
            await WriteVersionAsync(db, step.Version, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Upgraded {Component} schema to version {Version}", definition.Component, step.Version);
            version = step.Version;
        }

        if (version != definition.CurrentVersion)
        {
            throw new SchemaMismatchException(
                $"The {definition.Component} schema is at version {version} but this build needs " +
                $"{definition.CurrentVersion} and has no upgrade path.");
        }
    }

    private static async Task<int?> TryReadVersionAsync(DbContext db, SchemaDefinition definition, CancellationToken ct)
    {
        if (!await TableExistsAsync(db, definition.VersionTable, ct).ConfigureAwait(false))
        {
            return null;
        }

        SchemaVersionRow? row = await db.Set<SchemaVersionRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
        return row?.Version ?? throw new SchemaMismatchException(
            $"{definition.VersionTable} exists but holds no version row; the {definition.Component} schema is in an unknown state.");
    }

    private static async Task<int> CreateOrAdoptAsync(
        DbContext db, IRelationalDatabaseCreator creator, SchemaDefinition definition, ILogger? logger, CancellationToken ct)
    {
        int present = 0;
        foreach (string table in definition.Version1Tables)
        {
            if (await TableExistsAsync(db, table, ct).ConfigureAwait(false))
            {
                present++;
            }
        }

        if (present == 0)
        {
            // Fresh: create every table of the current model, then record the current version.
            await creator.CreateTablesAsync(ct).ConfigureAwait(false);
            await WriteVersionAsync(db, definition.CurrentVersion, ct).ConfigureAwait(false);
            logger?.LogInformation("Created {Component} schema version {Version}", definition.Component, definition.CurrentVersion);
            return definition.CurrentVersion;
        }

        if (present == definition.Version1Tables.Count)
        {
            // Pre-M5 database (EnsureCreated, no version table): its layout is version 1.
            await ExecuteAsync(db, [new CreateTableChange(definition.VersionTable)], ct).ConfigureAwait(false);
            await WriteVersionAsync(db, 1, ct).ConfigureAwait(false);
            logger?.LogInformation("Adopted existing {Component} tables as schema version 1", definition.Component);
            return 1;
        }

        throw new SchemaMismatchException(
            $"The {definition.Component} database has {present} of the {definition.Version1Tables.Count} expected tables " +
            "and no version table. Refusing to guess; repair or recreate it.");
    }

    private static Task ApplyStepAsync(DbContext db, SchemaStep step, CancellationToken ct) => ExecuteAsync(db, step.Changes, ct);

    /// <summary>Turn model-derived changes into provider DDL with EF's own migrations SQL generator.</summary>
    private static async Task ExecuteAsync(DbContext db, IReadOnlyList<SchemaChange> changes, CancellationToken ct)
    {
        IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        IReadOnlyList<MigrationOperation> all = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model);
        var createTables = all.OfType<CreateTableOperation>().ToDictionary(t => t.Name, StringComparer.Ordinal);

        var operations = new List<MigrationOperation>();
        foreach (SchemaChange change in changes)
        {
            switch (change)
            {
                case CreateTableChange create:
                    operations.Add(createTables.GetValueOrDefault(create.Table)
                        ?? throw new InvalidOperationException($"table {create.Table} is not in the model"));
                    break;

                case AddColumnChange add:
                    CreateTableOperation table = createTables.GetValueOrDefault(add.Table)
                        ?? throw new InvalidOperationException($"table {add.Table} is not in the model");
                    AddColumnOperation column = table.Columns.FirstOrDefault(c => c.Name == add.Column)
                        ?? throw new InvalidOperationException($"column {add.Table}.{add.Column} is not in the model");
                    if (await ColumnExistsAsync(db, table.Name, add.Column, ct).ConfigureAwait(false))
                    {
                        // A table created by an earlier step from the current model already
                        // has the column (an upgrade across both steps): nothing to add.
                        break;
                    }

                    column.Table = table.Name;
                    column.Schema = table.Schema;
                    if (!column.IsNullable && column.DefaultValue is null && column.DefaultValueSql is null)
                    {
                        // Existing rows need a value; SQLite also refuses NOT NULL without a default.
                        column.DefaultValue = column.ClrType.IsValueType ? Activator.CreateInstance(column.ClrType) : string.Empty;
                    }

                    operations.Add(column);
                    break;

                default:
                    throw new InvalidOperationException($"unsupported schema change {change}");
            }
        }

        IReadOnlyList<MigrationCommand> commands = db.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, db.GetService<IDesignTimeModel>().Model);
        foreach (MigrationCommand command in commands)
        {
            await db.Database.ExecuteSqlRawAsync(command.CommandText, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteVersionAsync(DbContext db, int version, CancellationToken ct)
    {
        DbSet<SchemaVersionRow> set = db.Set<SchemaVersionRow>();
        SchemaVersionRow? row = await set.FirstOrDefaultAsync(r => r.Id == 1, ct).ConfigureAwait(false);
        if (row is null)
        {
            set.Add(new SchemaVersionRow { Id = 1, Version = version });
        }
        else
        {
            row.Version = version;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Whether a table exists, asked of the engine's catalog (so a missing table is a plain
    /// "no", not a failed command in the logs).
    /// </summary>
    private static async Task<bool> TableExistsAsync(DbContext db, string table, CancellationToken ct)
    {
        string sql = db.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" =>
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
            "Pomelo.EntityFrameworkCore.MySql" =>
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name",
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name",
            _ => throw new NotSupportedException($"no catalog query for provider {db.Database.ProviderName}"),
        };

        return await CatalogCountAsync(db, sql, ("@name", table), ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Whether a column exists, asked of the engine's catalog.</summary>
    private static async Task<bool> ColumnExistsAsync(DbContext db, string table, string column, CancellationToken ct)
    {
        string sql = db.Database.ProviderName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" =>
                "SELECT COUNT(*) FROM pragma_table_info(@name) WHERE name = @column",
            "Pomelo.EntityFrameworkCore.MySql" =>
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @name AND column_name = @column",
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @name AND column_name = @column",
            _ => throw new NotSupportedException($"no catalog query for provider {db.Database.ProviderName}"),
        };

        return await CatalogCountAsync(db, sql, ("@name", table), ct, ("@column", column)).ConfigureAwait(false) > 0;
    }

    private static async Task<long> CatalogCountAsync(
        DbContext db, string sql, (string Name, string Value) first, CancellationToken ct, (string Name, string Value)? second = null)
    {
        DbConnection connection = db.Database.GetDbConnection();
        bool opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            opened = true;
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string Name, string Value) p in second is { } extra ? new[] { first, extra } : new[] { first })
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = p.Name;
                parameter.Value = p.Value;
                command.Parameters.Add(parameter);
            }

            object? result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
