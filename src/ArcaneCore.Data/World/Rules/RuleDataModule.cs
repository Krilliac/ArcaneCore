using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Rules;

/// <summary><c>rule</c> row (the property names are the columns in PascalCase; the database columns are lower snake case).</summary>
public sealed class RuleRow
{
    public int Id { get; set; }

    public byte Host { get; set; }

    public byte OwnerKind { get; set; }

    public int OwnerId { get; set; }

    public short EventType { get; set; }

    public int EventParam1 { get; set; }

    public int EventParam2 { get; set; }

    public int EventParam3 { get; set; }

    public int EventParam4 { get; set; }

    public int PhaseMask { get; set; }

    public int FilterId { get; set; }

    public byte ChancePct { get; set; } = 100;

    public int ConditionId { get; set; }

    public int CooldownMinMs { get; set; }

    public int CooldownMaxMs { get; set; }

    public short Priority { get; set; }

    public int Flags { get; set; }

    /// <summary>1 enabled, 0 disabled (a tinyint, so content files insert plain numbers on every provider).</summary>
    public byte Enabled { get; set; } = 1;

    public string? Comment { get; set; }
}

/// <summary>The shared shape of <c>rule_action</c> and <c>rule_list_action</c> rows.</summary>
public abstract class RuleActionRowBase
{
    public byte Seq { get; set; }

    public short ActionType { get; set; }

    public int Param1 { get; set; }

    public int Param2 { get; set; }

    public int Param3 { get; set; }

    public int Param4 { get; set; }

    public int Param5 { get; set; }

    public int Param6 { get; set; }

    public short TargetType { get; set; }

    public int TargetParam1 { get; set; }

    public int TargetParam2 { get; set; }

    public int TargetParam3 { get; set; }

    public int DelayMs { get; set; }

    public int Flags { get; set; }
}

/// <summary><c>rule_action</c> row: one action of a rule.</summary>
public sealed class RuleActionRow : RuleActionRowBase
{
    public int RuleId { get; set; }
}

/// <summary><c>rule_list</c> row: a reusable action list.</summary>
public sealed class RuleListRow
{
    public int Id { get; set; }

    public string? Comment { get; set; }
}

/// <summary><c>rule_list_action</c> row: one action of a rule list.</summary>
public sealed class RuleListActionRow : RuleActionRowBase
{
    public int ListId { get; set; }
}

/// <summary><c>rule_subject_filter</c> row.</summary>
public sealed class RuleSubjectFilterRow
{
    public int Id { get; set; }

    public int ClassMask { get; set; }

    public int RaceMask { get; set; }

    public byte LevelMin { get; set; }

    public byte LevelMax { get; set; }

    public byte Team { get; set; }

    public int MapId { get; set; }

    public int ZoneId { get; set; }

    public int AreaId { get; set; }

    public int SubjectFlags { get; set; }

    public byte SharePct { get; set; } = 100;

    public int ShareSeed { get; set; }

    public string? Comment { get; set; }
}

/// <summary><c>rule_text</c> row: the unified creature text a rule can say.</summary>
public sealed class RuleTextRow
{
    public int Id { get; set; }

    public string ContentDefault { get; set; } = string.Empty;

    public int Sound { get; set; }

    public byte Type { get; set; }

    public byte Language { get; set; }

    public int Emote { get; set; }

    public int BroadcastTextId { get; set; }

    public string? Comment { get; set; }
}

/// <summary>
/// World schema 58: the shared rule engine's tables (docs/superpowers/specs/2026-10-28-rule-engine-design.md, section 4):
/// <c>rule</c>, <c>rule_action</c>, <c>rule_list</c>, <c>rule_list_action</c>, <c>rule_subject_filter</c> and <c>rule_text</c>. Rule lists
/// keep their actions in a sibling table (<c>rule_list_action</c>) rather than behind a flag on <c>rule_action</c>, so a rule and a list
/// with the same id never share a key. Content arrives as idempotent SQL under <c>sql/content/world</c> (shipped ids below 1 000 000,
/// server-authored ids from 1 000 000); the schema step bundles no rows. The version is the next free world step (the integrator
/// renumbers this one constant; tests refer to it, never to a literal).
/// </summary>
public sealed class RuleDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 58;

    public const string RuleTable = "rule";
    public const string ActionTable = "rule_action";
    public const string ListTable = "rule_list";
    public const string ListActionTable = "rule_list_action";
    public const string FilterTable = "rule_subject_filter";
    public const string TextTable = "rule_text";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(RuleTable),
        new CreateTableChange(ActionTable),
        new CreateTableChange(ListTable),
        new CreateTableChange(ListActionTable),
        new CreateTableChange(FilterTable),
        new CreateTableChange(TextTable),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<RuleRow>(entity =>
        {
            entity.ToTable(RuleTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.Host).HasColumnName("host");
            entity.Property(r => r.OwnerKind).HasColumnName("owner_kind");
            entity.Property(r => r.OwnerId).HasColumnName("owner_id");
            entity.Property(r => r.EventType).HasColumnName("event_type");
            entity.Property(r => r.EventParam1).HasColumnName("event_param1");
            entity.Property(r => r.EventParam2).HasColumnName("event_param2");
            entity.Property(r => r.EventParam3).HasColumnName("event_param3");
            entity.Property(r => r.EventParam4).HasColumnName("event_param4");
            entity.Property(r => r.PhaseMask).HasColumnName("phase_mask");
            entity.Property(r => r.FilterId).HasColumnName("filter_id");
            entity.Property(r => r.ChancePct).HasColumnName("chance_pct");
            entity.Property(r => r.ConditionId).HasColumnName("condition_id");
            entity.Property(r => r.CooldownMinMs).HasColumnName("cooldown_min_ms");
            entity.Property(r => r.CooldownMaxMs).HasColumnName("cooldown_max_ms");
            entity.Property(r => r.Priority).HasColumnName("priority");
            entity.Property(r => r.Flags).HasColumnName("flags");
            entity.Property(r => r.Enabled).HasColumnName("enabled");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });

        modelBuilder.Entity<RuleActionRow>(entity =>
        {
            entity.ToTable(ActionTable);
            entity.HasKey(r => new { r.RuleId, r.Seq });
            entity.Property(r => r.RuleId).HasColumnName("rule_id").ValueGeneratedNever();
            MapAction(entity);
        });

        modelBuilder.Entity<RuleListRow>(entity =>
        {
            entity.ToTable(ListTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });

        modelBuilder.Entity<RuleListActionRow>(entity =>
        {
            entity.ToTable(ListActionTable);
            entity.HasKey(r => new { r.ListId, r.Seq });
            entity.Property(r => r.ListId).HasColumnName("list_id").ValueGeneratedNever();
            MapAction(entity);
        });

        modelBuilder.Entity<RuleSubjectFilterRow>(entity =>
        {
            entity.ToTable(FilterTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.ClassMask).HasColumnName("class_mask");
            entity.Property(r => r.RaceMask).HasColumnName("race_mask");
            entity.Property(r => r.LevelMin).HasColumnName("level_min");
            entity.Property(r => r.LevelMax).HasColumnName("level_max");
            entity.Property(r => r.Team).HasColumnName("team");
            entity.Property(r => r.MapId).HasColumnName("map_id");
            entity.Property(r => r.ZoneId).HasColumnName("zone_id");
            entity.Property(r => r.AreaId).HasColumnName("area_id");
            entity.Property(r => r.SubjectFlags).HasColumnName("subject_flags");
            entity.Property(r => r.SharePct).HasColumnName("share_pct");
            entity.Property(r => r.ShareSeed).HasColumnName("share_seed");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });

        modelBuilder.Entity<RuleTextRow>(entity =>
        {
            entity.ToTable(TextTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.ContentDefault).HasColumnName("content_default").HasMaxLength(2000).IsRequired();
            entity.Property(r => r.Sound).HasColumnName("sound");
            entity.Property(r => r.Type).HasColumnName("type");
            entity.Property(r => r.Language).HasColumnName("language");
            entity.Property(r => r.Emote).HasColumnName("emote");
            entity.Property(r => r.BroadcastTextId).HasColumnName("broadcast_text_id");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IRuleContentStore, EfRuleContentStore>();

    private static void MapAction<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : RuleActionRowBase
    {
        entity.Property(r => r.Seq).HasColumnName("seq").ValueGeneratedNever();
        entity.Property(r => r.ActionType).HasColumnName("action_type");
        entity.Property(r => r.Param1).HasColumnName("param1");
        entity.Property(r => r.Param2).HasColumnName("param2");
        entity.Property(r => r.Param3).HasColumnName("param3");
        entity.Property(r => r.Param4).HasColumnName("param4");
        entity.Property(r => r.Param5).HasColumnName("param5");
        entity.Property(r => r.Param6).HasColumnName("param6");
        entity.Property(r => r.TargetType).HasColumnName("target_type");
        entity.Property(r => r.TargetParam1).HasColumnName("target_param1");
        entity.Property(r => r.TargetParam2).HasColumnName("target_param2");
        entity.Property(r => r.TargetParam3).HasColumnName("target_param3");
        entity.Property(r => r.DelayMs).HasColumnName("delay_ms");
        entity.Property(r => r.Flags).HasColumnName("flags");
    }
}

/// <summary><c>playerbot_goal</c> row: the bot goal catalogue.</summary>
public sealed class PlayerbotGoalRow
{
    public short GoalId { get; set; }

    public string Name { get; set; } = string.Empty;

    public short BasePriority { get; set; }

    public byte Enabled { get; set; } = 1;

    public short Driver { get; set; }

    public int DriverParam1 { get; set; }

    public int DriverParam2 { get; set; }

    public int DriverParam3 { get; set; }

    public int DriverParam4 { get; set; }

    public string? Comment { get; set; }
}

/// <summary>
/// World schema 59: <c>playerbot_goal</c>, the bot goal catalogue keyed by the <c>PlayerbotGoalKind</c> value (custom goals from 1000),
/// read with the rule tables (the rule engine validates <c>bot.propose_goal</c> references against it). The bot host fills and reads it from
/// phase P1; the schema step bundles no rows. The version is the next free world step (the integrator renumbers this one constant).
/// </summary>
public sealed class PlayerbotGoalDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 59;

    public const string Table = "playerbot_goal";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<PlayerbotGoalRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.GoalId);
            entity.Property(r => r.GoalId).HasColumnName("goal_id").ValueGeneratedNever();
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            entity.Property(r => r.BasePriority).HasColumnName("base_priority");
            entity.Property(r => r.Enabled).HasColumnName("enabled");
            entity.Property(r => r.Driver).HasColumnName("driver");
            entity.Property(r => r.DriverParam1).HasColumnName("driver_param1");
            entity.Property(r => r.DriverParam2).HasColumnName("driver_param2");
            entity.Property(r => r.DriverParam3).HasColumnName("driver_param3");
            entity.Property(r => r.DriverParam4).HasColumnName("driver_param4");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255);
        });
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

/// <summary>EF Core implementation of <see cref="IRuleContentStore"/>: every rule table plus <c>playerbot_goal</c>, in key order.</summary>
public sealed class EfRuleContentStore(WorldDbContext db) : IRuleContentStore
{
    public async Task<RuleContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<RuleRecord> rules = await db.Set<RuleRow>().AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new RuleRecord
            {
                Id = r.Id, Host = r.Host, OwnerKind = r.OwnerKind, OwnerId = r.OwnerId, EventType = r.EventType,
                EventParam1 = r.EventParam1, EventParam2 = r.EventParam2, EventParam3 = r.EventParam3, EventParam4 = r.EventParam4,
                PhaseMask = r.PhaseMask, FilterId = r.FilterId, ChancePct = r.ChancePct, ConditionId = r.ConditionId,
                CooldownMinMs = r.CooldownMinMs, CooldownMaxMs = r.CooldownMaxMs, Priority = r.Priority, Flags = r.Flags,
                Enabled = r.Enabled != 0, Comment = r.Comment,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RuleActionRow> actions = await db.Set<RuleActionRow>().AsNoTracking().OrderBy(r => r.RuleId).ThenBy(r => r.Seq)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RuleListRecord> lists = await db.Set<RuleListRow>().AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new RuleListRecord { Id = r.Id, Comment = r.Comment })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RuleListActionRow> listActions = await db.Set<RuleListActionRow>().AsNoTracking().OrderBy(r => r.ListId).ThenBy(r => r.Seq)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RuleSubjectFilterRecord> filters = await db.Set<RuleSubjectFilterRow>().AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new RuleSubjectFilterRecord
            {
                Id = r.Id, ClassMask = r.ClassMask, RaceMask = r.RaceMask, LevelMin = r.LevelMin, LevelMax = r.LevelMax, Team = r.Team,
                MapId = r.MapId, ZoneId = r.ZoneId, AreaId = r.AreaId, SubjectFlags = r.SubjectFlags, SharePct = r.SharePct,
                ShareSeed = r.ShareSeed, Comment = r.Comment,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RuleTextRecord> texts = await db.Set<RuleTextRow>().AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new RuleTextRecord
            {
                Id = r.Id, ContentDefault = r.ContentDefault, Sound = r.Sound, Type = r.Type, Language = r.Language, Emote = r.Emote,
                BroadcastTextId = r.BroadcastTextId, Comment = r.Comment,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<PlayerbotGoalRecord> goals = await db.Set<PlayerbotGoalRow>().AsNoTracking().OrderBy(r => r.GoalId)
            .Select(r => new PlayerbotGoalRecord
            {
                GoalId = r.GoalId, Name = r.Name, BasePriority = r.BasePriority, Enabled = r.Enabled != 0, Driver = r.Driver,
                DriverParam1 = r.DriverParam1, DriverParam2 = r.DriverParam2, DriverParam3 = r.DriverParam3, DriverParam4 = r.DriverParam4,
                Comment = r.Comment,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new RuleContent
        {
            Rules = rules,
            Actions = [.. actions.Select(r => ToRecord(r, r.RuleId))],
            Lists = lists,
            ListActions = [.. listActions.Select(r => ToRecord(r, r.ListId))],
            Filters = filters,
            Texts = texts,
            Goals = goals,
        };
    }

    private static RuleActionRecord ToRecord(RuleActionRowBase r, int owner) => new()
    {
        OwnerId = owner, Seq = r.Seq, ActionType = r.ActionType,
        Param1 = r.Param1, Param2 = r.Param2, Param3 = r.Param3, Param4 = r.Param4, Param5 = r.Param5, Param6 = r.Param6,
        TargetType = r.TargetType, TargetParam1 = r.TargetParam1, TargetParam2 = r.TargetParam2, TargetParam3 = r.TargetParam3,
        DelayMs = r.DelayMs, Flags = r.Flags,
    };
}
