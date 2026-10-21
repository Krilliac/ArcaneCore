using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.WorldState;

/// <summary>
/// <c>game_event_playerbot_group</c> row: one group of bots a game event brings up (docs/areas/playbots-scenarios-events.md). The
/// property names are the columns in PascalCase; the database columns are lower snake case.
/// </summary>
public sealed class GameEventPlayerbotGroupRow
{
    /// <summary><c>game_event.entry</c>.</summary>
    public uint Event { get; set; }

    public uint GroupId { get; set; }

    /// <summary>Bots are named this prefix plus a generated suffix; the claim matches the prefix only.</summary>
    public string NamePrefix { get; set; } = string.Empty;

    public int Count { get; set; }

    /// <summary>0 Alliance, 1 Horde.</summary>
    public byte Team { get; set; }

    /// <summary>Comma-separated <c>race:class</c> pairs, cycled over the bots.</summary>
    public string ClassSpec { get; set; } = string.Empty;

    /// <summary>The level the bots are boosted to (0: leave them).</summary>
    public byte Level { get; set; }

    /// <summary>True: create missing bots up to <see cref="Count"/>; false: only use bots that exist.</summary>
    public bool Provision { get; set; }

    public uint StartMap { get; set; }

    public float StartX { get; set; }

    public float StartY { get; set; }

    public float StartZ { get; set; }

    public float StartO { get; set; }

    /// <summary>0 none, 1 defend, 2 siege attacker.</summary>
    public byte Role { get; set; }

    public string? Comment { get; set; }
}

/// <summary><c>game_event_playerbot_post</c> row: a defend post of a group.</summary>
public sealed class GameEventPlayerbotPostRow
{
    public uint Event { get; set; }

    public uint GroupId { get; set; }

    public uint PostId { get; set; }

    public uint Map { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Radius { get; set; }

    public string? Comment { get; set; }
}

/// <summary><c>game_event_playerbot_siege</c> row: what a siege group attacks.</summary>
public sealed class GameEventPlayerbotSiegeRow
{
    public uint Event { get; set; }

    public uint GroupId { get; set; }

    /// <summary>The creature entry whose death ends the siege.</summary>
    public uint ObjectiveEntry { get; set; }

    public byte Raids { get; set; }

    /// <summary>The siege starts when this percentage of the group is placed, alive and at level.</summary>
    public byte MinReadyPct { get; set; } = 100;

    public string? Comment { get; set; }
}

/// <summary><c>game_event_playerbot_waypoint</c> row: one point of a siege route.</summary>
public sealed class GameEventPlayerbotWaypointRow
{
    public uint Event { get; set; }

    public uint GroupId { get; set; }

    public uint Seq { get; set; }

    public uint Map { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public string? Comment { get; set; }
}

/// <summary>The four bot scenario tables, read together.</summary>
public sealed record GameEventPlayerbotContent(
    IReadOnlyList<GameEventPlayerbotGroupRow> Groups,
    IReadOnlyList<GameEventPlayerbotPostRow> Posts,
    IReadOnlyList<GameEventPlayerbotSiegeRow> Sieges,
    IReadOnlyList<GameEventPlayerbotWaypointRow> Waypoints)
{
    /// <summary>No rows.</summary>
    public static GameEventPlayerbotContent Empty { get; } = new([], [], [], []);
}

/// <summary>Read access to the bot scenario tables of the world database.</summary>
public interface IGameEventPlayerbotStore
{
    /// <summary>Every row of the four tables, in key order.</summary>
    Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The bot scenario tables (docs/areas/playbots-scenarios-events.md): <c>game_event_playerbot_group</c>, <c>_post</c>, <c>_siege</c> and
/// <c>_waypoint</c>, all keyed by <c>game_event.entry</c>. A game event that has rows here brings a scenario of bots up when it starts and
/// takes it down when it stops. Operator content: the schema step bundles no rows; the shipped scenarios are idempotent SQL under
/// <c>sql/content/world</c>. The version is the next free world step (the integrator renumbers this one constant; tests refer to it,
/// never to a literal).
/// </summary>
public sealed class GameEventPlayerbotDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 54;

    public const string GroupTable = "game_event_playerbot_group";
    public const string PostTable = "game_event_playerbot_post";
    public const string SiegeTable = "game_event_playerbot_siege";
    public const string WaypointTable = "game_event_playerbot_waypoint";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(GroupTable),
        new CreateTableChange(PostTable),
        new CreateTableChange(SiegeTable),
        new CreateTableChange(WaypointTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<GameEventPlayerbotGroupRow>(entity =>
        {
            entity.ToTable(GroupTable);
            entity.HasKey(r => new { r.Event, r.GroupId });
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
            entity.Property(r => r.GroupId).HasColumnName("group_id").ValueGeneratedNever();
            entity.Property(r => r.NamePrefix).HasColumnName("name_prefix").HasMaxLength(32).IsRequired();
            entity.Property(r => r.Count).HasColumnName("count");
            entity.Property(r => r.Team).HasColumnName("team");
            entity.Property(r => r.ClassSpec).HasColumnName("class_spec").HasMaxLength(255).IsRequired();
            entity.Property(r => r.Level).HasColumnName("level");
            entity.Property(r => r.Provision).HasColumnName("provision");
            entity.Property(r => r.StartMap).HasColumnName("start_map");
            entity.Property(r => r.StartX).HasColumnName("start_x");
            entity.Property(r => r.StartY).HasColumnName("start_y");
            entity.Property(r => r.StartZ).HasColumnName("start_z");
            entity.Property(r => r.StartO).HasColumnName("start_o");
            entity.Property(r => r.Role).HasColumnName("role");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });

        modelBuilder.Entity<GameEventPlayerbotPostRow>(entity =>
        {
            entity.ToTable(PostTable);
            entity.HasKey(r => new { r.Event, r.GroupId, r.PostId });
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
            entity.Property(r => r.GroupId).HasColumnName("group_id").ValueGeneratedNever();
            entity.Property(r => r.PostId).HasColumnName("post_id").ValueGeneratedNever();
            entity.Property(r => r.Map).HasColumnName("map");
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.Z).HasColumnName("z");
            entity.Property(r => r.Radius).HasColumnName("radius");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });

        modelBuilder.Entity<GameEventPlayerbotSiegeRow>(entity =>
        {
            entity.ToTable(SiegeTable);
            entity.HasKey(r => new { r.Event, r.GroupId });
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
            entity.Property(r => r.GroupId).HasColumnName("group_id").ValueGeneratedNever();
            entity.Property(r => r.ObjectiveEntry).HasColumnName("objective_entry");
            entity.Property(r => r.Raids).HasColumnName("raids");
            entity.Property(r => r.MinReadyPct).HasColumnName("min_ready_pct");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });

        modelBuilder.Entity<GameEventPlayerbotWaypointRow>(entity =>
        {
            entity.ToTable(WaypointTable);
            entity.HasKey(r => new { r.Event, r.GroupId, r.Seq });
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
            entity.Property(r => r.GroupId).HasColumnName("group_id").ValueGeneratedNever();
            entity.Property(r => r.Seq).HasColumnName("seq").ValueGeneratedNever();
            entity.Property(r => r.Map).HasColumnName("map");
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.Z).HasColumnName("z");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IGameEventPlayerbotStore, EfGameEventPlayerbotStore>();
}

/// <summary>EF Core implementation of <see cref="IGameEventPlayerbotStore"/>.</summary>
public sealed class EfGameEventPlayerbotStore(WorldDbContext db) : IGameEventPlayerbotStore
{
    public async Task<GameEventPlayerbotContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<GameEventPlayerbotGroupRow> groups = await db.Set<GameEventPlayerbotGroupRow>().AsNoTracking()
            .OrderBy(r => r.Event).ThenBy(r => r.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GameEventPlayerbotPostRow> posts = await db.Set<GameEventPlayerbotPostRow>().AsNoTracking()
            .OrderBy(r => r.Event).ThenBy(r => r.GroupId).ThenBy(r => r.PostId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GameEventPlayerbotSiegeRow> sieges = await db.Set<GameEventPlayerbotSiegeRow>().AsNoTracking()
            .OrderBy(r => r.Event).ThenBy(r => r.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GameEventPlayerbotWaypointRow> waypoints = await db.Set<GameEventPlayerbotWaypointRow>().AsNoTracking()
            .OrderBy(r => r.Event).ThenBy(r => r.GroupId).ThenBy(r => r.Seq).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new GameEventPlayerbotContent(groups, posts, sieges, waypoints);
    }
}
