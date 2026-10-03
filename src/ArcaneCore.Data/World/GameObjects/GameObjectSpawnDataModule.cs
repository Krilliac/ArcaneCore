using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.GameObjects;

/// <summary>
/// The game object spawn data step of the world schema (<see cref="IDataModule"/>): the respawn delay upper bound
/// (<c>spawntimesecsmax</c>, vmangos <c>GameObjectData::spawntimesecsmax</c>) and <c>spawn_flags</c> that retail uses
/// to roll the respawn delay (D:\refs\vmangos\src\game\Objects\GameObject.cpp:698-711, GameObjectDefines.h:814-832).
/// Both columns are additive on <c>gameobject_spawn</c>: the max is nullable (rows from before the step keep the fixed
/// minimum delay) and the flags default to 0.
/// <para>
/// <b>World version 15</b>: the next free number after the integrated tip (world 14). It is named once here; tests read
/// <see cref="Version"/> or <c>WorldDbContext.Schema.CurrentVersion</c>, never a literal, and the integration lead renumbers
/// in merge order (docs/integration/seams.md).
/// </para>
/// </summary>
public sealed class GameObjectSpawnDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 15;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("gameobject_spawn", nameof(GameObjectSpawnRow.SpawnTimeMaxSeconds)),
        new AddColumnChange("gameobject_spawn", nameof(GameObjectSpawnRow.SpawnFlags)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The columns are properties of GameObjectSpawnRow, configured by GameObjectLootDataModule; nothing more to map.
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
