using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Rename;

/// <summary>The bits of vmangos' <c>characters.at_login</c> (AtLoginFlags, Player.h:600); only the rename bit is used here.</summary>
public static class CharacterAtLoginFlags
{
    /// <summary>AT_LOGIN_RENAME: "Rename character at login".</summary>
    public const uint Rename = 0x01;
}

/// <summary>The result of <see cref="ICharacterRenameStore.RenameAsync"/>.</summary>
public enum CharacterRenameOutcome
{
    /// <summary>The name was changed and the rename flag cleared in one commit.</summary>
    Renamed,

    /// <summary>No such character on the account, or it carries no rename flag (vmangos: the validation query returns no row).</summary>
    NotAllowed,

    /// <summary>Another character already has the name (vmangos: the validation query's NOT EXISTS clause).</summary>
    NameTaken,
}

/// <summary>The outcome of a rename, with the name the character had when one was found.</summary>
public readonly record struct CharacterRenameResult(CharacterRenameOutcome Outcome, string? OldName = null);

/// <summary>Persistence of the at-login flags and of the rename they allow (docs/areas/character-rename.md).</summary>
public interface ICharacterRenameStore
{
    /// <summary>
    /// Set <paramref name="flag"/> on the character (vmangos <c>at_login = at_login | flag</c>). False when the
    /// character does not exist. Idempotent.
    /// </summary>
    Task<bool> SetFlagAsync(int characterId, uint flag, CancellationToken cancellationToken = default);

    /// <summary>The non-zero at-login flags of the characters of an account (a character without a row has none).</summary>
    Task<IReadOnlyDictionary<int, uint>> GetFlagsAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// vmangos HandleCharRenameOpcode's database half, atomically: the character must belong to
    /// <paramref name="accountId"/> and carry <see cref="CharacterAtLoginFlags.Rename"/>, and no other character may have
    /// <paramref name="newName"/> (case-insensitively); then the name changes and the flag is cleared in one commit.
    /// A creation or rename that wins the unique index on the name in between reports <see cref="CharacterRenameOutcome.NameTaken"/>.
    /// </summary>
    Task<CharacterRenameResult> RenameAsync(int characterId, int accountId, string newName, CancellationToken cancellationToken = default);
}

/// <summary>One <c>character_at_login</c> row: the flags of one character (vmangos keeps them in <c>characters.at_login</c>).</summary>
public sealed class CharacterAtLoginRow
{
    public int CharacterId { get; set; }

    public uint Flags { get; set; }
}

/// <summary>
/// The <c>character_at_login</c> table of the characters database: the at-login flags (today only the forced-rename
/// bit) in a table of their own, so the shared <c>characters</c> row stays untouched.
/// <see cref="Version"/> is the single constant the integrator renumbers (the next free characters version after
/// <see cref="Life.CharacterRestDataModule"/>'s).
/// </summary>
public sealed class CharacterRenameDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single place the schema version is set.</summary>
    public const int Version = 27;

    public const string Table = "character_at_login";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterAtLoginRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Flags).HasColumnName("at_login");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterRenameStore, EfCharacterRenameStore>();

    /// <summary>The row of the character, so a reused id does not inherit a forced rename.</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterAtLoginRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="ICharacterRenameStore"/>.</summary>
public sealed class EfCharacterRenameStore(CharacterDbContext db) : ICharacterRenameStore
{
    public async Task<bool> SetFlagAsync(int characterId, uint flag, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            DbSet<CharacterAtLoginRow> set = db.Set<CharacterAtLoginRow>();
            CharacterAtLoginRow? row = await set.FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                set.Add(new CharacterAtLoginRow { CharacterId = characterId, Flags = flag });
            }
            else
            {
                row.Flags |= flag;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyDictionary<int, uint>> GetFlagsAsync(int accountId, CancellationToken cancellationToken = default)
    {
        var flags = await db.Characters.AsNoTracking()
            .Where(c => c.AccountId == accountId)
            .Join(db.Set<CharacterAtLoginRow>().AsNoTracking(), c => c.Id, r => r.CharacterId, (c, r) => new { c.Id, r.Flags })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return flags.Where(f => f.Flags != 0).ToDictionary(f => f.Id, f => f.Flags);
    }

    public async Task<CharacterRenameResult> RenameAsync(int characterId, int accountId, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(newName);
        try
        {
            CharacterRecord? character = await db.Characters
                .FirstOrDefaultAsync(c => c.Id == characterId && c.AccountId == accountId, cancellationToken).ConfigureAwait(false);
            CharacterAtLoginRow? flags = character is null
                ? null
                : await db.Set<CharacterAtLoginRow>().FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (character is null || flags is null || (flags.Flags & CharacterAtLoginFlags.Rename) == 0)
            {
                return new CharacterRenameResult(CharacterRenameOutcome.NotAllowed);
            }

            string lower = newName.ToLowerInvariant();
            if (await db.Characters.AnyAsync(c => c.Name.ToLower() == lower, cancellationToken).ConfigureAwait(false))
            {
                return new CharacterRenameResult(CharacterRenameOutcome.NameTaken, character.Name);
            }

            string oldName = character.Name;
            character.Name = newName;
            flags.Flags &= ~CharacterAtLoginFlags.Rename;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new CharacterRenameResult(CharacterRenameOutcome.Renamed, oldName);
        }
        catch (DbUpdateException ex) when (EfCharacterStore.IsUniqueViolation(ex))
        {
            // A creation or rename of the same name committed between the check and the update (unique index on characters.name).
            return new CharacterRenameResult(CharacterRenameOutcome.NameTaken);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
