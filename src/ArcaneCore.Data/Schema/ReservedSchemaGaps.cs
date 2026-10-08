using ArcaneCore.Data.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Schema;

/// <summary>
/// A schema version reserved for another lane of a parallel wave, held by an empty step so a branch that owns a later
/// version can run on its own (<see cref="DataModules.Compose"/> refuses a gap). A placeholder yields to any real module
/// or inline step that claims the same version: Compose leaves it out, so merging the owning lane needs no edit here.
/// Delete a placeholder once its version is owned in the tree. Never ship a database upgraded through a placeholder:
/// the empty step would be recorded as applied and the real one skipped.
/// </summary>
public interface IReservedSchemaGap : IDataModule
{
}

/// <summary>Shared body of the placeholders: no changes, no model, no services.</summary>
public abstract class ReservedSchemaGap : IReservedSchemaGap
{
    public abstract DatabaseComponent Component { get; }

    public abstract int SchemaVersion { get; }

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

/// <summary>Characters schema 35, reserved for another wave-2 lane (ops-social owns 37 and 38).</summary>
public sealed class ReservedCharactersSchema35 : ReservedSchemaGap, ICharacterDataCleanup
{
    /// <summary>A placeholder has no tables: nothing to delete.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;

    public override DatabaseComponent Component => DatabaseComponent.Characters;

    public override int SchemaVersion => 35;
}

/// <summary>Characters schema 36, reserved for another wave-2 lane (ops-social owns 37 and 38).</summary>
public sealed class ReservedCharactersSchema36 : ReservedSchemaGap, ICharacterDataCleanup
{
    /// <summary>A placeholder has no tables: nothing to delete.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;

    public override DatabaseComponent Component => DatabaseComponent.Characters;

    public override int SchemaVersion => 36;
}
