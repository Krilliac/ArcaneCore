using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Rules;

/// <summary>
/// World schema 62 (lane P1 was assigned 64, but the update-file exporter refuses to write files while placeholders for the open 62-63 exist, so this branch uses the first free number): the shipped bot content (docs/superpowers/specs/2026-10-28-rule-engine-design.md, phase P1). No new table: the step
/// seeds <c>playerbot_goal</c> with the goal catalogue (the 18 goal kinds and the 16 ladder rungs) and the bot goal ladder as rules
/// (<c>rule</c> ids 10001-10016, one <c>bot.propose_goal</c> action each in <c>rule_action</c>), all from <see cref="PlayerbotLadderContent"/>.
/// A row is inserted only when its key is absent, so a server's edit of a shipped row (a weight, <c>enabled</c>) survives a later upgrade,
/// and the step is idempotent. The version is the next free world step (the integrator renumbers this one constant).
/// </summary>
public sealed class PlayerbotLadderDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 62;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        .. PlayerbotLadderContent.Goals.Select(g => (SchemaChange)new EnsureRowChange(
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
        .. PlayerbotLadderContent.Rules.Select(r => (SchemaChange)new EnsureRowChange(
            RuleDataModule.RuleTable, "id", r.Id, new Dictionary<string, object?>
            {
                ["host"] = r.Host,
                ["owner_kind"] = r.OwnerKind,
                ["owner_id"] = r.OwnerId,
                ["event_type"] = r.EventType,
                ["event_param1"] = r.EventParam1,
                ["event_param2"] = r.EventParam2,
                ["event_param3"] = r.EventParam3,
                ["event_param4"] = r.EventParam4,
                ["phase_mask"] = r.PhaseMask,
                ["filter_id"] = r.FilterId,
                ["chance_pct"] = r.ChancePct,
                ["condition_id"] = r.ConditionId,
                ["cooldown_min_ms"] = r.CooldownMinMs,
                ["cooldown_max_ms"] = r.CooldownMaxMs,
                ["priority"] = r.Priority,
                ["flags"] = r.Flags,
                ["enabled"] = (byte)(r.Enabled ? 1 : 0),
                ["comment"] = r.Comment,
            })),
        .. PlayerbotLadderContent.Actions.Select(a => (SchemaChange)new EnsureRowChange(
            RuleDataModule.ActionTable, "rule_id", a.OwnerId, new Dictionary<string, object?>
            {
                ["seq"] = a.Seq,
                ["action_type"] = a.ActionType,
                ["param1"] = a.Param1,
                ["param2"] = a.Param2,
                ["param3"] = a.Param3,
                ["param4"] = a.Param4,
                ["param5"] = a.Param5,
                ["param6"] = a.Param6,
                ["target_type"] = a.TargetType,
                ["target_param1"] = a.TargetParam1,
                ["target_param2"] = a.TargetParam2,
                ["target_param3"] = a.TargetParam3,
                ["delay_ms"] = a.DelayMs,
                ["flags"] = a.Flags,
            })),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // The tables belong to RuleDataModule and PlayerbotGoalDataModule; this step only seeds them.
        ArgumentNullException.ThrowIfNull(modelBuilder);
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

/// <summary>
/// The shipped bot content as EF rows, for <c>WorldDbInitializer</c>: each table is filled from <see cref="PlayerbotLadderContent"/> only
/// when it is empty (the Warden check precedent), so a database that did not get the schema step's rows (a baseline install, a wiped table)
/// still has the ladder, and a table a server edited is left alone. One source: <see cref="PlayerbotLadderContent"/>, which the schema step
/// and the brain's fallback use too.
/// </summary>
public static class PlayerbotLadderSeed
{
    public static IEnumerable<PlayerbotGoalRow> Goals() => PlayerbotLadderContent.Goals.Select(g => new PlayerbotGoalRow
    {
        GoalId = g.GoalId, Name = g.Name, BasePriority = g.BasePriority, Enabled = (byte)(g.Enabled ? 1 : 0), Driver = g.Driver,
        DriverParam1 = g.DriverParam1, DriverParam2 = g.DriverParam2, DriverParam3 = g.DriverParam3, DriverParam4 = g.DriverParam4, Comment = g.Comment,
    });

    public static IEnumerable<RuleRow> Rules() => PlayerbotLadderContent.Rules.Select(r => new RuleRow
    {
        Id = r.Id, Host = r.Host, OwnerKind = r.OwnerKind, OwnerId = r.OwnerId, EventType = r.EventType, EventParam1 = r.EventParam1,
        EventParam2 = r.EventParam2, EventParam3 = r.EventParam3, EventParam4 = r.EventParam4, PhaseMask = r.PhaseMask, FilterId = r.FilterId,
        ChancePct = r.ChancePct, ConditionId = r.ConditionId, CooldownMinMs = r.CooldownMinMs, CooldownMaxMs = r.CooldownMaxMs,
        Priority = r.Priority, Flags = r.Flags, Enabled = (byte)(r.Enabled ? 1 : 0), Comment = r.Comment,
    });

    public static IEnumerable<RuleActionRow> Actions() => PlayerbotLadderContent.Actions.Select(a => new RuleActionRow
    {
        RuleId = a.OwnerId, Seq = a.Seq, ActionType = a.ActionType, Param1 = a.Param1, Param2 = a.Param2, Param3 = a.Param3, Param4 = a.Param4,
        Param5 = a.Param5, Param6 = a.Param6, TargetType = a.TargetType, TargetParam1 = a.TargetParam1, TargetParam2 = a.TargetParam2,
        TargetParam3 = a.TargetParam3, DelayMs = a.DelayMs, Flags = a.Flags,
    });
}
