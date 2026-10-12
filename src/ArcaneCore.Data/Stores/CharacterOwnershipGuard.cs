using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Kernel.Characters;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>Shared staged-save fence used by core, economy and quest transactions.</summary>
internal static class CharacterOwnershipGuard
{
    public static async Task VerifyAsync(CharacterDbContext db, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        // The schema row doubles as a serialized inactive gate. A local-mode save locks it for
        // the caller's transaction, so a claim cannot slip in after the missing-token decision.
        CharacterOwnershipRow? row = await db.Set<CharacterOwnershipRow>()
            .AsNoTracking().SingleOrDefaultAsync(r => r.Scope == "world", cancellationToken).ConfigureAwait(false);
        if (row is null)
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
            stale = !await LockSharedAsync(db, token, cancellationToken).ConfigureAwait(false);
        }

        if (stale)
            throw new CharacterOwnershipRejectedException(token?.Scope ?? "world", token?.Epoch ?? row.Epoch);
    }

    private static async Task<int> BumpAsync(CharacterDbContext db, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        if (token is null)
        {
            return await db.Set<CharacterOwnershipRow>()
                .Where(r => r.Scope == "world" && !r.Active && r.WriteSequence < long.MaxValue)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
                .ConfigureAwait(false);
        }

        return await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == token.Scope && r.Active && r.Epoch == token.Epoch
                && r.OwnerIncarnation == token.Incarnation.ToString("D"))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> LockSharedAsync(CharacterDbContext db, CharacterOwnershipToken? token, CancellationToken cancellationToken)
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

        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        void Add(string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        if (token is null)
        {
            command.CommandText = $"SELECT 1 FROM {table} WHERE {scope} = 'world' AND {active} = FALSE "
                + $"AND {sql.DelimitIdentifier("write_sequence")} < {long.MaxValue}{lockClause}";
        }
        else
        {
            if (!string.Equals(token.Scope, "world", StringComparison.Ordinal))
                return false;
            command.CommandText = $"SELECT 1 FROM {table} WHERE {scope} = @scope AND {active} = TRUE "
                + $"AND {sql.DelimitIdentifier("epoch")} = @epoch AND {sql.DelimitIdentifier("owner_incarnation")} = @incarnation{lockClause}";
            Add("@scope", token.Scope);
            Add("@epoch", token.Epoch);
            Add("@incarnation", token.Incarnation.ToString("D"));
        }

        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }
}
