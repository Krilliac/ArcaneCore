using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Spells;

/// <summary>
/// One known spell of a character (cmangos-classic / vmangos <c>character_spell</c>: guid, spell;
/// the <c>active</c>/<c>disabled</c> columns there serve rank replacement and talents, which are
/// not implemented). Keyed by (character, spell); column names follow this repository's
/// characters tables (<c>CharacterId</c>, as in <c>character_action</c>).
/// </summary>
public sealed class CharacterSpellRow
{
    public int CharacterId { get; set; }

    public uint Spell { get; set; }
}

/// <summary>Persistence of the spellbook (characters database).</summary>
public interface ICharacterSpellStore
{
    /// <summary>Every known spell of every character (startup cache; see docs/integration/spells.md).</summary>
    Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The spells one character knows.</summary>
    Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Record spells as known (already-known spells are ignored).</summary>
    Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default);

    /// <summary>Forget one spell (no-op when it is not known).</summary>
    Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default);

    /// <summary>Forget every spell of a deleted character.</summary>
    Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>The <c>character_spell</c> table (characters schema version 3; see docs/integration/spells.md).</summary>
public sealed class CharacterSpellDataModule : IDataModule
{
    public const string Table = "character_spell";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => 3;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterSpellRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.CharacterId, r.Spell });
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterSpellStore, EfCharacterSpellStore>();
}

/// <summary>EF Core implementation of <see cref="ICharacterSpellStore"/>.</summary>
public sealed class EfCharacterSpellStore(CharacterDbContext db) : ICharacterSpellStore
{
    public async Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
        => await db.Set<CharacterSpellRow>().AsNoTracking()
            .OrderBy(r => r.CharacterId).ThenBy(r => r.Spell)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterSpellRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Spell)
            .Select(r => r.Spell)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (spells.Count == 0)
        {
            return;
        }

        uint[] wanted = [.. spells.Distinct()];
        List<uint> known = await db.Set<CharacterSpellRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId && wanted.Contains(r.Spell))
            .Select(r => r.Spell)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (uint spell in wanted.Except(known))
        {
            db.Set<CharacterSpellRow>().Add(new CharacterSpellRow { CharacterId = characterId, Spell = spell });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default)
        => await db.Set<CharacterSpellRow>()
            .Where(r => r.CharacterId == characterId && r.Spell == spell)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

    public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<CharacterSpellRow>()
            .Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}
