using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.WorldState;

/// <summary>
/// <c>game_event</c> row. The property names are what <see cref="GameEventDumpImporter"/> maps source columns onto (names equal
/// ignoring case and underscores, so mangos-classic <c>linkedTo</c> and vmangos <c>patch_min</c> need no alias); the database
/// columns are lower snake case. The dates are source text (see <see cref="GameEventRecord"/>).
/// </summary>
public sealed class GameEventRow
{
    public uint Entry { get; set; }
    public int ScheduleType { get; set; }
    public uint Occurence { get; set; }
    public uint Length { get; set; }
    public uint Holiday { get; set; }
    public uint LinkedTo { get; set; }
    public string? Description { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public bool Hardcoded { get; set; }
    public bool Disabled { get; set; }
    public byte PatchMin { get; set; }
    public byte PatchMax { get; set; } = 10;
}

/// <summary><c>game_event_time</c> row (mangos-classic).</summary>
public sealed class GameEventTimeRow
{
    public uint Entry { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
}

/// <summary><c>game_event_creature</c> row.</summary>
public sealed class GameEventCreatureRow
{
    public uint Guid { get; set; }
    public int Event { get; set; }
}

/// <summary><c>game_event_gameobject</c> row.</summary>
public sealed class GameEventGameObjectRow
{
    public uint Guid { get; set; }
    public int Event { get; set; }
}

/// <summary><c>game_event_creature_data</c> row (vmangos <c>display_id</c> maps onto <see cref="ModelId"/>).</summary>
public sealed class GameEventCreatureDataRow
{
    public uint Guid { get; set; }
    public int Event { get; set; }
    public uint EntryId { get; set; }
    public uint ModelId { get; set; }
    public uint EquipmentId { get; set; }
    public uint SpellStart { get; set; }
    public uint SpellEnd { get; set; }
}

/// <summary><c>game_event_quest</c> row.</summary>
public sealed class GameEventQuestRow
{
    public uint Quest { get; set; }
    public int Event { get; set; }
}

/// <summary><c>game_event_mail</c> row.</summary>
public sealed class GameEventMailRow
{
    public int Event { get; set; }
    public uint RaceMask { get; set; }
    public uint Quest { get; set; }
    public uint MailTemplateId { get; set; }
    public uint SenderEntry { get; set; }
}

/// <summary>
/// The game-event tables of the world database (docs/areas/game-events-weather.md): <c>game_event</c> (a superset of the vmangos
/// and mangos-classic columns), <c>game_event_time</c>, <c>game_event_creature</c>, <c>game_event_gameobject</c>,
/// <c>game_event_creature_data</c>, <c>game_event_quest</c> and <c>game_event_mail</c>. The data comes from the operator's own
/// content (<see cref="GameEventDumpImporter"/>); nothing is bundled. All identifiers are lower snake case (PostgreSQL folds
/// unquoted case; EF quotes, but the names stay stable on every provider). The version is one constant the integrator renumbers.
/// </summary>
public sealed class GameEventDataModule : IDataModule
{
    /// <summary>The next free world schema version at this base (the highest before it is the start-action step, 20).</summary>
    public const int Version = 21;

    public const string EventTable = "game_event";
    public const string TimeTable = "game_event_time";
    public const string CreatureTable = "game_event_creature";
    public const string GameObjectTable = "game_event_gameobject";
    public const string CreatureDataTable = "game_event_creature_data";
    public const string QuestTable = "game_event_quest";
    public const string MailTable = "game_event_mail";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    // Each CreateTableChange is re-runnable (the bootstrapper skips an existing table), which matters on MariaDB where DDL is
    // not transactional and a failed step may have created some tables already.
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(EventTable),
        new CreateTableChange(TimeTable),
        new CreateTableChange(CreatureTable),
        new CreateTableChange(GameObjectTable),
        new CreateTableChange(CreatureDataTable),
        new CreateTableChange(QuestTable),
        new CreateTableChange(MailTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<GameEventRow>(entity =>
        {
            entity.ToTable(EventTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.ScheduleType).HasColumnName("schedule_type");
            entity.Property(r => r.Occurence).HasColumnName("occurence");
            entity.Property(r => r.Length).HasColumnName("length");
            entity.Property(r => r.Holiday).HasColumnName("holiday");
            entity.Property(r => r.LinkedTo).HasColumnName("linked_to");
            entity.Property(r => r.Description).HasColumnName("description").HasMaxLength(255);
            entity.Property(r => r.StartTime).HasColumnName("start_time").HasMaxLength(32);
            entity.Property(r => r.EndTime).HasColumnName("end_time").HasMaxLength(32);
            entity.Property(r => r.Hardcoded).HasColumnName("hardcoded");
            entity.Property(r => r.Disabled).HasColumnName("disabled");
            entity.Property(r => r.PatchMin).HasColumnName("patch_min");
            entity.Property(r => r.PatchMax).HasColumnName("patch_max");
        });

        modelBuilder.Entity<GameEventTimeRow>(entity =>
        {
            entity.ToTable(TimeTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.StartTime).HasColumnName("start_time").HasMaxLength(32).IsRequired();
            entity.Property(r => r.EndTime).HasColumnName("end_time").HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<GameEventCreatureRow>(entity =>
        {
            entity.ToTable(CreatureTable);
            entity.HasKey(r => new { r.Guid, r.Event });
            entity.Property(r => r.Guid).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
        });

        modelBuilder.Entity<GameEventGameObjectRow>(entity =>
        {
            entity.ToTable(GameObjectTable);
            entity.HasKey(r => new { r.Guid, r.Event });
            entity.Property(r => r.Guid).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
        });

        modelBuilder.Entity<GameEventCreatureDataRow>(entity =>
        {
            entity.ToTable(CreatureDataTable);
            entity.HasKey(r => new { r.Guid, r.Event });
            entity.Property(r => r.Guid).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
            entity.Property(r => r.EntryId).HasColumnName("entry_id");
            entity.Property(r => r.ModelId).HasColumnName("modelid");
            entity.Property(r => r.EquipmentId).HasColumnName("equipment_id");
            entity.Property(r => r.SpellStart).HasColumnName("spell_start");
            entity.Property(r => r.SpellEnd).HasColumnName("spell_end");
        });

        modelBuilder.Entity<GameEventQuestRow>(entity =>
        {
            entity.ToTable(QuestTable);
            entity.HasKey(r => new { r.Quest, r.Event });
            entity.Property(r => r.Quest).HasColumnName("quest").ValueGeneratedNever();
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
        });

        modelBuilder.Entity<GameEventMailRow>(entity =>
        {
            entity.ToTable(MailTable);
            entity.HasKey(r => new { r.Event, r.RaceMask, r.Quest });
            entity.Property(r => r.Event).HasColumnName("event").ValueGeneratedNever();
            entity.Property(r => r.RaceMask).HasColumnName("race_mask").ValueGeneratedNever();
            entity.Property(r => r.Quest).HasColumnName("quest").ValueGeneratedNever();
            entity.Property(r => r.MailTemplateId).HasColumnName("mail_template_id");
            entity.Property(r => r.SenderEntry).HasColumnName("sender_entry");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IGameEventDataStore, EfGameEventDataStore>();
}

/// <summary>EF Core implementation of <see cref="IGameEventDataStore"/>: rows in key order, so a load is deterministic.</summary>
public sealed class EfGameEventDataStore(WorldDbContext db) : IGameEventDataStore
{
    public async Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<GameEventRecord> events = (await db.Set<GameEventRow>().AsNoTracking().OrderBy(r => r.Entry).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameEventRecord(
                r.Entry, r.ScheduleType, r.Occurence, r.Length, r.Holiday, r.LinkedTo, r.Description ?? string.Empty,
                r.StartTime, r.EndTime, r.Hardcoded, r.Disabled, r.PatchMin, r.PatchMax))
            .ToList();
        List<GameEventTimeRecord> times = (await db.Set<GameEventTimeRow>().AsNoTracking().OrderBy(r => r.Entry).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameEventTimeRecord(r.Entry, r.StartTime, r.EndTime))
            .ToList();
        List<GameEventSpawnRecord> creatures = (await db.Set<GameEventCreatureRow>().AsNoTracking().OrderBy(r => r.Event).ThenBy(r => r.Guid).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameEventSpawnRecord(r.Guid, r.Event))
            .ToList();
        List<GameEventSpawnRecord> gameObjects = (await db.Set<GameEventGameObjectRow>().AsNoTracking().OrderBy(r => r.Event).ThenBy(r => r.Guid).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameEventSpawnRecord(r.Guid, r.Event))
            .ToList();
        List<GameEventCreatureDataRecord> creatureData = (await db.Set<GameEventCreatureDataRow>().AsNoTracking().OrderBy(r => r.Event).ThenBy(r => r.Guid).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameEventCreatureDataRecord(r.Guid, r.Event, r.EntryId, r.ModelId, r.EquipmentId, r.SpellStart, r.SpellEnd))
            .ToList();
        List<GameEventQuestRecord> quests = (await db.Set<GameEventQuestRow>().AsNoTracking().OrderBy(r => r.Event).ThenBy(r => r.Quest).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameEventQuestRecord(r.Quest, r.Event))
            .ToList();
        List<GameEventMailRecord> mails = (await db.Set<GameEventMailRow>().AsNoTracking().OrderBy(r => r.Event).ThenBy(r => r.RaceMask).ThenBy(r => r.Quest).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new GameEventMailRecord(r.Event, r.RaceMask, r.Quest, r.MailTemplateId, r.SenderEntry))
            .ToList();
        return new GameEventContent(events, times, creatures, gameObjects, creatureData, quests, mails);
    }

    public async Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default)
        => await db.Set<GameEventRow>().Where(r => r.Entry == entry)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Disabled, disabled), cancellationToken).ConfigureAwait(false);
}
