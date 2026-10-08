using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Items;

/// <summary>
/// Gift wrapping (vmangos ItemHandler.cpp HandleWrapItemOpcode, SpellHandler.cpp HandleOpenItemOpcode): two columns on <c>item_instance</c>,
/// <c>gift_entry</c> and <c>gift_flags</c>, holding what vmangos keeps in <c>character_gifts</c> (the wrapped item's own entry and flags).
/// A deliberate difference: on the item row, so the wrapped state travels with the item through mail, auction and trade escrow and is
/// deleted with it, where vmangos has to move or delete a <c>character_gifts</c> row on every one of those paths.
/// <para>
/// <b>Characters version 38</b>: reserved as 39 in the wave-2 plan and renumbered down at the 2026-10-07 integration, which
/// closed the unclaimed numbers (docs/integration/wave2-20261007.md). Tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class ItemGiftDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The characters schema version of this step.</summary>
    public const int Version = 38; // reserved as 39 in the wave-2 plan; renumbered down at the 2026-10-07 integration (no gaps)

    public const string GiftEntryColumn = "gift_entry";

    public const string GiftFlagsColumn = "gift_flags";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange("item_instance", GiftEntryColumn), new AddColumnChange("item_instance", GiftFlagsColumn)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ItemInstanceRow>().Property(r => r.GiftEntry).HasColumnName(GiftEntryColumn);
        modelBuilder.Entity<ItemInstanceRow>().Property(r => r.GiftFlags).HasColumnName(GiftFlagsColumn);
    }

    public void AddServices(IServiceCollection services)
    {
        // The item stores read and write the columns with the rest of item_instance.
    }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        // The columns go with the item rows, which ItemCharacterDataModule deletes.
        return Task.CompletedTask;
    }
}
