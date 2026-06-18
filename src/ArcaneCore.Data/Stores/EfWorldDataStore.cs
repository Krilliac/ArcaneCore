using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>EF Core implementation of <see cref="IWorldDataStore"/> over the seeded DB tables.</summary>
public sealed class EfWorldDataStore(CharacterDbContext db) : IWorldDataStore
{
    public async Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
    {
        PlayerCreateInfoRow? row = await db.PlayerCreateInfo.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Race == race && r.Class == cls, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new StartPosition(row.MapId, row.ZoneId, row.X, row.Y, row.Z, row.Orientation);
    }

    public async Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
    {
        RaceInfoRow? row = await db.RaceInfo.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Race == race && r.Gender == gender, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new RaceInfo(row.DisplayId, row.FactionTemplate);
    }

    public async Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
    {
        ClassInfoRow? row = await db.ClassInfo.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Class == cls, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new ClassInfo(row.BaseHealth, row.BaseMana, row.PowerType);
    }

    public async Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => await db.PlayerCreateInfo.AsNoTracking()
            .AnyAsync(r => r.Race == race && r.Class == cls, cancellationToken).ConfigureAwait(false);
}
