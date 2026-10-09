using System.Data;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>Persistent game_tele edits (vmangos ObjectMgr::AddGameTele/DeleteGameTele, ObjectMgr.cpp:10555-10604).</summary>
public sealed class EfGameTeleStore(WorldDbContext db) : IGameTeleStore
{
    public async Task<GameTele?> AddAsync(GameTele location, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        List<GameTeleRow> rows = await db.Set<GameTeleRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Any(row => string.Equals(row.Name, location.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        uint id = rows.Count == 0 ? 1 : checked(rows.Max(row => row.Id) + 1);
        db.Set<GameTeleRow>().Add(new GameTeleRow
        {
            Id = id, PositionX = location.X, PositionY = location.Y, PositionZ = location.Z,
            Orientation = location.Orientation, Map = location.MapId, Name = location.Name,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return location with { Id = id };
    }

    public async Task<GameTele?> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        List<GameTeleRow> rows = await db.Set<GameTeleRow>().ToListAsync(cancellationToken).ConfigureAwait(false);
        GameTeleRow? row = rows.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return null;
        }

        db.Set<GameTeleRow>().Remove(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GameTele(row.Id, row.PositionX, row.PositionY, row.PositionZ, row.Orientation, row.Map, row.Name);
    }
}
