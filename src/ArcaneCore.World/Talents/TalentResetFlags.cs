using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Rename;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Talents;

/// <summary>
/// The "reset the talents at the next login" request of a character: vmangos CHARACTER_FLAG_RESET_TALENTS_ON_LOGIN (Player.h:296),
/// set by <c>.reset talents</c> for an offline character and by <c>.reset all talents</c> (CharacterCommands.cpp:3910, :3980),
/// applied and cleared at login (CharacterHandler.cpp:661-665). Stored as <see cref="CharacterAtLoginFlags.ResetTalents"/> in the
/// <c>character_at_login</c> table, because the characters row has no <c>character_flags</c> column (docs/areas/talents.md).
/// Scoped: one instance per database scope.
/// </summary>
public interface ITalentResetFlagStore
{
    /// <summary>Flag one character. False when it does not exist. Idempotent.</summary>
    Task<bool> FlagAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Flag every character (vmangos: <c>WHERE (character_flags &amp; flag) = 0</c>). Returns how many were not flagged before.</summary>
    Task<int> FlagAllAsync(CancellationToken cancellationToken = default);

    Task<bool> IsFlaggedAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Clear the flag of one character (no-op when it is not set).</summary>
    Task ClearAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>Where the flag store of a scope comes from.</summary>
public static class TalentResetFlags
{
    /// <summary>
    /// The store of <paramref name="scope"/>: a registered <see cref="ITalentResetFlagStore"/>, else the EF store over the scope's
    /// characters database, else null (a host without a characters database cannot flag anything).
    /// </summary>
    public static ITalentResetFlagStore? Resolve(IServiceProvider scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.GetService<ITalentResetFlagStore>()
            ?? (scope.GetService<CharacterDbContext>() is { } db ? new EfTalentResetFlagStore(db) : null);
    }
}

/// <summary>EF Core <see cref="ITalentResetFlagStore"/> over the <c>character_at_login</c> rows of <see cref="CharacterRenameDataModule"/>.</summary>
public sealed class EfTalentResetFlagStore(CharacterDbContext db) : ITalentResetFlagStore
{
    private const uint Flag = CharacterAtLoginFlags.ResetTalents;

    public async Task<bool> FlagAsync(int characterId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            DbSet<CharacterAtLoginRow> rows = db.Set<CharacterAtLoginRow>();
            CharacterAtLoginRow? row = await rows.FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                rows.Add(new CharacterAtLoginRow { CharacterId = characterId, Flags = Flag });
            }
            else
            {
                row.Flags |= Flag;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<int> FlagAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<int> ids = await db.Characters.AsNoTracking().Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            DbSet<CharacterAtLoginRow> rows = db.Set<CharacterAtLoginRow>();
            Dictionary<int, CharacterAtLoginRow> existing = await rows.ToDictionaryAsync(r => r.CharacterId, cancellationToken).ConfigureAwait(false);
            int flagged = 0;
            foreach (int id in ids)
            {
                if (!existing.TryGetValue(id, out CharacterAtLoginRow? row))
                {
                    rows.Add(new CharacterAtLoginRow { CharacterId = id, Flags = Flag });
                    flagged++;
                }
                else if ((row.Flags & Flag) == 0)
                {
                    row.Flags |= Flag;
                    flagged++;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return flagged;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<bool> IsFlaggedAsync(int characterId, CancellationToken cancellationToken = default)
    {
        uint flags = await db.Set<CharacterAtLoginRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId).Select(r => r.Flags)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return (flags & Flag) != 0;
    }

    public async Task ClearAsync(int characterId, CancellationToken cancellationToken = default)
    {
        try
        {
            CharacterAtLoginRow? row = await db.Set<CharacterAtLoginRow>()
                .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (row is null || (row.Flags & Flag) == 0)
            {
                return;
            }

            row.Flags &= ~Flag;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
