using ArcaneCore.Data.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Schema;

/// <summary>
/// A schema version reserved for another lane of a parallel wave, held by an empty step so a branch that owns a later
/// version can run on its own (<see cref="DataModules.Compose"/> refuses a gap). A placeholder yields to any real module
/// or inline step that claims the same version: Compose leaves it out, so merging the owning lane needs no edit here.
/// Delete a placeholder once its version is owned in the tree.
/// <para>
/// A database must never pass through a placeholder: the empty step would be recorded as applied and the owner's real
/// step skipped for good (the bootstrapper tracks one version number). Compose records the versions still held by
/// placeholders in <see cref="SchemaDefinition.ReservedGapVersions"/>, and the bootstrapper and the planner refuse to
/// create a database or upgrade one across such a version unless <see cref="ReservedSchemaGaps.AllowSwitch"/> is on.
/// Only the test projects turn it on (Directory.Build.props, through their runtimeconfig), so a server built from a
/// branch that still holds a placeholder fails closed at start instead of damaging the database.
/// </para>
/// </summary>
public interface IReservedSchemaGap : IDataModule
{
}

/// <summary>The fail-closed guard of <see cref="IReservedSchemaGap"/> placeholders.</summary>
public static class ReservedSchemaGaps
{
    /// <summary>
    /// The AppContext switch that lets this process create or upgrade a database through a placeholder version.
    /// Set by the test projects only (RuntimeHostConfigurationOption in Directory.Build.props); a server or tool never has it.
    /// </summary>
    public const string AllowSwitch = "ArcaneCore.Data.AllowReservedSchemaGaps";

    /// <summary>Whether <see cref="AllowSwitch"/> is on in this process (off unless a runtimeconfig or the host sets it).</summary>
    public static bool AllowedInThisProcess => AppContext.TryGetSwitch(AllowSwitch, out bool allowed) && allowed;

    /// <summary>
    /// The placeholder versions that bringing a database from <paramref name="fromVersion"/> to the current schema would
    /// record as applied: every one for a database without a version (fresh create, resumed create, version-1 adoption).
    /// </summary>
    public static IReadOnlyList<int> Pending(SchemaDefinition definition, int? fromVersion)
    {
        ArgumentNullException.ThrowIfNull(definition);
        int from = fromVersion ?? SchemaBootstrapper.CreatingVersion;
        return [.. definition.ReservedGapVersions.Where(v => v > from).Order()];
    }

    /// <summary>Why a create or upgrade through <paramref name="pending"/> placeholder versions is refused.</summary>
    public static string RefusalMessage(SchemaDefinition definition, int? fromVersion, IReadOnlyList<int> pending)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(pending);
        string have = fromVersion is { } v and > SchemaBootstrapper.CreatingVersion ? $"is at schema version {v}" : "holds no complete schema";
        return $"The {definition.Component} database {have}, and this build would bring it to version {definition.CurrentVersion} " +
               $"through reserved placeholder version(s) {string.Join(", ", pending)} (Schema/ReservedSchemaGaps.cs) whose real " +
               "changes are not in this build. Recording them as applied would skip the real changes for good, so no table was created and no version was recorded. " +
               "Run a build in which those versions are owned (merge the lanes that own them).";
    }
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
