using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Talents;

/// <summary>
/// The respec economy of one character (vmangos <c>characters.reset_talents_multiplier</c> and
/// <c>reset_talents_time</c>, Player.cpp:14901-14902 and :16452-16453). The learned talent spells themselves are
/// ordinary <c>character_spell</c> rows; the used and free points are derived, as in vmangos.
/// </summary>
public sealed class CharacterTalentRow
{
    public int CharacterId { get; set; }

    public uint ResetMultiplier { get; set; }

    /// <summary>Unix seconds of the last paid respec (0 = never).</summary>
    public long ResetTimeUnix { get; set; }
}

/// <summary>
/// A spell hidden by a talent removal (vmangos <c>character_spell.disabled</c> = 1, Player.cpp:3797-3885). The
/// spell stays out of the spellbook until its talent is relearned. Kept in its own table so the spellbook table
/// that other features edit is not touched.
/// </summary>
public sealed class CharacterSpellDisabledRow
{
    public int CharacterId { get; set; }

    public uint Spell { get; set; }
}

/// <summary>The stored respec state.</summary>
public sealed record CharacterTalentState(uint ResetMultiplier, long ResetTimeUnix);

/// <summary>Persistence of the talent state (characters database).</summary>
public interface ICharacterTalentStore
{
    /// <summary>The stored respec state, or null when the character never respecced.</summary>
    Task<CharacterTalentState?> GetAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the respec state.</summary>
    Task SaveAsync(int characterId, CharacterTalentState state, CancellationToken cancellationToken = default);

    /// <summary>The disabled spells of one character in ascending order.</summary>
    Task<IReadOnlyList<uint>> GetDisabledAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Record spells as disabled (already-disabled spells are ignored).</summary>
    Task AddDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default);

    /// <summary>Forget disabled spells (spells that are not disabled are ignored).</summary>
    Task RemoveDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default);

    /// <summary>Remove the rows of a character id that has no <c>characters</c> row (the queued removal after a deletion).</summary>
    Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The <c>character_talent</c> and <c>character_spell_disabled</c> tables. <see cref="Version"/> is the single place the
/// characters schema version is set; it is the next free characters version of this tree and the integrator renumbers it.
/// </summary>
public sealed class CharacterTalentDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 14;

    public const string TalentTable = "character_talent";

    public const string DisabledSpellTable = "character_spell_disabled";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(TalentTable), new CreateTableChange(DisabledSpellTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterTalentRow>(entity =>
        {
            entity.ToTable(TalentTable);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });

        modelBuilder.Entity<CharacterSpellDisabledRow>(entity =>
        {
            entity.ToTable(DisabledSpellTable);
            entity.HasKey(r => new { r.CharacterId, r.Spell });
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterTalentStore, EfCharacterTalentStore>();

    /// <summary>Both tables (vmangos Player::DeleteFromDB clears every per-character table in one transaction).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterTalentRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterSpellDisabledRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="ICharacterTalentStore"/>.</summary>
public sealed class EfCharacterTalentStore(CharacterDbContext db) : ICharacterTalentStore
{
    public async Task<CharacterTalentState?> GetAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterTalentRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .Select(r => new CharacterTalentState(r.ResetMultiplier, r.ResetTimeUnix))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task SaveAsync(int characterId, CharacterTalentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        int updated = await db.Set<CharacterTalentRow>().Where(r => r.CharacterId == characterId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.ResetMultiplier, state.ResetMultiplier).SetProperty(r => r.ResetTimeUnix, state.ResetTimeUnix),
                cancellationToken).ConfigureAwait(false);
        if (updated > 0)
        {
            return;
        }

        try
        {
            db.Set<CharacterTalentRow>().Add(new CharacterTalentRow
            {
                CharacterId = characterId,
                ResetMultiplier = state.ResetMultiplier,
                ResetTimeUnix = state.ResetTimeUnix,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<uint>> GetDisabledAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterSpellDisabledRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Spell)
            .Select(r => r.Spell)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task AddDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (spells.Count == 0)
        {
            return;
        }

        // A List, not an array: with C# 14 an array's Contains binds to the span overload, which EF cannot translate.
        List<uint> wanted = [.. spells.Distinct()];
        List<uint> known = await db.Set<CharacterSpellDisabledRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId && wanted.Contains(r.Spell))
            .Select(r => r.Spell)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (uint spell in wanted.Except(known))
            {
                db.Set<CharacterSpellDisabledRow>().Add(new CharacterSpellDisabledRow { CharacterId = characterId, Spell = spell });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task RemoveDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (spells.Count == 0)
        {
            return;
        }

        List<uint> wanted = [.. spells.Distinct()];
        await db.Set<CharacterSpellDisabledRow>()
            .Where(r => r.CharacterId == characterId && wanted.Contains(r.Spell))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Conditional in each statement, so a late or retried removal never wipes a character recreated with the same id
    /// (docs/integration/character-delete.md).
    /// </summary>
    public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await db.Set<CharacterTalentRow>()
            .Where(r => r.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterSpellDisabledRow>()
            .Where(r => r.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
