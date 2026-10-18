using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// One parameterized statement run once per row inside the context's current transaction, for a hot batch writer on SQLite. A LINQ
/// <c>ExecuteUpdate</c> or a tracked <c>SaveChanges</c> per row costs an expression-tree walk, a query-cache lookup and change tracking each time;
/// under a CPU-starved host that work happens while the SQLite write lock is held, which stretched batches of 128 managed-bot updates
/// to 2-10 s on the live server (SlowTransactionInterceptor, 2026-10-17). Here the SQL is built once from the EF model (table and column
/// names, identifier quoting) and each row runs it through <c>ExecuteSqlRaw</c>: no LINQ translation, no tracking, only parameter binding
/// and SQLite's own (microsecond) statement preparation.
/// <para>
/// Values are bound through each property's EF type mapping (<see cref="RelationalTypeMapping.CreateParameter"/>), the same path EF
/// uses for its own parameters, so value converters (a <see cref="Guid"/> stored as TEXT, an enum stored as its number) bind exactly as
/// EF would write them. The statement runs through EF on the context's connection and current transaction, so every command interceptor
/// (slow-command log, write fence, write gate, test fault injection) sees it as it sees any other EF statement. Not thread-safe: one
/// instance per transaction, disposed with it.
/// </para>
/// </summary>
internal sealed class SqliteRowCommand : IDisposable
{
    private readonly DbContext _db;
    private readonly DbCommand _factory; // only creates parameters; never executed
    private readonly IProperty[] _parameters;

    private SqliteRowCommand(DbContext db, string sql, IProperty[] parameters)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A SqliteRowCommand runs inside the context's transaction; begin one first.");
        _db = db;
        _factory = db.Database.GetDbConnection().CreateCommand();
        _parameters = parameters;
        Sql = sql;
    }

    /// <summary>The statement text (for tests and diagnostics).</summary>
    public string Sql { get; }

    /// <summary>
    /// <c>UPDATE t SET set0 = @p0, ... WHERE where0 = @pN AND ...</c>; <see cref="ExecuteAsync"/> takes the set values, then the where values,
    /// in the order named. A property may appear in both lists (an optimistic revision is set to its new value where it equals the old one).
    /// </summary>
    public static SqliteRowCommand Update(DbContext db, Type entity, IReadOnlyList<string> set, IReadOnlyList<string> where)
    {
        ArgumentNullException.ThrowIfNull(db);
        (IEntityType type, StoreObjectIdentifier table, ISqlGenerationHelper sql) = Resolve(db, entity);
        var parameters = new List<IProperty>(set.Count + where.Count);
        var text = new System.Text.StringBuilder("UPDATE ").Append(TableName(type, sql)).Append(" SET ");
        for (int i = 0; i < set.Count; i++)
        {
            IProperty property = Property(type, set[i]);
            text.Append(i == 0 ? "" : ", ").Append(Column(property, table, sql)).Append(" = @p").Append(parameters.Count);
            parameters.Add(property);
        }

        text.Append(" WHERE ");
        for (int i = 0; i < where.Count; i++)
        {
            IProperty property = Property(type, where[i]);
            text.Append(i == 0 ? "" : " AND ").Append(Column(property, table, sql)).Append(" = @p").Append(parameters.Count);
            parameters.Add(property);
        }

        return new SqliteRowCommand(db, text.ToString(), [.. parameters]);
    }

    /// <summary>
    /// <c>INSERT INTO t (keys..., values...) VALUES (...) ON CONFLICT (keys...) DO UPDATE SET value = excluded.value, ...</c>: one statement
    /// replaces a read-then-update-or-insert. The keys must be the table's primary key or a unique index. <see cref="ExecuteAsync"/> takes
    /// the key values, then the other values.
    /// </summary>
    public static SqliteRowCommand Upsert(DbContext db, Type entity, IReadOnlyList<string> keys, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(db);
        (IEntityType type, StoreObjectIdentifier table, ISqlGenerationHelper sql) = Resolve(db, entity);
        IProperty[] parameters = [.. keys.Concat(values).Select(name => Property(type, name))];
        string[] columns = [.. parameters.Select(p => Column(p, table, sql))];
        string text = "INSERT INTO " + TableName(type, sql) + " (" + string.Join(", ", columns) + ") VALUES ("
            + string.Join(", ", Enumerable.Range(0, columns.Length).Select(i => "@p" + i)) + ") ON CONFLICT ("
            + string.Join(", ", columns.Take(keys.Count)) + ") DO "
            + (values.Count == 0
                ? "NOTHING"
                : "UPDATE SET " + string.Join(", ", columns.Skip(keys.Count).Select(c => c + " = excluded." + c)));
        return new SqliteRowCommand(db, text, parameters);
    }

    /// <summary>Runs the statement for one row and returns the rows it changed.</summary>
    public async Task<int> ExecuteAsync(object?[] values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != _parameters.Length)
            throw new ArgumentException($"The statement takes {_parameters.Length} values, not {values.Length}.", nameof(values));

        var parameters = new DbParameter[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            IProperty property = _parameters[i];
            parameters[i] = property.GetRelationalTypeMapping().CreateParameter(_factory, "@p" + i, values[i], property.IsNullable);
        }

        return await _db.Database.ExecuteSqlRawAsync(Sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _factory.Dispose();

    private static (IEntityType Type, StoreObjectIdentifier Table, ISqlGenerationHelper Sql) Resolve(DbContext db, Type entity)
    {
        IEntityType type = db.Model.FindEntityType(entity)
            ?? throw new InvalidOperationException($"{entity.Name} is not mapped by {db.GetType().Name}.");
        string name = type.GetTableName() ?? throw new InvalidOperationException($"{entity.Name} is not mapped to a table.");
        return (type, StoreObjectIdentifier.Table(name, type.GetSchema()), db.GetService<ISqlGenerationHelper>());
    }

    private static string TableName(IEntityType type, ISqlGenerationHelper sql) => sql.DelimitIdentifier(type.GetTableName()!, type.GetSchema());

    private static IProperty Property(IEntityType type, string name)
        => type.FindProperty(name) ?? throw new InvalidOperationException($"{type.ClrType.Name} has no property {name}.");

    private static string Column(IProperty property, in StoreObjectIdentifier table, ISqlGenerationHelper sql)
        => sql.DelimitIdentifier(property.GetColumnName(table)
            ?? throw new InvalidOperationException($"{property.Name} is not mapped to a column of {table.Name}."));
}
