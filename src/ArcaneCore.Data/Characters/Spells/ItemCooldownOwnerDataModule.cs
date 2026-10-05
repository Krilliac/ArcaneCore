using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Spells;

/// <summary>Owner metadata for item-caused cooldowns; migration slot 22 is reconciled centrally.</summary>
public sealed class ItemCooldownOwnerDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 22;
    public const string Table = "character_item_cooldown_owner";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterSpellCooldownOwnerRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.CharacterId, r.SpellId, r.ItemId });
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services) { }

    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => await db.Set<CharacterSpellCooldownOwnerRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}
