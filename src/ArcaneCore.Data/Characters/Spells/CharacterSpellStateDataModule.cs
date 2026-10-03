using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Spells;

/// <summary>
/// One running cooldown of a character (vmangos <c>character_spell_cooldown</c>: guid, spell,
/// item, time; re-implemented with an explicit kind so category cooldowns persist too). The end is
/// an absolute wall clock in Unix milliseconds: cooldowns keep running while offline.
/// </summary>
public sealed class CharacterSpellCooldownRow
{
    public int CharacterId { get; set; }

    /// <summary>0 = spell cooldown, 1 = Spell.dbc category cooldown.</summary>
    public byte Kind { get; set; }

    /// <summary>Spell id (kind 0) or category id (kind 1).</summary>
    public uint Id { get; set; }

    public long EndsAtUnixMs { get; set; }
}

/// <summary>
/// One saved aura of a character (vmangos / cmangos-classic <c>character_aura</c>: caster_guid,
/// spell, stackcount, remaincharges, basepoints0-2, periodictime0-2, maxduration, remaintime,
/// effIndexMask). <see cref="Seq"/> keeps the save order. The caster GUID is provenance only
/// (docs/integration/spells-persistence.md).
/// </summary>
public sealed class CharacterAuraRow
{
    public int CharacterId { get; set; }

    public int Seq { get; set; }

    public uint Spell { get; set; }

    public ulong CasterGuid { get; set; }

    public byte CasterLevel { get; set; }

    public byte StackCount { get; set; }

    public int Charges { get; set; }

    /// <summary>-1 when permanent.</summary>
    public int MaxDurationMs { get; set; }

    /// <summary>-1 when permanent.</summary>
    public int RemainingMs { get; set; }

    public byte EffectMask { get; set; }

    public int Amount0 { get; set; }

    public int Amount1 { get; set; }

    public int Amount2 { get; set; }

    public int PeriodicTimer0 { get; set; }

    public int PeriodicTimer1 { get; set; }

    public int PeriodicTimer2 { get; set; }

    public long SavedAtUnixMs { get; set; }
}

/// <summary>The persisted cooldowns and auras of one character.</summary>
public sealed record CharacterSpellState(IReadOnlyList<CharacterSpellCooldownRow> Cooldowns, IReadOnlyList<CharacterAuraRow> Auras);

/// <summary>Persistence of cooldowns and auras across logout (characters database).</summary>
public interface ICharacterSpellStateStore
{
    /// <summary>The saved state of one character (empty lists when nothing is saved).</summary>
    Task<CharacterSpellState> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Replace every saved cooldown and aura of one character, atomically. Row character ids are overwritten with <paramref name="characterId"/>.</summary>
    Task SaveAsync(int characterId, CharacterSpellState state, CancellationToken cancellationToken = default);

    /// <summary>Forget the saved state of a deleted character.</summary>
    Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The <c>character_spell_cooldown</c> and <c>character_aura</c> tables. Reserved as characters
/// schema version 8 (after reputation's v7); this branch uses 7 until v7 lands because versions
/// must be contiguous — the lead renumbers <see cref="Version"/> at merge (docs/integration/spells-persistence.md).
/// </summary>
public sealed class CharacterSpellStateDataModule : IDataModule
{
    /// <summary>The single place the schema version is set.</summary>
    public const int Version = 7;

    public const string CooldownTable = "character_spell_cooldown";

    public const string AuraTable = "character_aura";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(CooldownTable), new CreateTableChange(AuraTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CharacterSpellCooldownRow>(entity =>
        {
            entity.ToTable(CooldownTable);
            entity.HasKey(r => new { r.CharacterId, r.Kind, r.Id });
        });

        modelBuilder.Entity<CharacterAuraRow>(entity =>
        {
            entity.ToTable(AuraTable);
            entity.HasKey(r => new { r.CharacterId, r.Seq });
            entity.Property(r => r.Seq).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterSpellStateStore, EfCharacterSpellStateStore>();
}

/// <summary>EF Core implementation of <see cref="ICharacterSpellStateStore"/>.</summary>
public sealed class EfCharacterSpellStateStore(CharacterDbContext db) : ICharacterSpellStateStore
{
    public async Task<CharacterSpellState> LoadAsync(int characterId, CancellationToken cancellationToken = default) => new(
        await db.Set<CharacterSpellCooldownRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Kind).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false),
        await db.Set<CharacterAuraRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Seq)
            .ToListAsync(cancellationToken).ConfigureAwait(false));

    public async Task SaveAsync(int characterId, CharacterSpellState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await DeleteRowsAsync(characterId, cancellationToken).ConfigureAwait(false);
            var seen = new HashSet<(byte, uint)>();
            foreach (CharacterSpellCooldownRow row in state.Cooldowns)
            {
                if (seen.Add((row.Kind, row.Id)))
                {
                    db.Set<CharacterSpellCooldownRow>().Add(new CharacterSpellCooldownRow
                    {
                        CharacterId = characterId,
                        Kind = row.Kind,
                        Id = row.Id,
                        EndsAtUnixMs = row.EndsAtUnixMs,
                    });
                }
            }

            int seq = 0;
            foreach (CharacterAuraRow row in state.Auras)
            {
                CharacterAuraRow copy = Copy(row);
                copy.CharacterId = characterId;
                copy.Seq = seq++;
                db.Set<CharacterAuraRow>().Add(copy);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        => DeleteRowsAsync(characterId, cancellationToken);

    private async Task DeleteRowsAsync(int characterId, CancellationToken cancellationToken)
    {
        await db.Set<CharacterSpellCooldownRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterAuraRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CharacterAuraRow Copy(CharacterAuraRow row) => new()
    {
        Spell = row.Spell,
        CasterGuid = row.CasterGuid,
        CasterLevel = row.CasterLevel,
        StackCount = row.StackCount,
        Charges = row.Charges,
        MaxDurationMs = row.MaxDurationMs,
        RemainingMs = row.RemainingMs,
        EffectMask = row.EffectMask,
        Amount0 = row.Amount0,
        Amount1 = row.Amount1,
        Amount2 = row.Amount2,
        PeriodicTimer0 = row.PeriodicTimer0,
        PeriodicTimer1 = row.PeriodicTimer1,
        PeriodicTimer2 = row.PeriodicTimer2,
        SavedAtUnixMs = row.SavedAtUnixMs,
    };
}
