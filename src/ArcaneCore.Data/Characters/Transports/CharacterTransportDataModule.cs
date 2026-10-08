using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Transports;

/// <summary>
/// The ship a character was saved on, as vmangos keeps it on the character row: <c>characters.transport_guid</c> and
/// <c>transport_x</c>, <c>transport_y</c>, <c>transport_z</c>, <c>transport_o</c> (Player::SaveToDB, Player.cpp:16427-16434; read
/// back by Player::LoadFromDB, Player.cpp:14733, 14794-14838, which puts the character back on the ship or at its bind point).
/// The columns are additive and default to 0 (on land). The character store writes them with every character snapshot.
/// <para>
/// <b>Characters version 41</b>: the transports lane's reserved number in the wave-2 plan. Named once here; tests read
/// <see cref="Version"/>.
/// </para>
/// </summary>
public sealed class CharacterTransportDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 41;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("characters", "transport_guid"),
        new AddColumnChange("characters", "transport_x"),
        new AddColumnChange("characters", "transport_y"),
        new AddColumnChange("characters", "transport_z"),
        new AddColumnChange("characters", "transport_o"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterRecord>(entity =>
        {
            entity.Property(c => c.TransportGuid).HasColumnName("transport_guid");
            entity.Property(c => c.TransportX).HasColumnName("transport_x");
            entity.Property(c => c.TransportY).HasColumnName("transport_y");
            entity.Property(c => c.TransportZ).HasColumnName("transport_z");
            entity.Property(c => c.TransportOrientation).HasColumnName("transport_o");
        });
    }

    public void AddServices(IServiceCollection services)
    {
        // The existing character store saves these columns with the position in one character snapshot.
    }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        // The core deletion removes the characters row, and these columns with it, in the same transaction.
        return Task.CompletedTask;
    }
}

/// <summary>
/// Empty characters schema steps 35 to 40: the wave-2 plan gives the transports lane characters 41 and the numbers below it to
/// other lanes, but <see cref="DataModules.Compose"/> requires contiguous versions, so this lane holds the gap open with steps that
/// change nothing. INTEGRATOR: delete each placeholder whose number a merged lane really uses (Compose reports "claimed twice" until
/// you do); keep the ones nobody claimed. Never ship a build with these placeholders to a live realm (see TransportLaneWorldGapStep).
/// </summary>
public abstract class TransportLaneCharactersGapStep(int version) : IDataModule
{
    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion { get; } = version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }
}

/// <summary>Characters step 35 held open for the lane that owns it (see <see cref="TransportLaneCharactersGapStep"/>).</summary>
public sealed class TransportLaneCharactersGap35() : TransportLaneCharactersGapStep(Version)
{
    public const int Version = 35;
}

/// <summary>Characters step 36 held open for the lane that owns it (see <see cref="TransportLaneCharactersGapStep"/>).</summary>
public sealed class TransportLaneCharactersGap36() : TransportLaneCharactersGapStep(Version)
{
    public const int Version = 36;
}

/// <summary>Characters step 37 held open for the lane that owns it (see <see cref="TransportLaneCharactersGapStep"/>).</summary>
public sealed class TransportLaneCharactersGap37() : TransportLaneCharactersGapStep(Version)
{
    public const int Version = 37;
}

/// <summary>Characters step 38 held open for the lane that owns it (see <see cref="TransportLaneCharactersGapStep"/>).</summary>
public sealed class TransportLaneCharactersGap38() : TransportLaneCharactersGapStep(Version)
{
    public const int Version = 38;
}

/// <summary>Characters step 39 held open for the lane that owns it (see <see cref="TransportLaneCharactersGapStep"/>).</summary>
public sealed class TransportLaneCharactersGap39() : TransportLaneCharactersGapStep(Version)
{
    public const int Version = 39;
}

/// <summary>Characters step 40 held open for the lane that owns it (see <see cref="TransportLaneCharactersGapStep"/>).</summary>
public sealed class TransportLaneCharactersGap40() : TransportLaneCharactersGapStep(Version)
{
    public const int Version = 40;
}
