using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Stores;

/// <summary>EF Core implementation of <see cref="ICharacterStore"/>.</summary>
public sealed class EfCharacterStore(CharacterDbContext db) : ICharacterStore
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

    public async Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default)
    {
        db.Characters.Add(character);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return character;
    }

    public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default)
        => DeleteAsync(id, accountId, CharacterDataCleanups.All, cancellationToken);

    /// <summary>
    /// Delete the character row, its action bar and every module's per-character rows
    /// (<see cref="ICharacterDataCleanup"/>) in one transaction: either all of it goes or none.
    /// A module's refusal answers false and leaves everything in place. <paramref name="cleanups"/> is
    /// normally <see cref="CharacterDataCleanups.All"/> (tests substitute their own).
    /// </summary>
    public async Task<bool> DeleteAsync(
        int id, int accountId, IReadOnlyList<ICharacterDataCleanup> cleanups, CancellationToken cancellationToken)
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
            .Select(c => new CharacterIdentity(c.Id, c.AccountId, c.Name, c.Race, c.Gender, c.Class))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
