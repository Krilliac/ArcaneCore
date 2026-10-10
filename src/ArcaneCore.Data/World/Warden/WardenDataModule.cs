using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Warden;

/// <summary>
/// A <c>warden_checks</c> row: one Warden scan, in vmangos's <c>warden_scans</c> layout (vmangos WardenScanMgr::LoadFromDB). The meaning
/// of <see cref="Str"/>, <see cref="Data"/> and <see cref="Result"/> depends on <see cref="Type"/>, the vmangos WindowsScanType:
/// 0 memory (Str module, Result expected hex), 1 module by name (Str name, Result "1" wanted), 2 / 3 page A / B (Data pattern hex,
/// Result wanted), 4 MPQ file hash (Str path, Result SHA-1 hex or empty for "must be absent"), 5 Lua (Str variable, Data the value a
/// clean client reports, or empty with Result wanted), 6 API hook (not run), 7 driver (Str name, Data path, Result wanted), 8 timing.
/// </summary>
public sealed class WardenCheckRow
{
    public uint Id { get; set; }

    public int Type { get; set; }

    public string? Str { get; set; }

    public string? Data { get; set; }

    public uint Address { get; set; }

    public int Length { get; set; }

    public string Result { get; set; } = string.Empty;

    /// <summary>vmangos ScanFlags (kept for reference; ArcaneCore filters by build).</summary>
    public uint Flags { get; set; }

    /// <summary>-1 for the configured action, else 0 log, 1 kick, 2 ban (vmangos WardenActions).</summary>
    public int Penalty { get; set; } = -1;

    public int BuildMin { get; set; } = 5875;

    public int BuildMax { get; set; } = 5875;

    public string Comment { get; set; } = string.Empty;
}

/// <summary>
/// The Warden world-schema step: the <c>warden_checks</c> table (vmangos <c>warden_scans</c>).
/// <para><b>World version 49</b>: claimed by wave 18 (Warden; 47 is chat_word_filter, 48 belongs to PR #72 and is held by <see cref="ReservedWorldSchema48"/> here). The number is named once here; tests read <see cref="Version"/>.</para>
/// </summary>
public sealed class WardenDataModule : IDataModule
{
    public const int Version = 49;

    public const string Table = "warden_checks";

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<WardenCheckRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Str).HasMaxLength(255);
            entity.Property(r => r.Data).HasMaxLength(512);
            entity.Property(r => r.Result).HasMaxLength(512);
            entity.Property(r => r.Comment).HasMaxLength(255);
        });

    public void AddServices(IServiceCollection services)
    {
    }
}

/// <summary>Reads <c>warden_checks</c>.</summary>
public static class WardenCheckStore
{
    public static Task<List<WardenCheckRow>> LoadAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Set<WardenCheckRow>().AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken);
    }
}

/// <summary>World schema 48, owned by PR #72. Delete this placeholder when that module merges (Compose drops it on its own).</summary>
public sealed class ReservedWorldSchema48 : ReservedSchemaGap
{
    public override DatabaseComponent Component => DatabaseComponent.World;

    public override int SchemaVersion => 48;
}
