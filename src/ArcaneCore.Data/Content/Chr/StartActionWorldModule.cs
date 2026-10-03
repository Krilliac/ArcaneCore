using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Chr;

/// <summary>
/// One <c>playercreateinfo_action</c> row: a button of the action bar every new character of this
/// race and class starts with (cmangos classic-db and vmangos: race, class, button, action, type).
/// </summary>
public sealed class PlayerCreateActionRow
{
    public byte Race { get; set; }

    public byte Class { get; set; }

    public byte Button { get; set; }

    public uint Action { get; set; }

    public byte Type { get; set; }
}

/// <summary>
/// World schema step for <c>playercreateinfo_action</c>. Allocated as the next free world version
/// after <c>creature_onkill_reputation</c> (14); the integrator renumbers this one constant when
/// other world steps merge first (tests refer to it, never to a literal). It also registers
/// <see cref="EfStartActionSource"/>. No cleanup registration: the world schema holds no
/// per-character rows.
/// </summary>
public sealed class StartActionWorldModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 15;

    /// <summary>The table name.</summary>
    public const string Table = "playercreateinfo_action";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        // classic-db: playercreateinfo_action (race, class, button, action, type), PK (race, class, button).
        modelBuilder.Entity<PlayerCreateActionRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.Race, r.Class, r.Button });
            entity.Property(r => r.Race).HasColumnName("race");
            entity.Property(r => r.Class).HasColumnName("class");
            entity.Property(r => r.Button).HasColumnName("button");
            entity.Property(r => r.Action).HasColumnName("action");
            entity.Property(r => r.Type).HasColumnName("type");
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IStartActionSource, EfStartActionSource>();
}

/// <summary>Reads the starting action bars from the world database.</summary>
public sealed class EfStartActionSource(WorldDbContext db) : IStartActionSource
{
    public async Task<IReadOnlyList<ActionButton>> GetAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => await db.Set<PlayerCreateActionRow>().AsNoTracking()
            .Where(r => r.Race == race && r.Class == cls)
            .OrderBy(r => r.Button)
            .Select(r => new ActionButton(r.Button, r.Action, r.Type))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
