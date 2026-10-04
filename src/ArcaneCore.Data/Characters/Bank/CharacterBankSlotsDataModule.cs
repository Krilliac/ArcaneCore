using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Bank;

/// <summary>
/// Purchased bank bag slots live with the character, as in vmangos characters.bank_bag_slots
/// (sql/characters.sql:74, Player.cpp:14663,16403). The character row is removed by the core deletion transaction.
/// </summary>
public sealed class CharacterBankSlotsDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 21;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange("characters", "bank_bag_slots")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterRecord>().Property(c => c.BankBagSlotCount).HasColumnName("bank_bag_slots");
    }

    public void AddServices(IServiceCollection services)
    {
        // The existing character store saves this column with money in one character snapshot.
    }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        // The core deletion removes the characters row and its bank_bag_slots column in the same transaction.
        return Task.CompletedTask;
    }
}
