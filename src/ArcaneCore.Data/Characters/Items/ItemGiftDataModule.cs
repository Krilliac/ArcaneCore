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
/// <b>Characters version 39</b>: the number the wave-2 plan reserves for the economy-items lane. Versions must be contiguous, so this branch
/// holds 35 to 38 open with empty steps (<see cref="EconomyItemsLaneSchemaGap"/>); tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class ItemGiftDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The characters schema version of this step.</summary>
    public const int Version = 39;

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

/// <summary>
/// Empty characters schema steps 35 to 38: the wave-2 plan reserves them for other lanes and gives the economy-items lane 39, but
/// <see cref="DataModules.Compose"/> requires contiguous versions, so this branch holds the gap open with steps that change nothing.
/// INTEGRATOR: delete each placeholder whose number a merged lane really uses (Compose reports "claimed twice" until you do); keep the
/// ones nobody claimed. An empty step only advances the version table; never ship a build with these placeholders to a live realm.
/// </summary>
public abstract class EconomyItemsLaneSchemaGap(int version) : IDataModule, ICharacterDataCleanup
{
    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion { get; } = version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }

    /// <summary>An empty step owns no rows.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Characters step 35 held open for the lane that owns it (see <see cref="EconomyItemsLaneSchemaGap"/>).</summary>
public sealed class EconomyItemsLaneSchemaGap35() : EconomyItemsLaneSchemaGap(Version)
{
    public const int Version = 35;
}

/// <summary>Characters step 36 held open for the lane that owns it (see <see cref="EconomyItemsLaneSchemaGap"/>).</summary>
public sealed class EconomyItemsLaneSchemaGap36() : EconomyItemsLaneSchemaGap(Version)
{
    public const int Version = 36;
}

/// <summary>Characters step 37 held open for the lane that owns it (see <see cref="EconomyItemsLaneSchemaGap"/>).</summary>
public sealed class EconomyItemsLaneSchemaGap37() : EconomyItemsLaneSchemaGap(Version)
{
    public const int Version = 37;
}

/// <summary>Characters step 38 held open for the lane that owns it (see <see cref="EconomyItemsLaneSchemaGap"/>).</summary>
public sealed class EconomyItemsLaneSchemaGap38() : EconomyItemsLaneSchemaGap(Version)
{
    public const int Version = 38;
}
