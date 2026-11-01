using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Kernel.Characters;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>Shared staged-save fence used by core, economy and quest transactions.</summary>
internal static class CharacterOwnershipGuard
{
    private static readonly ConditionalWeakTable<IDbContextOptions, object> FenceActive = new();
    private static readonly object Yes = new();
    private static readonly object No = new();

    /// <summary>
    /// True when the write fence's transaction interceptor is attached to this context, i.e. a transaction EF begins on it has already
    /// been verified. Cached per options instance (one per service provider), so the save path pays a lookup, not a scan.
    /// </summary>
    internal static bool IsFenceActive(DbContext db)
    {
        IDbContextOptions options = db.GetService<IDbContextOptions>();
        return ReferenceEquals(FenceActive.GetValue(options, static o => Scan(o) ? Yes : No), Yes);
    }

    private static bool Scan(IDbContextOptions options)
    {
        foreach (IDbContextOptionsExtension extension in options.Extensions)
        {
            if (extension is CoreOptionsExtension { Interceptors: { } interceptors })
            {
                foreach (IInterceptor interceptor in interceptors)
                {
                    if (interceptor is CharacterWriteFenceInterceptor)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The explicit check for a transaction the caller began with <see cref="CharacterWriteFence.BeginTransactionAsync(DbContext, CharacterOwnershipToken?, CancellationToken)"/>:
    /// skipped only when the interceptor already verified that very transaction against that very token: the store began it, the snapshot
    /// carries a token (a null token makes the interceptor verify the ambient one, which is not the snapshot's claim) and the fence is on.
    /// Fence off with no token (the live default): nothing is checked and nothing is issued, so the save is the SQL it was before the fence existed;
    /// a world with fencing off watches the ownership row once per autosave interval instead (WorldHost). Otherwise run: a tokened snapshot whose
    /// transaction the interceptor did not verify, or a transaction the caller joined.
    /// </summary>
    internal static Task VerifyUnlessFencedAsync(CharacterDbContext db, CharacterOwnershipToken? token, bool beganFenced, CancellationToken cancellationToken)
        => token is null && !IsFenceActive(db) ? Task.CompletedTask // fence off, untokened: exactly the SQL train 16 issued, no ownership statement
            : beganFenced && token is not null && IsFenceActive(db) ? Task.CompletedTask : VerifyAsync(db, token, cancellationToken);

    public static async Task VerifyAsync(CharacterDbContext db, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        // The schema row doubles as a serialized inactive gate. A local-mode save locks it for
        // the caller's transaction, so a claim cannot slip in after the missing-token decision.
        // A tokened save is checked against its own scope's row (the combined World's 'world', or a worker shard's); an untokened one against 'world'.
        string scope = token?.Scope ?? CharacterOwnershipScopes.World;
        CharacterOwnershipRow? row = await db.Set<CharacterOwnershipRow>()
            .AsNoTracking().SingleOrDefaultAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
        // A worker row is inserted at first claim, so a missing one under a token is a stale token, not an uninitialised schema.
        if (row is null && string.Equals(scope, CharacterOwnershipScopes.World, StringComparison.Ordinal))
            throw new InvalidOperationException("the characters ownership fence row is missing; schema initialization is incomplete");

        bool stale;
        if (db.Database.IsSqlite())
        {
            // SQLite has a single writer, so bumping the row costs nothing extra and takes the write lock
            // up front; a claim or takeover can then neither slip in between this check and the caller's writes.
            stale = await BumpAsync(db, token, cancellationToken).ConfigureAwait(false) == 0;
        }
        else
        {
            // MariaDB/PostgreSQL: a SHARED row lock held to the end of the caller's transaction. It still makes an
            // acquire, takeover or release (which UPDATE the row) wait for in-flight saves, but concurrent saves do
            // not serialize behind one row, and Serializable read-then-fence transactions gain no deadlock edge.
            stale = !await LockSharedAsync(db, db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction(), token, cancellationToken).ConfigureAwait(false);
        }

        if (stale)
            throw new CharacterOwnershipRejectedException(scope, token?.Epoch ?? row!.Epoch);
    }

    /// <summary>
    /// The same check as <see cref="VerifyAsync"/>, run with raw commands on a transaction EF has just begun and not yet handed to
    /// <c>Database.CurrentTransaction</c> (the write fence's transaction interceptor runs in that window, where a context query would
    /// not join the transaction). Provider behaviour is identical: SQLite bumps the sequence (taking the write lock up front), MariaDB
    /// and PostgreSQL take a shared row lock held to the end of the transaction.
    /// </summary>
    internal static async Task VerifyOnTransactionAsync(DbContext db, DbConnection connection, DbTransaction transaction, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        bool ok;
        if (db.Database.IsSqlite())
        {
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            string table = sql.DelimitIdentifier(CharacterOwnershipDataModule.Table);
            string sequence = sql.DelimitIdentifier("write_sequence");
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            if (token is null)
            {
                // Tokenless: allowed only while no row at all is active (the 'world' row and every worker shard row).
                command.CommandText = $"UPDATE {table} SET {sequence} = {sequence} + 1 WHERE {sql.DelimitIdentifier("scope")} = 'world' "
                    + $"AND {sql.DelimitIdentifier("active")} = 0 AND {sequence} < {long.MaxValue} "
                    + $"AND NOT EXISTS (SELECT 1 FROM {table} WHERE {sql.DelimitIdentifier("active")} = 1)";
            }
            else
            {
                command.CommandText = $"UPDATE {table} SET {sequence} = {sequence} + 1 WHERE {sql.DelimitIdentifier("scope")} = @scope "
                    + $"AND {sql.DelimitIdentifier("active")} = 1 AND {sql.DelimitIdentifier("epoch")} = @epoch "
                    + $"AND {sql.DelimitIdentifier("owner_incarnation")} = @incarnation";
                AddParameter(command, "@scope", token.Scope);
                AddParameter(command, "@epoch", token.Epoch);
                AddParameter(command, "@incarnation", token.Incarnation.ToString("D"));
            }

            ok = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        else
        {
            ok = await LockSharedAsync(db, connection, transaction, token, cancellationToken).ConfigureAwait(false);
        }

        if (ok)
            return;

        // Refused: say whether that is a stale token or a database that was never initialised.
        string scope = token?.Scope ?? CharacterOwnershipScopes.World;
        long? epoch = await ReadEpochAsync(db, connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        if (epoch is null && string.Equals(scope, CharacterOwnershipScopes.World, StringComparison.Ordinal))
            throw new InvalidOperationException("the characters ownership fence row is missing; schema initialization is incomplete");
        throw new CharacterOwnershipRejectedException(scope, token?.Epoch ?? epoch!.Value);
    }

    private static async Task<long?> ReadEpochAsync(DbContext db, DbConnection connection, DbTransaction transaction, string scope, CancellationToken cancellationToken)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {sql.DelimitIdentifier("epoch")} FROM {sql.DelimitIdentifier(CharacterOwnershipDataModule.Table)} "
            + $"WHERE {sql.DelimitIdentifier("scope")} = @scope";
        AddParameter(command, "@scope", scope);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<int> BumpAsync(CharacterDbContext db, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        if (token is null)
        {
            return await db.Set<CharacterOwnershipRow>()
                .Where(r => r.Scope == "world" && !r.Active && r.WriteSequence < long.MaxValue
                    && !db.Set<CharacterOwnershipRow>().Any(o => o.Active))
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
                .ConfigureAwait(false);
        }

        return await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == token.Scope && r.Active && r.Epoch == token.Epoch
                && r.OwnerIncarnation == token.Incarnation.ToString("D"))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> LockSharedAsync(DbContext db, DbConnection connection, DbTransaction? transaction, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        string table = sql.DelimitIdentifier(CharacterOwnershipDataModule.Table);
        string scope = sql.DelimitIdentifier("scope");
        string active = sql.DelimitIdentifier("active");
        string lockClause = db.Database.ProviderName switch
        {
            "Pomelo.EntityFrameworkCore.MySql" => " LOCK IN SHARE MODE",
            "Npgsql.EntityFrameworkCore.PostgreSQL" => " FOR SHARE",
            _ => throw new NotSupportedException($"no ownership fence lock for provider {db.Database.ProviderName}"),
        };

        bool isMariaDb = db.Database.ProviderName == "Pomelo.EntityFrameworkCore.MySql";
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        void Add(string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        if (token is null)
        {
            // Tokenless: allowed only while no row at all is active. Two statements, in this order. (1) Share-lock the inactive 'world' row: from here
            // a worker claim's gate UPDATE waits behind this lock until the caller's transaction ends. If a claim held the row first, this blocks
            // until it commits, then the row's re-check sees it still inactive (the claim only bumps its sequence), so (1) alone cannot see the claim.
            // (2) therefore asks "is any row active" as a separate statement taken after the lock: under READ COMMITTED it gets a fresh snapshot, and
            // on MariaDB it is a locking read (a current read), so the REPEATABLE READ snapshot cannot hide a claim that committed while (1) waited.
            command.CommandText = $"SELECT 1 FROM {table} WHERE {scope} = 'world' AND {active} = FALSE "
                + $"AND {sql.DelimitIdentifier("write_sequence")} < {long.MaxValue}{lockClause}";
            await DbConnectionOpen.EnsureOpenAsync(db.Database, cancellationToken).ConfigureAwait(false);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                return false;

            await using DbCommand anyActive = connection.CreateCommand();
            anyActive.Transaction = transaction;
            anyActive.CommandText = $"SELECT 1 FROM {table} WHERE {active} = TRUE{(isMariaDb ? lockClause : string.Empty)}";
            return await anyActive.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null;
        }
        else
        {
            if (!CharacterOwnershipScopes.IsValid(token.Scope))
                return false;
            command.CommandText = $"SELECT 1 FROM {table} WHERE {scope} = @scope AND {active} = TRUE "
                + $"AND {sql.DelimitIdentifier("epoch")} = @epoch AND {sql.DelimitIdentifier("owner_incarnation")} = @incarnation{lockClause}";
            Add("@scope", token.Scope);
            Add("@epoch", token.Epoch);
            Add("@incarnation", token.Incarnation.ToString("D"));
        }

        await DbConnectionOpen.EnsureOpenAsync(db.Database, cancellationToken).ConfigureAwait(false);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }
}
