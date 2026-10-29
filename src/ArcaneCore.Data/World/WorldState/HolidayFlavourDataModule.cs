using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.WorldState;

/// <summary><c>holiday_flavour</c> row (the property names are the columns in PascalCase; the database columns are lower snake case).</summary>
public sealed class HolidayFlavourRow
{
    public uint HolidayId { get; set; }

    public string FlavourKey { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public uint Emote1 { get; set; }

    public uint Emote2 { get; set; }

    public uint Emote3 { get; set; }

    /// <summary>1 bots gather at the festival, 0 they do not (a tinyint, so content files insert plain numbers on every provider).</summary>
    public byte Gather { get; set; } = 1;
}

/// <summary>
/// World schema 62: <c>holiday_flavour</c>, how each client holiday is told in bot speech and emotes (line-template key, name, the crowd's text
/// emotes, whether bots gather), read by the playerbot pack at start-up. Holiday id 0 is the flavour of a holiday the table does not name. The
/// rows ship as content (<c>sql/content/world/&lt;provider&gt;/0002_holiday_flavour.sql</c>); the schema step bundles none. The version is the
/// next free world step (the integrator renumbers this one constant).
/// </summary>
public sealed class HolidayFlavourDataModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 62;

    public const string Table = "holiday_flavour";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<HolidayFlavourRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.HolidayId);
            entity.Property(r => r.HolidayId).HasColumnName("holiday_id").ValueGeneratedNever();
            entity.Property(r => r.FlavourKey).HasColumnName("flavour_key").HasMaxLength(32).IsRequired();
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            entity.Property(r => r.Emote1).HasColumnName("emote1");
            entity.Property(r => r.Emote2).HasColumnName("emote2");
            entity.Property(r => r.Emote3).HasColumnName("emote3");
            entity.Property(r => r.Gather).HasColumnName("gather");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IHolidayFlavourStore, EfHolidayFlavourStore>();
}

/// <summary>EF Core implementation of <see cref="IHolidayFlavourStore"/>.</summary>
public sealed class EfHolidayFlavourStore(WorldDbContext db) : IHolidayFlavourStore
{
    public async Task<IReadOnlyList<HolidayFlavourRecord>> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<HolidayFlavourRow>().AsNoTracking().OrderBy(r => r.HolidayId)
            .Select(r => new HolidayFlavourRecord(r.HolidayId, r.FlavourKey, r.Name, r.Emote1, r.Emote2, r.Emote3, r.Gather != 0))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// The shipped <c>holiday_flavour</c> rows, seeded by <see cref="Content.WorldDbInitializer"/> into an empty table (nothing applies
/// <c>sql/content</c> by itself, and a schema step carries no rows). <c>sql/content/world/&lt;provider&gt;/0002_holiday_flavour.sql</c> holds the
/// same rows for an operator who edits them; a test compares the two row for row.
/// </summary>
public static class HolidayFlavourSeed
{
    public static IReadOnlyList<HolidayFlavourRecord> Rows { get; } =
    [
        new(0, "festival", "", 21, 101, 34, true),
        new(141, "winterveil", "Winter Veil", 21, 5, 0, true),
        new(181, "noblegarden", "Noblegarden", 21, 101, 34, true),
        new(201, "children", "Children's Week", 21, 101, 34, true),
        new(301, "fishing", "Fishing Extravaganza", 21, 101, 34, false),
        new(321, "harvest", "Harvest Festival", 21, 101, 34, true),
        new(324, "hallows", "Hallow's End", 60, 21, 0, true),
        new(327, "lunar", "Lunar Festival", 17, 21, 0, true),
        new(335, "love", "Love is in the Air", 21, 101, 34, true),
        new(341, "midsummer", "Midsummer", 34, 21, 0, true),
        new(374, "darkmoon", "Darkmoon Faire", 21, 5, 60, true),
        new(375, "darkmoon", "Darkmoon Faire", 21, 5, 60, true),
    ];
}
