using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Reputation;

/// <summary>
/// <c>creature_onkill_reputation</c> (cmangos/vmangos): the reputation a creature's kill grants,
/// up to two factions. Column meanings follow vmangos <c>ObjectMgr::LoadReputationOnKill</c>
/// (src/game/ObjectMgr.cpp:8894-8965) and cmangos classic-db. Rows are read by column name:
/// vmangos selects <c>IsTeamAward</c> before <c>MaxStanding</c> and classic-db stores them the
/// other way round.
/// </summary>
public sealed class CreatureOnKillReputationRow
{
    public uint CreatureId { get; set; }

    public uint RewOnKillRepFaction1 { get; set; }

    public uint RewOnKillRepFaction2 { get; set; }

    public byte MaxStanding1 { get; set; }

    public bool IsTeamAward1 { get; set; }

    public int RewOnKillRepValue1 { get; set; }

    public byte MaxStanding2 { get; set; }

    public bool IsTeamAward2 { get; set; }

    public int RewOnKillRepValue2 { get; set; }

    public bool TeamDependent { get; set; }
}

/// <summary>
/// World schema step for <c>creature_onkill_reputation</c>. Allocated as the next free world
/// version after the quest reputation columns (10); the integrator renumbers this one constant
/// when other world steps merge first (tests refer to it, never to a literal). The module also
/// registers <see cref="EfReputationOnKillSource"/>, the <see cref="IReputationOnKillSource"/>
/// the reputation feature resolves from DI (nothing registered it before). No cleanup
/// registration: the world schema holds no per-character rows.
/// </summary>
public sealed class CreatureOnKillReputationWorldModule : IDataModule
{
    /// <summary>The world schema version of this step.</summary>
    public const int Version = 14;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("creature_onkill_reputation")];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatureOnKillReputationRow>(entity =>
        {
            entity.ToTable("creature_onkill_reputation");
            entity.HasKey(r => r.CreatureId);
            entity.Property(r => r.CreatureId).ValueGeneratedNever();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IReputationOnKillSource, EfReputationOnKillSource>();
}

/// <summary>Reads the kill-reputation rows from the world database (once, at startup).</summary>
public sealed class EfReputationOnKillSource(WorldDbContext db) : IReputationOnKillSource
{
    public async Task<IReadOnlyList<ReputationOnKillEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<CreatureOnKillReputationRow> rows = await db.Set<CreatureOnKillReputationRow>().AsNoTracking()
            .OrderBy(r => r.CreatureId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new ReputationOnKillEntry(
            r.CreatureId, r.RewOnKillRepFaction1, r.RewOnKillRepFaction2,
            r.MaxStanding1, r.IsTeamAward1, r.RewOnKillRepValue1,
            r.MaxStanding2, r.IsTeamAward2, r.RewOnKillRepValue2, r.TeamDependent))];
    }
}
