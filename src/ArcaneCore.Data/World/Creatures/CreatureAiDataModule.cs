using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Creatures;

/// <summary><c>creature_ai_scripts</c>: one cmangos-classic EventAI row (doc/EventAI.txt column names).</summary>
public sealed class CreatureAiScriptRow
{
    public uint Id { get; set; }

    /// <summary>The creature entry (0 when the row is keyed by spawn guid, see <see cref="CreatureGuid"/>).</summary>
    public uint CreatureId { get; set; }

    /// <summary>The spawn guid when the dump's <c>creature_id</c> was negative (cmangos CreatureEventAIMgr.cpp:233-252). Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint CreatureGuid { get; set; }

    public byte EventType { get; set; }

    public uint EventInversePhaseMask { get; set; }

    public byte EventChance { get; set; } = 100;

    /// <summary>The low byte of the event flags: the column the world-8 step created. <see cref="EventFlags32"/> wins when set.</summary>
    public byte EventFlags { get; set; }

    /// <summary>The full 32-bit <c>event_flags</c> (cmangos reads it with GetUInt32; classic-db carries 1024/1025). Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint EventFlags32 { get; set; }

    public int EventParam1 { get; set; }

    public int EventParam2 { get; set; }

    public int EventParam3 { get; set; }

    public int EventParam4 { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/> (cmangos reads event_param1-6).</summary>
    public int EventParam5 { get; set; }

    /// <summary>Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public int EventParam6 { get; set; }

    public byte Action1Type { get; set; }

    public int Action1Param1 { get; set; }

    public int Action1Param2 { get; set; }

    public int Action1Param3 { get; set; }

    public byte Action2Type { get; set; }

    public int Action2Param1 { get; set; }

    public int Action2Param2 { get; set; }

    public int Action2Param3 { get; set; }

    public byte Action3Type { get; set; }

    public int Action3Param1 { get; set; }

    public int Action3Param2 { get; set; }

    public int Action3Param3 { get; set; }

    public string Comment { get; set; } = string.Empty;
}

/// <summary><c>creature_ai_texts</c>: EventAI text lines (cmangos-classic; negative entries).</summary>
public sealed class CreatureAiTextRow
{
    public int Entry { get; set; }

    public string Content { get; set; } = string.Empty;

    public byte Type { get; set; }

    public uint Language { get; set; }

    public uint Emote { get; set; }

    /// <summary>SoundEntries.dbc id (cmangos <c>sound</c>). Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint Sound { get; set; }

    /// <summary>cmangos <c>broadcast_text_id</c>. Added by <see cref="CreatureBehaviourDataModule"/>.</summary>
    public uint BroadcastTextId { get; set; }
}

/// <summary>
/// The creature AI world-schema step (<see cref="IDataModule"/>): EventAI scripts and texts,
/// the <c>creature_template.AIName</c> selector and the <c>creature_movement.Run</c> flag.
/// <para>
/// <b>World version 8</b>, the integrated allocation after game objects + loot (world 7);
/// <see cref="ReservedVersion"/> and <see cref="Version"/> are both 8. Source branch heads that
/// lack the world-7 module kept a provisional 7 (docs/integration/creature-ai.md). Nothing else
/// depends on the number.
/// </para>
/// </summary>
public sealed class CreatureAiDataModule : IDataModule
{
    /// <summary>The reserved number for this step (fleet round 2).</summary>
    public const int ReservedVersion = 8;

    /// <summary>The number used in the integrated tree: the next free world version after game objects + loot (7).</summary>
    public const int Version = 8;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("creature_ai_scripts"),
        new CreateTableChange("creature_ai_texts"),
        new AddColumnChange("creature_template", nameof(CreatureTemplateRow.AIName)),
        new AddColumnChange("creature_movement", nameof(CreatureMovementRow.Run)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureAiScriptRow>(entity =>
        {
            entity.ToTable("creature_ai_scripts");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.HasIndex(r => r.CreatureId);
            entity.Property(r => r.Comment).HasMaxLength(255).IsRequired();
        });

        modelBuilder.Entity<CreatureAiTextRow>(entity =>
        {
            entity.ToTable("creature_ai_texts");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).ValueGeneratedNever();
            entity.Property(r => r.Content).HasMaxLength(2000).IsRequired();
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }

    internal static CreatureAiEvent ToEvent(CreatureAiScriptRow r) => new()
    {
        Id = r.Id,
        CreatureId = r.CreatureId,
        EventType = r.EventType,
        InversePhaseMask = r.EventInversePhaseMask,
        CreatureGuid = r.CreatureGuid,
        Chance = r.EventChance,

        // The 32-bit column wins; rows written by the world-8 importer only have the (clamped) byte.
        Flags = r.EventFlags32 != 0 ? r.EventFlags32 : r.EventFlags,
        Param1 = r.EventParam1,
        Param2 = r.EventParam2,
        Param3 = r.EventParam3,
        Param4 = r.EventParam4,
        Param5 = r.EventParam5,
        Param6 = r.EventParam6,
        Action1 = new CreatureAiAction(r.Action1Type, r.Action1Param1, r.Action1Param2, r.Action1Param3),
        Action2 = new CreatureAiAction(r.Action2Type, r.Action2Param1, r.Action2Param2, r.Action2Param3),
        Action3 = new CreatureAiAction(r.Action3Type, r.Action3Param1, r.Action3Param2, r.Action3Param3),
    };

    internal static CreatureAiText ToText(CreatureAiTextRow r)
        => new(r.Entry, r.Content, r.Type, r.Language, r.Emote) { Sound = r.Sound, BroadcastTextId = r.BroadcastTextId };
}
