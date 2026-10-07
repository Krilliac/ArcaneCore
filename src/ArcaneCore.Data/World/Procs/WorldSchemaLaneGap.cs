using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Procs;

/// <summary>
/// Empty world schema steps 38, 39 and 40: the wave-2 plan reserves those numbers for other lanes and gives the proc-engine lane 41-42, but
/// <see cref="DataModules.Compose"/> requires contiguous versions, so this lane holds the gap open with steps that change nothing.
/// INTEGRATOR: delete each placeholder whose number a merged lane really uses (Compose reports "claimed twice" until you do); keep the
/// ones nobody claimed. An empty step only advances the version table, so a database that ran it is upgraded correctly by a real step
/// only if the real step is renumbered above it; never ship a build with these placeholders to a live realm.
/// </summary>
public abstract class WorldSchemaLaneGapStep(int version) : IDataModule
{
    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion { get; } = version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

/// <summary>World step 38 held open for the lane that owns it (see <see cref="WorldSchemaLaneGapStep"/>).</summary>
public sealed class WorldSchemaLaneGap38() : WorldSchemaLaneGapStep(Version)
{
    public const int Version = 38;
}

/// <summary>World step 39 held open for the lane that owns it (see <see cref="WorldSchemaLaneGapStep"/>).</summary>
public sealed class WorldSchemaLaneGap39() : WorldSchemaLaneGapStep(Version)
{
    public const int Version = 39;
}

/// <summary>World step 40 held open for the lane that owns it (see <see cref="WorldSchemaLaneGapStep"/>).</summary>
public sealed class WorldSchemaLaneGap40() : WorldSchemaLaneGapStep(Version)
{
    public const int Version = 40;
}
