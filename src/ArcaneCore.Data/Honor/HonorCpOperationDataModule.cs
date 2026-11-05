using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Honor;

/// <summary>
/// Characters schema 76: <c>character_honor_cp.operation_id</c> (nullable, unique), the write identity <c>HonorWriteQueue</c> stamps on every
/// contribution row so that a retried batch whose first commit succeeded appends nothing twice (<see cref="EfHonorStore.AppendCpAsync"/>).
/// The column and its unique index are declared by the model of <see cref="CharacterHonorDataModule"/>; this step adds them to databases created
/// before it. Existing rows keep a null id, which the unique index allows any number of times.
/// </summary>
public sealed class HonorCpOperationDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 76;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new AddColumnChange(CharacterHonorDataModule.CpTable, "operation_id"), new EnsureIndexesChange(CharacterHonorDataModule.CpTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The row and column mapping live with the table's owner, CharacterHonorDataModule.
    }

    public void AddServices(IServiceCollection services)
    {
        // EfHonorStore (registered by CharacterHonorDataModule) reads and writes the column.
    }

    /// <summary>Nothing to remove: this step only adds a column to a table whose owner deletes the character's rows.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
