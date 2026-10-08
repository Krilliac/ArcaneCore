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
/// <b>Characters version 40</b>: reserved as 41 in the wave-2 plan and renumbered down at the 2026-10-07 integration, which
/// closed the unclaimed numbers (docs/integration/wave2-20261007.md). Tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class CharacterTransportDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 40; // reserved as 41 in the wave-2 plan; renumbered down at the 2026-10-07 integration (no gaps)

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
