using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary><c>creature_spell_list_entry</c>: a spell list's name and the chances that gate its support and ranged entries.</summary>
public sealed class CreatureSpellListHeaderRow
{
    public uint Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public uint ChanceSupportAction { get; set; }

    public uint ChanceRangedAttack { get; set; }
}

/// <summary><c>creature_spell_list</c>: one spell of a list (cmangos Entities/CreatureSpellList.h; times in milliseconds).</summary>
public sealed class CreatureSpellListRow
{
    public uint Id { get; set; }

    public uint Position { get; set; }

    public uint SpellId { get; set; }

    public uint Flags { get; set; }

    public int CombatCondition { get; set; } = -1;

    public uint TargetId { get; set; }

    public uint ScriptId { get; set; }

    public uint Availability { get; set; } = 100;

    public uint Probability { get; set; }

    public uint InitialMin { get; set; }

    public uint InitialMax { get; set; }

    public uint RepeatMin { get; set; }

    public uint RepeatMax { get; set; }
}

/// <summary><c>creature_spell_targeting</c>: a target rule entries name by id.</summary>
public sealed class CreatureSpellTargetingRow
{
    public uint Id { get; set; }

    public uint Type { get; set; }

    public uint Param1 { get; set; }

    public uint Param2 { get; set; }

    public uint Param3 { get; set; }

    public int UnitCondition { get; set; } = -1;
}

/// <summary><c>combat_condition</c>: a gate on a spell-list entry (cmangos Globals/CombatCondition.h).</summary>
public sealed class CombatConditionRow
{
    public int Id { get; set; }

    public uint WorldStateExpressionId { get; set; }

    public int SelfConditionId { get; set; }

    public int TargetConditionId { get; set; }

    public uint FriendConditionLogic { get; set; }

    public uint EnemyConditionLogic { get; set; }

    public int FriendConditionId0 { get; set; }

    public int FriendConditionId1 { get; set; }

    public uint FriendConditionOp0 { get; set; }

    public uint FriendConditionOp1 { get; set; }

    public uint FriendConditionCount0 { get; set; }

    public uint FriendConditionCount1 { get; set; }

    public int EnemyConditionId0 { get; set; }

    public int EnemyConditionId1 { get; set; }

    public uint EnemyConditionOp0 { get; set; }

    public uint EnemyConditionOp1 { get; set; }

    public uint EnemyConditionCount0 { get; set; }

    public uint EnemyConditionCount1 { get; set; }
}

/// <summary><c>unit_condition</c>: up to eight variable, comparison and value terms on one unit (cmangos Globals/UnitCondition.h).</summary>
public sealed class UnitConditionRow
{
    public int Id { get; set; }

    public uint Flags { get; set; }

    public uint Variable0 { get; set; }

    public uint Variable1 { get; set; }

    public uint Variable2 { get; set; }

    public uint Variable3 { get; set; }

    public uint Variable4 { get; set; }

    public uint Variable5 { get; set; }

    public uint Variable6 { get; set; }

    public uint Variable7 { get; set; }

    public uint Op0 { get; set; }

    public uint Op1 { get; set; }

    public uint Op2 { get; set; }

    public uint Op3 { get; set; }

    public uint Op4 { get; set; }

    public uint Op5 { get; set; }

    public uint Op6 { get; set; }

    public uint Op7 { get; set; }

    public int Value0 { get; set; }

    public int Value1 { get; set; }

    public int Value2 { get; set; }

    public int Value3 { get; set; }

    public int Value4 { get; set; }

    public int Value5 { get; set; }

    public int Value6 { get; set; }

    public int Value7 { get; set; }
}

/// <summary>
/// World schema 68: the creature spell lists. Five cmangos tables (<c>creature_spell_list_entry</c>, <c>creature_spell_list</c>,
/// <c>creature_spell_targeting</c>, <c>combat_condition</c>, <c>unit_condition</c>) and the <c>creature_template.SpellList</c> column a spawn takes
/// its list from. Filled by the content importer from a ClassicDB / cmangos dump; a database upgraded to 68 has no lists until
/// <c>arcane-db refresh</c> backfills them (<see cref="CreatureDumpImporter.RefreshSpellListsAsync"/>).
/// </summary>
public sealed class CreatureSpellListDataModule : IDataModule
{
    /// <summary>The world schema version this module arrives in.</summary>
    public const int Version = 68;

    public const string HeaderTable = "creature_spell_list_entry";
    public const string EntryTable = "creature_spell_list";
    public const string TargetingTable = "creature_spell_targeting";
    public const string CombatConditionTable = "combat_condition";
    public const string UnitConditionTable = "unit_condition";

    /// <summary>UNIT_CONDITION_FLAG_OR (cmangos Globals/UnitCondition.h): any term of the row is enough.</summary>
    private const uint UnitConditionFlagOr = 1;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(HeaderTable),
        new CreateTableChange(EntryTable),
        new CreateTableChange(TargetingTable),
        new CreateTableChange(CombatConditionTable),
        new CreateTableChange(UnitConditionTable),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.SpellList)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureSpellListHeaderRow>(entity =>
        {
            entity.ToTable(HeaderTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Name).HasMaxLength(200).IsRequired();
        });
        modelBuilder.Entity<CreatureSpellListRow>(entity =>
        {
            entity.ToTable(EntryTable);
            entity.HasKey(r => new { r.Id, r.Position });
        });
        modelBuilder.Entity<CreatureSpellTargetingRow>(entity =>
        {
            entity.ToTable(TargetingTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });
        modelBuilder.Entity<CombatConditionRow>(entity =>
        {
            entity.ToTable(CombatConditionTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });
        modelBuilder.Entity<UnitConditionRow>(entity =>
        {
            entity.ToTable(UnitConditionTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });

        // The column is added to a populated table, so it needs a default; a fresh database gets the same default the step file gives.
        modelBuilder.Entity<CreatureTemplateRow>().Property(r => r.SpellList).HasDefaultValue(0u);
    }

    public void AddServices(IServiceCollection services)
    {
    }

    /// <summary>Build the catalog from table rows. A row the catalog refuses is left out and goes to the anomaly ledger, as the reference logs and skips it.</summary>
    internal static CreatureSpellListCatalog ToCatalog(
        IEnumerable<CreatureSpellListHeaderRow> headers,
        IEnumerable<CreatureSpellListRow> entries,
        IEnumerable<CreatureSpellTargetingRow> targeting,
        IEnumerable<CombatConditionRow> combatConditions,
        IEnumerable<UnitConditionRow> unitConditions)
        => CreatureSpellListCatalog.FromRows(
            headers.Select(r => new CreatureSpellListHeader(r.Id, r.Name, r.ChanceSupportAction, r.ChanceRangedAttack)),
            entries.Select(r => (r.Id, ToEntry(r))),
            targeting.Select(r => new CreatureSpellListTargeting(r.Id, (CreatureSpellListTargetingType)r.Type, r.Param1, r.Param2, r.Param3, r.UnitCondition)),
            new CreatureConditionCatalog(combatConditions.Select(ToCombatCondition), unitConditions.Select(ToUnitCondition)),
            reason => AnomalyLedger.Shared.Report("creature-spell-lists", "row-skipped", EntryTable, reason));

    private static CreatureSpellListEntry ToEntry(CreatureSpellListRow r)
        => new(r.Position, r.SpellId, (CreatureSpellListFlags)r.Flags, r.TargetId, r.ScriptId, r.Availability, r.Probability, r.InitialMin, r.InitialMax, r.RepeatMin, r.RepeatMax)
        {
            CombatCondition = r.CombatCondition,
        };

    private static CombatConditionDefinition ToCombatCondition(CombatConditionRow r) => new(
        r.Id,
        r.WorldStateExpressionId,
        r.SelfConditionId,
        r.TargetConditionId,
        (CreatureConditionLogic)r.FriendConditionLogic,
        (CreatureConditionLogic)r.EnemyConditionLogic,
        new CombatConditionClause(r.FriendConditionId0, (CreatureConditionOperation)r.FriendConditionOp0, r.FriendConditionCount0),
        new CombatConditionClause(r.FriendConditionId1, (CreatureConditionOperation)r.FriendConditionOp1, r.FriendConditionCount1),
        new CombatConditionClause(r.EnemyConditionId0, (CreatureConditionOperation)r.EnemyConditionOp0, r.EnemyConditionCount0),
        new CombatConditionClause(r.EnemyConditionId1, (CreatureConditionOperation)r.EnemyConditionOp1, r.EnemyConditionCount1));

    /// <summary>
    /// Unused slots are dropped. The cmangos loop reads them too, as a term that holds, so its OR rows hold for everyone once they reach the first unused slot
    /// (row 855, "enemy is casting or channeling", would be true of any enemy); the rows say what they mean, and retail wins over that loop.
    /// </summary>
    private static UnitConditionDefinition ToUnitCondition(UnitConditionRow r)
    {
        (uint Variable, uint Op, int Value)[] slots =
        [
            (r.Variable0, r.Op0, r.Value0), (r.Variable1, r.Op1, r.Value1), (r.Variable2, r.Op2, r.Value2), (r.Variable3, r.Op3, r.Value3),
            (r.Variable4, r.Op4, r.Value4), (r.Variable5, r.Op5, r.Value5), (r.Variable6, r.Op6, r.Value6), (r.Variable7, r.Op7, r.Value7),
        ];
        UnitConditionTerm[] terms =
        [
            .. slots.Where(s => s.Variable != 0).Select(s => new UnitConditionTerm(s.Variable, (CreatureConditionOperation)s.Op, s.Value)),
        ];
        return new UnitConditionDefinition(r.Id, (r.Flags & UnitConditionFlagOr) != 0, terms);
    }
}
