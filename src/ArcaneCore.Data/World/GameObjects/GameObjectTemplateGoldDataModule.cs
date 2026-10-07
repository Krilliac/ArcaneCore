using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.GameObjects;

/// <summary>
/// The chest money step of the world schema (<see cref="IDataModule"/>): <c>gameobject_template.mingold</c> and <c>maxgold</c>,
/// the two columns the reference core reads after <c>data23</c> into <c>GameObjectInfo::MinMoneyLoot</c>/<c>MaxMoneyLoot</c>
/// (mangos Object/GameObject.h:415-416) and rolls with <c>Loot::generateMoneyLoot</c> when a game object's loot is generated
/// (Object/PlayerLoot.cpp:229). Both columns are additive on <c>gameobject_template</c> and default to 0 (no money), so rows
/// imported before the step keep paying nothing.
/// <para>
/// <b>World version 31</b>: the next free number after the wave-4 integrated tip (world 30, the tavern area triggers). It is named
/// once here; tests read <see cref="Version"/> or <c>WorldDbContext.Schema.CurrentVersion</c>, never a literal, and the integration
/// lead renumbers in merge order (docs/integration/seams.md).
/// </para>
/// </summary>
public sealed class GameObjectTemplateGoldDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 31;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("gameobject_template", nameof(GameObjectTemplateRow.MinGold)),
        new AddColumnChange("gameobject_template", nameof(GameObjectTemplateRow.MaxGold)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The columns are properties of GameObjectTemplateRow, configured by GameObjectLootDataModule; nothing more to map.
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
