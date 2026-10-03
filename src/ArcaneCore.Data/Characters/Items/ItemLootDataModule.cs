using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Items;

/// <summary>
/// The generated loot of container items (lockboxes, clams): <c>item_loot_state</c> and <c>item_loot</c>, two new tables (no ALTER of
/// <c>item_instance</c>), written in the same SaveChanges as the inventory snapshot (<see cref="ItemPersistence.StageReplaceAsync"/>), so an item and
/// its loot never disagree after a crash. <see cref="Version"/> is the next free characters version at this lane's base (the wave-3 integrated base ends at 18); the
/// integrator renumbers this one constant.
/// </summary>
public sealed class ItemLootDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The characters schema version of this step.</summary>
    public const int Version = 19;

    public static readonly IReadOnlyList<string> Tables = ["item_loot_state", "item_loot"];

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [.. Tables.Select(t => new CreateTableChange(t))];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ItemLootStateRow>(ItemLootStateRow.Configure);
        modelBuilder.Entity<ItemLootRow>(ItemLootRow.Configure);
    }

    public void AddServices(IServiceCollection services)
    {
    }

    /// <summary>The loot of every item the character owns (vmangos DeleteFromDB deletes <c>item_loot</c> with the items).</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        List<uint> guids = await db.Set<ItemInstanceRow>().AsNoTracking().Where(r => r.OwnerGuid == characterId).Select(r => r.Guid)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (guids.Count == 0)
        {
            return;
        }

        db.RemoveRange(await db.Set<ItemLootStateRow>().Where(r => guids.Contains(r.ItemGuid)).ToListAsync(cancellationToken).ConfigureAwait(false));
        db.RemoveRange(await db.Set<ItemLootRow>().Where(r => guids.Contains(r.ItemGuid)).ToListAsync(cancellationToken).ConfigureAwait(false));
    }
}