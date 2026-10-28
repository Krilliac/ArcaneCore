using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Npc;

/// <summary>
/// <c>npc_text_broadcast_text</c> (classic-db / mangos-classic; the table vmangos names the same way): an NPC text whose eight weighted
/// variants are <c>broadcast_text</c> ids instead of inline strings. Columns <c>Id</c>, <c>Prob0..7</c>, <c>BroadcastTextId0..7</c>.
/// <see cref="EfNpcContentStore"/> resolves a row against <c>broadcast_text</c> into an <see cref="NpcText"/> when no inline
/// <c>npc_text</c> row has the same id.
/// </summary>
public sealed class NpcTextBroadcastRow
{
    public uint Id { get; set; }

    public float Prob0 { get; set; }

    public float Prob1 { get; set; }

    public float Prob2 { get; set; }

    public float Prob3 { get; set; }

    public float Prob4 { get; set; }

    public float Prob5 { get; set; }

    public float Prob6 { get; set; }

    public float Prob7 { get; set; }

    public uint BroadcastTextId0 { get; set; }

    public uint BroadcastTextId1 { get; set; }

    public uint BroadcastTextId2 { get; set; }

    public uint BroadcastTextId3 { get; set; }

    public uint BroadcastTextId4 { get; set; }

    public uint BroadcastTextId5 { get; set; }

    public uint BroadcastTextId6 { get; set; }

    public uint BroadcastTextId7 { get; set; }
}

/// <summary>World schema 57: <c>npc_text_broadcast_text</c>, the broadcast-text form of gossip greeting texts.</summary>
public sealed class NpcTextBroadcastDataModule : IDataModule
{
    /// <summary>The world schema version this module arrives in.</summary>
    public const int Version = 57;

    public const string Table = "npc_text_broadcast_text";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(Table),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NpcTextBroadcastRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}
