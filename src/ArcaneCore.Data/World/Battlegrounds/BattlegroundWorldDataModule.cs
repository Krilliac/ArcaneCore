using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Battlegrounds;

/// <summary>
/// <c>battleground_template</c> row: the cmangos classic-db columns plus the four vmangos mark spells (0 when the dump has none). Keyed by
/// <see cref="Id"/> (the battleground type: 1 Alterac Valley, 2 Warsong Gulch, 3 Arathi Basin).
/// </summary>
public sealed class BattlegroundTemplateRow
{
    public uint Id { get; set; }

    public uint MinPlayersPerTeam { get; set; }

    public uint MaxPlayersPerTeam { get; set; }

    public uint MinLevel { get; set; }

    public uint MaxLevel { get; set; }

    public uint AllianceWinSpell { get; set; }

    public uint AllianceLoseSpell { get; set; }

    public uint HordeWinSpell { get; set; }

    public uint HordeLoseSpell { get; set; }

    public uint AllianceStartLoc { get; set; }

    public uint HordeStartLoc { get; set; }

    public float StartMaxDist { get; set; }

    public uint PlayerSkinRefLootId { get; set; }
}

/// <summary>A <c>creature_battleground</c> row: the creature spawn guid and its event pair.</summary>
public sealed class CreatureBattlegroundRow
{
    public uint Guid { get; set; }

    public byte Event1 { get; set; }

    public byte Event2 { get; set; }
}

/// <summary>A <c>gameobject_battleground</c> row: the game object spawn guid and its event pair.</summary>
public sealed class GameObjectBattlegroundRow
{
    public uint Guid { get; set; }

    public byte Event1 { get; set; }

    public byte Event2 { get; set; }
}

/// <summary><c>battlemaster_entry</c> row: creature entry and battleground type.</summary>
public sealed class BattlemasterEntryRow
{
    public uint Entry { get; set; }

    public uint BattlegroundTemplate { get; set; }
}

/// <summary>
/// The battleground content tables of the world database (docs/areas/battlegrounds.md): <c>battleground_template</c>,
/// <c>creature_battleground</c>, <c>gameobject_battleground</c> and <c>battlemaster_entry</c>, in the cmangos classic-db layout that the
/// content importer reads (the vmangos tables have the same names and key columns). The event tables gate the battleground spawns
/// (vmangos <c>BattleGround::SpawnEvent</c>); the templates hold the player limits, levels and start locations.
/// <para>
/// <b>World version 40</b>: reserved as 44 in the wave-2 plan and renumbered down at the 2026-10-07 integration, which
/// closed the unclaimed numbers (docs/integration/wave2-20261007.md). Tests read <see cref="Version"/>, never a literal.
/// No cleanup registration: the world schema holds no per-character rows.
/// </para>
/// </summary>
public sealed class BattlegroundWorldDataModule : IDataModule
{
    /// <summary>The world schema version of this step (wave-2 reservation for the battlegrounds lane).</summary>
    public const int Version = 40; // reserved as 44 in the wave-2 plan; renumbered down at the 2026-10-07 integration (no gaps)

    public const string TemplateTable = "battleground_template";
    public const string CreatureEventTable = "creature_battleground";
    public const string GameObjectEventTable = "gameobject_battleground";
    public const string BattlemasterTable = "battlemaster_entry";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(TemplateTable),
        new CreateTableChange(CreatureEventTable),
        new CreateTableChange(GameObjectEventTable),
        new CreateTableChange(BattlemasterTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BattlegroundTemplateRow>(entity =>
        {
            entity.ToTable(TemplateTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.MinPlayersPerTeam).HasColumnName("MinPlayersPerTeam");
            entity.Property(r => r.MaxPlayersPerTeam).HasColumnName("MaxPlayersPerTeam");
            entity.Property(r => r.MinLevel).HasColumnName("MinLvl");
            entity.Property(r => r.MaxLevel).HasColumnName("MaxLvl");
            entity.Property(r => r.AllianceWinSpell).HasColumnName("AllianceWinSpell");
            entity.Property(r => r.AllianceLoseSpell).HasColumnName("AllianceLoseSpell");
            entity.Property(r => r.HordeWinSpell).HasColumnName("HordeWinSpell");
            entity.Property(r => r.HordeLoseSpell).HasColumnName("HordeLoseSpell");
            entity.Property(r => r.AllianceStartLoc).HasColumnName("AllianceStartLoc");
            entity.Property(r => r.HordeStartLoc).HasColumnName("HordeStartLoc");
            entity.Property(r => r.StartMaxDist).HasColumnName("StartMaxDist");
            entity.Property(r => r.PlayerSkinRefLootId).HasColumnName("PlayerSkinReflootId");
        });

        modelBuilder.Entity<CreatureBattlegroundRow>(entity =>
        {
            entity.ToTable(CreatureEventTable);
            entity.HasKey(r => new { r.Guid, r.Event1, r.Event2 });
            entity.Property(r => r.Guid).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Event1).HasColumnName("event1").ValueGeneratedNever();
            entity.Property(r => r.Event2).HasColumnName("event2").ValueGeneratedNever();
        });

        modelBuilder.Entity<GameObjectBattlegroundRow>(entity =>
        {
            entity.ToTable(GameObjectEventTable);
            entity.HasKey(r => new { r.Guid, r.Event1, r.Event2 });
            entity.Property(r => r.Guid).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Event1).HasColumnName("event1").ValueGeneratedNever();
            entity.Property(r => r.Event2).HasColumnName("event2").ValueGeneratedNever();
        });

        modelBuilder.Entity<BattlemasterEntryRow>(entity =>
        {
            entity.ToTable(BattlemasterTable);
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.BattlegroundTemplate).HasColumnName("bg_template");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IBattlegroundContentStore, EfBattlegroundContentStore>();
}

/// <summary>EF Core implementation of <see cref="IBattlegroundContentStore"/>.</summary>
public sealed class EfBattlegroundContentStore(WorldDbContext db) : IBattlegroundContentStore
{
    public async Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<BattlegroundTemplateRecord> templates = (await db.Set<BattlegroundTemplateRow>().AsNoTracking().OrderBy(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new BattlegroundTemplateRecord(r.Id, r.MinPlayersPerTeam, r.MaxPlayersPerTeam, r.MinLevel, r.MaxLevel, r.AllianceStartLoc,
                r.HordeStartLoc, r.StartMaxDist, r.PlayerSkinRefLootId, r.AllianceWinSpell, r.AllianceLoseSpell, r.HordeWinSpell, r.HordeLoseSpell))
            .ToList();
        List<BattlegroundEventIndex> creatures = (await db.Set<CreatureBattlegroundRow>().AsNoTracking().OrderBy(r => r.Guid).ThenBy(r => r.Event1).ThenBy(r => r.Event2)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new BattlegroundEventIndex(r.Guid, r.Event1, r.Event2))
            .ToList();
        List<BattlegroundEventIndex> objects = (await db.Set<GameObjectBattlegroundRow>().AsNoTracking().OrderBy(r => r.Guid).ThenBy(r => r.Event1).ThenBy(r => r.Event2)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new BattlegroundEventIndex(r.Guid, r.Event1, r.Event2))
            .ToList();
        List<BattlemasterRecord> masters = (await db.Set<BattlemasterEntryRow>().AsNoTracking().OrderBy(r => r.Entry)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => new BattlemasterRecord(r.Entry, r.BattlegroundTemplate))
            .ToList();
        return new BattlegroundContent(templates, creatures, objects, masters);
    }
}
