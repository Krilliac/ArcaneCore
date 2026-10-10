using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// <c>smart_scripts</c>: one AzerothCore SmartAI row (data/sql/base/db_world/smart_scripts.sql column set; SmartScriptMgr.cpp
/// LoadSmartAIFromDB reads the same fields). Key (entryorguid, source_type, id, link) as in AzerothCore.
/// </summary>
public sealed class SmartScriptDbRow
{
    public int EntryOrGuid { get; set; }
    public byte SourceType { get; set; }
    public ushort Id { get; set; }
    public ushort Link { get; set; }
    public byte EventType { get; set; }
    public uint EventPhaseMask { get; set; }
    public byte EventChance { get; set; } = 100;
    public uint EventFlags { get; set; }
    public uint EventParam1 { get; set; }
    public uint EventParam2 { get; set; }
    public uint EventParam3 { get; set; }
    public uint EventParam4 { get; set; }
    public uint EventParam5 { get; set; }
    public uint EventParam6 { get; set; }
    public byte ActionType { get; set; }
    public uint ActionParam1 { get; set; }
    public uint ActionParam2 { get; set; }
    public uint ActionParam3 { get; set; }
    public uint ActionParam4 { get; set; }
    public uint ActionParam5 { get; set; }
    public uint ActionParam6 { get; set; }
    public byte TargetType { get; set; }
    public uint TargetParam1 { get; set; }
    public uint TargetParam2 { get; set; }
    public uint TargetParam3 { get; set; }
    public uint TargetParam4 { get; set; }
    public float TargetX { get; set; }
    public float TargetY { get; set; }
    public float TargetZ { get; set; }
    public float TargetO { get; set; }
    public string Comment { get; set; } = string.Empty;
}

/// <summary>
/// World 48 (w18 smartai lane; 47 is chat_word_filter): the <c>smart_scripts</c> table that creatures with AIName 'SmartAI' run. ClassicDB z2815 has no such table,
/// so it is empty after an import; rows are hand-authored (docs/integration/smartai-20261010.md).
/// </summary>
public sealed class SmartScriptDataModule : IDataModule
{
    public const int Version = 48;
    public const string Table = "smart_scripts";

    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SmartScriptDbRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.EntryOrGuid, r.SourceType, r.Id, r.Link });
            entity.Property(r => r.EntryOrGuid).ValueGeneratedNever();
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Comment).HasMaxLength(255).IsRequired();
        });
    }

    public void AddServices(IServiceCollection services) { }

    internal static SmartScriptRow ToRow(SmartScriptDbRow r) => new()
    {
        EntryOrGuid = r.EntryOrGuid, SourceType = r.SourceType, Id = r.Id, Link = r.Link,
        EventType = r.EventType, EventPhaseMask = r.EventPhaseMask, EventChance = r.EventChance, EventFlags = r.EventFlags,
        EventParam1 = r.EventParam1, EventParam2 = r.EventParam2, EventParam3 = r.EventParam3,
        EventParam4 = r.EventParam4, EventParam5 = r.EventParam5, EventParam6 = r.EventParam6,
        ActionType = r.ActionType, ActionParam1 = r.ActionParam1, ActionParam2 = r.ActionParam2, ActionParam3 = r.ActionParam3,
        ActionParam4 = r.ActionParam4, ActionParam5 = r.ActionParam5, ActionParam6 = r.ActionParam6,
        TargetType = r.TargetType, TargetParam1 = r.TargetParam1, TargetParam2 = r.TargetParam2,
        TargetParam3 = r.TargetParam3, TargetParam4 = r.TargetParam4,
        TargetX = r.TargetX, TargetY = r.TargetY, TargetZ = r.TargetZ, TargetO = r.TargetO, Comment = r.Comment,
    };
}
