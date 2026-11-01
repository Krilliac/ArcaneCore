using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Rules;

/// <summary>
/// World schema 67: seeds the <c>playerbot_goal</c> row of the WorldPvp goal kind (goal id <see cref="WorldPvpGoalId"/>), which
/// <see cref="PlayerbotLadderContent.Goals"/> gained after the bot content step (<see cref="PlayerbotLadderDataModule"/>, world 64) was
/// released. A released step and its seeds are frozen, so the new row is seeded here and <see cref="PlayerbotLadderDataModule"/> leaves
/// it out. A row is inserted only when its key is absent, so a server's edit of the shipped row survives a later upgrade, and the step is
/// idempotent. No new table.
/// </summary>
public sealed class PlayerbotWorldPvpGoalDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 67;

    /// <summary>The <c>playerbot_goal.goal_id</c> of the WorldPvp goal kind.</summary>
    public const short WorldPvpGoalId = 18;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        .. PlayerbotLadderContent.Goals.Where(g => g.GoalId == WorldPvpGoalId).Select(g => (SchemaChange)new EnsureRowChange(
            PlayerbotGoalDataModule.Table, "goal_id", g.GoalId, new Dictionary<string, object?>
            {
                ["name"] = g.Name,
                ["base_priority"] = g.BasePriority,
                ["enabled"] = (byte)(g.Enabled ? 1 : 0),
                ["driver"] = g.Driver,
                ["driver_param1"] = g.DriverParam1,
                ["driver_param2"] = g.DriverParam2,
                ["driver_param3"] = g.DriverParam3,
                ["driver_param4"] = g.DriverParam4,
                ["comment"] = g.Comment,
            })),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The table belongs to PlayerbotGoalDataModule; this step only seeds it.
        ArgumentNullException.ThrowIfNull(modelBuilder);
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
