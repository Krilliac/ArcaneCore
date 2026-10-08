using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// <c>broadcast_text</c>: the lines EventAI text actions speak. Columns are the ones mangos-classic
/// <c>ObjectMgr::LoadBroadcastText</c> reads (Globals/ObjectMgr.cpp:7786-7821); the dump's
/// <c>Text</c> / <c>Text1</c> are the male and female text.
/// </summary>
public sealed class BroadcastTextRow
{
    public uint Id { get; set; }

    public string Text { get; set; } = string.Empty;

    public string FemaleText { get; set; } = string.Empty;

    public byte ChatType { get; set; }

    public byte Language { get; set; }

    public uint SoundId { get; set; }

    public uint EmoteId1 { get; set; }

    public uint EmoteId2 { get; set; }

    public uint EmoteId3 { get; set; }

    public uint EmoteDelay1 { get; set; }

    public uint EmoteDelay2 { get; set; }

    public uint EmoteDelay3 { get; set; }
}

/// <summary><c>creature_ai_summons</c>: EventAI summon locations (cmangos-classic mangos.sql).</summary>
public sealed class CreatureAiSummonRow
{
    public uint Id { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Orientation { get; set; }

    /// <summary>The <c>spawntimesecs</c> column; it holds milliseconds despite its name (<see cref="CreatureAiSummon.LifetimeMs"/>).</summary>
    public uint SpawnTimeSeconds { get; set; } = 120;

    public string Comment { get; set; } = string.Empty;
}

/// <summary>
/// The creature behaviour world-schema step (<see cref="IDataModule"/>): the columns and tables the
/// vanilla AI model needs and the first AI step dropped. It adds the full-width EventAI
/// <c>EventFlags32</c> (the world-8 byte column stays; schema changes are additive only), the EventAI
/// parameters 5 and 6, the spawn-guid key of guid-scoped EventAI rows, the AI text sound and
/// broadcast id, <c>broadcast_text</c>, <c>creature_ai_summons</c>, and the creature template columns
/// <c>Detection</c>, <c>CallForHelp</c>, <c>Pursuit</c>, <c>Leash</c>, <c>Timeout</c>,
/// <c>StaticFlags1/2</c> and the <c>ExtraFlags</c> dialect tag.
/// <para>
/// <b>World version 12</b>: the number this step has after the lanes were renumbered in merge order (it was allocated
/// as 11, the next free number after the quest reputation reward step, world 10). It is named once here; tests read <see cref="Version"/> or
/// <c>WorldDbContext.Schema.CurrentVersion</c>, never a literal, and the integration lead renumbers
/// in merge order (docs/integration/seams.md).
/// </para>
/// </summary>
public sealed class CreatureBehaviourDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 12;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("broadcast_text"),
        new CreateTableChange("creature_ai_summons"),
        new AddColumnChange("creature_ai_scripts", nameof(CreatureAiScriptRow.CreatureGuid)),
        new AddColumnChange("creature_ai_scripts", nameof(CreatureAiScriptRow.EventFlags32)),
        new AddColumnChange("creature_ai_scripts", nameof(CreatureAiScriptRow.EventParam5)),
        new AddColumnChange("creature_ai_scripts", nameof(CreatureAiScriptRow.EventParam6)),
        new AddColumnChange("creature_ai_texts", nameof(CreatureAiTextRow.Sound)),
        new AddColumnChange("creature_ai_texts", nameof(CreatureAiTextRow.BroadcastTextId)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.Detection)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.CallForHelp)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.Pursuit)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.Leash)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.Timeout)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.StaticFlags1)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.StaticFlags2)),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.ExtraFlagsDialect)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BroadcastTextRow>(entity =>
        {
            entity.ToTable("broadcast_text");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();

            // classic-db z2815: Text and Text1 are `text` columns; the longest row is 511 characters.
            entity.Property(r => r.Text).HasMaxLength(2000).IsRequired();
            entity.Property(r => r.FemaleText).HasMaxLength(2000).IsRequired();
        });

        modelBuilder.Entity<CreatureAiSummonRow>(entity =>
        {
            entity.ToTable("creature_ai_summons");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Comment).HasMaxLength(255).IsRequired();
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
