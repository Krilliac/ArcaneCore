using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>EF Core implementation of <see cref="ICharacterStore"/>.</summary>
public sealed class EfCharacterStore(CharacterDbContext db) : ICharacterStore, ICharacterDeletionStore
{
    public async Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(
        int accountId, CancellationToken cancellationToken = default)
    {
        return await db.Characters
            .AsNoTracking()
            .Where(c => c.AccountId == accountId)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default)
    {
        string lower = name.ToLowerInvariant();
        return await db.Characters.AnyAsync(c => c.Name.ToLower() == lower, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default)
        => await db.Characters.CountAsync(c => c.AccountId == accountId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Insert a character. A caller-supplied nonzero <c>Id</c> (an explicit id; the world never
    /// supplies one) is fenced: it is refused with <see cref="CharacterIdPendingDeletionException"/>
    /// while a deletion of that id is still pending finalization, so a late finalizer of the earlier
    /// lifetime cannot reach the new one. The insert comes first and the check follows in the same
    /// transaction, so a deletion committing concurrently (which the insert waits for) is always seen.
    /// Generated ids are not fenced (docs/integration/character-delete.md, Limits).
    /// </summary>
    public async Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (character.Id == 0)
        {
            try
            {
                db.Characters.Add(character);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return character;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A generated id cannot collide, so the unique index that fired is the one on the name.
                throw new CharacterNameTakenException(character.Name, ex);
            }
            finally
            {
                // A failed insert must not stay tracked: the next save on this scope would retry it.
                db.ChangeTracker.Clear();
            }
        }

        bool own = db.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null;
        await using IDbContextTransaction? transaction = own
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            db.Characters.Add(character);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (await db.Set<CharacterDeletionRow>().AnyAsync(r => r.CharacterId == character.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new CharacterIdPendingDeletionException(character.Id); // disposing the transaction rolls the insert back
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return character;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Whether an insert failed on a unique constraint, per engine: SQLite extended result code 2067
    /// (SQLITE_CONSTRAINT_UNIQUE) or 1555 (PRIMARY KEY), MySqlConnector error 1062 (duplicate entry),
    /// Npgsql SQLSTATE 23505 (unique_violation).
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case Microsoft.Data.Sqlite.SqliteException sqlite when sqlite.SqliteExtendedErrorCode is 2067 or 1555:
                case MySqlConnector.MySqlException mysql when mysql.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry:
                case Npgsql.PostgresException pg when pg.SqlState == "23505":
                    return true;
            }
        }

        return false;
    }

    public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default)
        => DeleteAsync(Guid.Empty, id, accountId, CharacterDataCleanups.All, cancellationToken);

    Task<bool> ICharacterDeletionStore.DeleteAsync(Guid operationId, int id, int accountId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A deletion operation needs a non-empty id.", nameof(operationId));
        }

        return DeleteAsync(operationId, id, accountId, CharacterDataCleanups.All, cancellationToken);
    }

    public async Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        string key = operationId.ToString("D");
        return await db.Set<CharacterDeletionRow>().AsNoTracking()
            .AnyAsync(r => r.OperationId == key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PendingCharacterDeletion>> GetPendingAsync(
        int accountId, CancellationToken cancellationToken = default)
    {
        List<CharacterDeletionRow> rows = await db.Set<CharacterDeletionRow>().AsNoTracking()
            .Where(r => r.AccountId == accountId)
            .OrderBy(r => r.CharacterId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new PendingCharacterDeletion(Guid.Parse(r.OperationId), r.CharacterId, r.AccountId, r.Name))];
    }

    public async Task CompleteAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        string key = operationId.ToString("D");
        await db.Set<CharacterDeletionRow>().Where(r => r.OperationId == key)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete the character row, its action bar and every module's per-character rows
    /// (<see cref="ICharacterDataCleanup"/>) in one transaction: either all of it goes or none.
    /// A module's refusal answers false and leaves everything in place. <paramref name="cleanups"/> is
    /// normally <see cref="CharacterDataCleanups.All"/> (tests substitute their own).
    /// </summary>
    public Task<bool> DeleteAsync(
        int id, int accountId, IReadOnlyList<ICharacterDataCleanup> cleanups, CancellationToken cancellationToken)
        => DeleteAsync(Guid.Empty, id, accountId, cleanups, cancellationToken);

    /// <summary>
    /// As the overload without an operation, and a non-empty <paramref name="operationId"/> is also
    /// written to <c>character_deletion</c> in the same transaction (the ledger row commits or rolls
    /// back with the deletion; in a caller-owned transaction the caller's outcome decides).
    /// <see cref="Guid.Empty"/> writes no ledger row.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid operationId, int id, int accountId, IReadOnlyList<ICharacterDataCleanup> cleanups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cleanups);
        if (db.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Character deletion requires a context without tracked caller state.");
        }

        // Join a caller's transaction (its commit decides); otherwise own one.
        bool own = db.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null;
        await using IDbContextTransaction? transaction = own
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            CharacterRecord? character = await db.Characters
                .FirstOrDefaultAsync(c => c.Id == id && c.AccountId == accountId, cancellationToken)
                .ConfigureAwait(false);
            if (character is null)
            {
                return false;
            }

            foreach (ICharacterDataCleanup cleanup in cleanups)
            {
                await cleanup.DeleteCharacterDataAsync(db, id, cancellationToken).ConfigureAwait(false);
            }

            db.ActionButtons.RemoveRange(db.ActionButtons.Where(b => b.CharacterId == id));
            db.Characters.Remove(character);
            if (operationId != Guid.Empty)
            {
                db.Set<CharacterDeletionRow>().Add(new CharacterDeletionRow
                {
                    OperationId = operationId.ToString("D"),
                    CharacterId = character.Id,
                    AccountId = accountId,
                    Name = character.Name,
                    CommittedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (CharacterDeletionRefusedException) when (transaction is not null)
        {
            return false; // disposing the transaction rolls back any set-based deletes
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
    {
        if (!await StageStateAsync(state, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    /// <summary>Stage state and inventory on this context, leaving the commit to the caller.</summary>
    internal async Task<bool> StageStateAsync(CharacterState state, CancellationToken cancellationToken)
    {
        CharacterRecord? character = await db.Characters
            .FirstOrDefaultAsync(c => c.Id == state.Id, cancellationToken)
            .ConfigureAwait(false);
        if (character is null)
        {
            return false; // deleted while the save was queued
        }

        character.MapId = state.MapId;
        character.ZoneId = state.ZoneId;
        character.X = state.X;
        character.Y = state.Y;
        character.Z = state.Z;
        character.Orientation = state.Orientation;
        character.Level = state.Level;
        character.PlayedTime = state.PlayedTime;
        character.LevelPlayedTime = state.LevelPlayedTime;
        character.Money = state.Money;
        character.ActionBarToggles = state.ActionBarToggles;
        character.BankBagSlotCount = state.BankBagSlotCount;
        // vmangos SaveToDB (Player.cpp:16427-16434): the ship and the offset on it, zeros on land.
        TransportSeat seat = state.Transport ?? default;
        character.TransportGuid = seat.Guid;
        character.TransportX = seat.X;
        character.TransportY = seat.Y;
        character.TransportZ = seat.Z;
        character.TransportOrientation = seat.Orientation;
        if (state.Home is { } home)
        {
            character.HomeMapId = home.MapId;
            character.HomeZoneId = home.ZoneId;
            character.HomeX = home.X;
            character.HomeY = home.Y;
            character.HomeZ = home.Z;
        }

        if (state.ActionButtons is { } buttons)
        {
            db.ActionButtons.RemoveRange(
                await db.ActionButtons.Where(b => b.CharacterId == state.Id).ToListAsync(cancellationToken).ConfigureAwait(false));
            foreach (ActionButton button in buttons)
            {
                db.ActionButtons.Add(new ActionButtonRow
                {
                    CharacterId = state.Id, Button = button.Button, Action = button.Action, Type = button.Type,
                });
            }
        }

        if (state.Inventory is { } inventory)
        {
            await ItemPersistence.StageReplaceAsync(db, state.Id, inventory, cancellationToken).ConfigureAwait(false);
        }

        if (state.Life is { } life)
        {
            // Same transaction as the rest of the snapshot: a ghost flag and its corpse row commit together.
            await CharacterLifePersistence.StageAsync(db, state.Id, life, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.ActionButtons.AsNoTracking()
            .Where(b => b.CharacterId == characterId)
            .OrderBy(b => b.Button)
            .Select(b => new ActionButton(b.Button, b.Action, b.Type))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default)
        => await db.Characters.AsNoTracking()
            .Select(c => new CharacterIdentity(c.Id, c.AccountId, c.Name, c.Race, c.Gender, c.Class, c.Level, c.ZoneId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
