using System.Data.Common;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// The per-character half of the write fence in Worker scope (design _wf/writer-lock-DESIGN.md section 1b): inside the transaction EF has just begun,
/// proves that the characters named by <see cref="CharacterWriteClaim"/> are claimed by the worker that is writing (its shard scope and process
/// incarnation), so a source that handed a character away, or lost it to a takeover, is refused durably. Raw commands on the connection and transaction,
/// like <see cref="CharacterOwnershipGuard.VerifyOnTransactionAsync"/>, because the interceptor runs before <c>Database.CurrentTransaction</c> is set.
/// <para>
/// Cost, Worker mode only (decision Q3): one indexed statement per character. SQLite: a no-op row <c>UPDATE</c> (the write lock is already held by the
/// ownership bump, and the technique matches <c>LeaseFencedWrite</c>). MariaDB and PostgreSQL: a shared row lock held to the end of the transaction, so a
/// transfer commit or a takeover (which UPDATE the claim row) waits for in-flight saves and cannot slip between this check and the caller's writes.
/// It does not require <c>active_transfer IS NULL</c> for the owner's own saves, because the settlement save runs while a transfer is reserved.
/// </para>
/// </summary>
internal static class CharacterClaimGuard
{
    internal static async Task VerifyAsync(
        DbContext db, DbConnection connection, DbTransaction transaction, CharacterOwnershipToken token, CharacterWriteClaim.Frame frame, CancellationToken cancellationToken)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        bool sqlite = db.Database.IsSqlite();
        string owner = sql.DelimitIdentifier(ClusterTransferDataModule.OwnerTable);
        string ownership = sql.DelimitIdentifier(CharacterOwnershipDataModule.Table);
        string lockClause = sqlite ? string.Empty : db.Database.ProviderName switch
        {
            "Pomelo.EntityFrameworkCore.MySql" => " LOCK IN SHARE MODE",
            "Npgsql.EntityFrameworkCore.PostgreSQL" => " FOR SHARE OF o",
            _ => throw new NotSupportedException($"no character claim lock for provider {db.Database.ProviderName}"),
        };
        string active = sqlite ? "1" : "TRUE";

        foreach (int characterId in frame.CharacterIds)
        {
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            Add(command, "@guid", (long)characterId);
            bool held;
            if (frame.Requirement == CharacterClaimRequirement.HeldByThisWorker)
            {
                Add(command, "@scope", token.Scope);
                Add(command, "@incarnation", token.Incarnation.ToString("D"));
                if (sqlite)
                {
                    command.CommandText = $"UPDATE {owner} SET {sql.DelimitIdentifier("updated_at_ms")} = {sql.DelimitIdentifier("updated_at_ms")} "
                        + $"WHERE {sql.DelimitIdentifier("character_guid")} = @guid AND {sql.DelimitIdentifier("scope")} = @scope "
                        + $"AND {sql.DelimitIdentifier("incarnation")} = @incarnation";
                    held = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
                }
                else
                {
                    command.CommandText = $"SELECT 1 FROM {owner} o WHERE o.{sql.DelimitIdentifier("character_guid")} = @guid "
                        + $"AND o.{sql.DelimitIdentifier("scope")} = @scope AND o.{sql.DelimitIdentifier("incarnation")} = @incarnation{lockClause}";
                    held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
                }
            }
            else
            {
                // A claim some live worker holds: its owner's ownership row is active under the claim's incarnation; no transfer is in flight.
                command.CommandText = $"SELECT 1 FROM {owner} o JOIN {ownership} w ON w.{sql.DelimitIdentifier("scope")} = o.{sql.DelimitIdentifier("scope")} "
                    + $"AND w.{sql.DelimitIdentifier("owner_incarnation")} = o.{sql.DelimitIdentifier("incarnation")} AND w.{sql.DelimitIdentifier("active")} = {active} "
                    + $"WHERE o.{sql.DelimitIdentifier("character_guid")} = @guid AND o.{sql.DelimitIdentifier("active_transfer")} IS NULL{lockClause}";
                held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
            }

            if (!held)
                throw frame.Requirement == CharacterClaimRequirement.HeldByAnyLiveWorker
                    ? CharacterClaimRejectedException.NotHeldByAnyLiveWorker(characterId, token.Scope)
                    : new CharacterClaimRejectedException(characterId, token.Scope);
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
