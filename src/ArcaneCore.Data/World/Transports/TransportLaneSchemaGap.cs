using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.World.Transports;

/// <summary>
/// Empty world schema steps 38 to 44: the wave-2 plan gives the transports lane world 45 (<see cref="TransportWorldDataModule"/>)
/// and the numbers below it to other lanes, but <see cref="DataModules.Compose"/> requires contiguous versions, so this lane holds
/// the gap open with steps that change nothing (the pattern of the proc-engine lane's WorldSchemaLaneGap).
/// INTEGRATOR: delete each placeholder whose number a merged lane really uses (Compose reports "claimed twice" until you do); keep the
/// ones nobody claimed. An empty step only advances the version table, so a database that ran it is upgraded correctly by a real
/// step only if the real step is renumbered above it; never ship a build with these placeholders to a live realm.
/// </summary>
public abstract class TransportLaneWorldGapStep(int version) : IDataModule
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

/// <summary>World step 38 held open for the lane that owns it (see <see cref="TransportLaneWorldGapStep"/>).</summary>
public sealed class TransportLaneWorldGap38() : TransportLaneWorldGapStep(Version)
{
    public const int Version = 38;
}

/// <summary>World step 39 held open for the lane that owns it (see <see cref="TransportLaneWorldGapStep"/>).</summary>
public sealed class TransportLaneWorldGap39() : TransportLaneWorldGapStep(Version)
{
    public const int Version = 39;
}

/// <summary>World step 40 held open for the lane that owns it (see <see cref="TransportLaneWorldGapStep"/>).</summary>
public sealed class TransportLaneWorldGap40() : TransportLaneWorldGapStep(Version)
{
    public const int Version = 40;
}

/// <summary>World step 41 held open for the lane that owns it (see <see cref="TransportLaneWorldGapStep"/>).</summary>
public sealed class TransportLaneWorldGap41() : TransportLaneWorldGapStep(Version)
{
    public const int Version = 41;
}

/// <summary>World step 42 held open for the lane that owns it (see <see cref="TransportLaneWorldGapStep"/>).</summary>
public sealed class TransportLaneWorldGap42() : TransportLaneWorldGapStep(Version)
{
    public const int Version = 42;
}

/// <summary>World step 43 held open for the lane that owns it (see <see cref="TransportLaneWorldGapStep"/>).</summary>
public sealed class TransportLaneWorldGap43() : TransportLaneWorldGapStep(Version)
{
    public const int Version = 43;
}

/// <summary>World step 44 held open for the lane that owns it (see <see cref="TransportLaneWorldGapStep"/>).</summary>
public sealed class TransportLaneWorldGap44() : TransportLaneWorldGapStep(Version)
{
    public const int Version = 44;
}
