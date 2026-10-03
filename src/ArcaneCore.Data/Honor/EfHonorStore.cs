using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Honor;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Honor;

/// <summary>
/// EF Core implementation of <see cref="IHonorStore"/> over the characters database. Every multi-statement
/// write is one transaction made only of DML, so it behaves the same on SQLite, MariaDB (InnoDB) and
/// PostgreSQL; no advisory lock is taken (Npgsql pooling hands back the same physical connection, which would
/// make such a lock re-entrant rather than exclusive).
/// </summary>
/// <param name="db">The scoped characters context.</param>
/// <param name="beforeCommit">
/// Test seam: awaited after the last statement of <see cref="ApplyMaintenanceAsync"/> and before it commits, so
/// a test can fail the transaction at its end. Null in production.
/// </param>
public sealed class EfHonorStore(CharacterDbContext db, Func<CancellationToken, Task>? beforeCommit = null) : IHonorStore
{
    // Keeps IN (...) lists well below every provider's parameter limit.
    private const int ChunkSize = 500;

    public async Task<CharacterHonorData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        CharacterHonorRow? state = await db.Set<CharacterHonorRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        List<HonorCpRecord> cp = await db.Set<HonorCpRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Id)
            .Select(r => new HonorCpRecord(r.VictimType, r.VictimId, r.Cp, r.Date, r.Type))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CharacterHonorData(state is null ? CharacterHonorState.Empty : ToState(state), cp);
    }

    public async Task SaveStateAsync(int characterId, CharacterHonorState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!await CharacterExistsAsync(characterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            CharacterHonorRow? row = await db.Set<CharacterHonorRow>()
                .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new CharacterHonorRow { CharacterId = characterId };
                db.Set<CharacterHonorRow>().Add(row);
            }

            Apply(row, state);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task AppendCpAsync(int characterId, IReadOnlyList<HonorCpRecord> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0 || !await CharacterExistsAsync(characterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (HonorCpRecord row in rows)
            {
                db.Set<HonorCpRow>().Add(new HonorCpRow
                {
                    CharacterId = characterId,
                    VictimType = row.VictimType,
                    VictimId = row.VictimId,
                    Cp = HonorRounding.OneDecimal(row.Cp),
                    Date = row.Date,
                    Type = row.Type,
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task ResetAsync(int characterId, CancellationToken cancellationToken = default)
    {
        // HonorMgr::Reset clears the honor numbers but not the character flags (PvP, City Protector).
        await db.Set<HonorCpRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterHonorRow>().Where(r => r.CharacterId == characterId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.RankPoints, 0f)
                    .SetProperty(r => r.HighestRank, (byte)0)
                    .SetProperty(r => r.Standing, 0u)
                    .SetProperty(r => r.LastWeekHk, 0u)
                    .SetProperty(r => r.LastWeekCp, 0f)
                    .SetProperty(r => r.StoredHk, 0)
                    .SetProperty(r => r.StoredDk, 0),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await db.Set<HonorCpRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterHonorRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await db.Set<HonorCpRow>()
            .Where(r => r.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterHonorRow>()
            .Where(r => r.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HonorMaintenanceState?> GetMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        HonorMaintenanceRow? row = await db.Set<HonorMaintenanceRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == HonorMaintenanceRow.SingletonId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new HonorMaintenanceState(row.LastDay, row.NextDay, row.Marker);
    }

    public async Task SaveMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            await StageMaintenanceAsync(state, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<HonorWeeklyScore>> ListWeeklyScoresAsync(uint weekBeginDay, uint weekEndDay, CancellationToken cancellationToken = default)
    {
        // HonorMgr.cpp:62-102: kills are counted per type, contribution points exclude dishonorable rows, and a
        // character with rank points above zero is included even without rows. Aggregated here, over the rows,
        // so the float arithmetic is identical on every provider (the original SQL SUMs in the engine).
        var weekRows = await db.Set<HonorCpRow>().AsNoTracking()
            .Where(r => r.Date >= weekBeginDay && r.Date <= weekEndDay)
            .Select(r => new { r.CharacterId, r.Type, r.Cp })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<int, (uint Hk, uint Dk, double Cp)> sums = [];
        foreach (var row in weekRows)
        {
            (uint hk, uint dk, double cp) = sums.GetValueOrDefault(row.CharacterId);
            if (row.Type == (byte)HonorKind.Honorable)
            {
                hk++;
            }
            else if (row.Type == (byte)HonorKind.Dishonorable)
            {
                dk++;
            }

            if (row.Type != (byte)HonorKind.Dishonorable)
            {
                cp += row.Cp;
            }

            sums[row.CharacterId] = (hk, dk, cp);
        }

        Dictionary<int, CharacterHonorRow> states = (await db.Set<CharacterHonorRow>().AsNoTracking()
            .Where(r => r.RankPoints > 0f)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.CharacterId);
        foreach (int[] chunk in sums.Keys.Where(k => !states.ContainsKey(k)).Chunk(ChunkSize))
        {
            List<int> part = [.. chunk];
            foreach (CharacterHonorRow row in await db.Set<CharacterHonorRow>().AsNoTracking()
                .Where(r => part.Contains(r.CharacterId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                states[row.CharacterId] = row;
            }
        }

        HashSet<int> ids = [.. sums.Keys];
        foreach (CharacterHonorRow state in states.Values.Where(s => s.RankPoints > 0f))
        {
            ids.Add(state.CharacterId);
        }

        List<HonorWeeklyScore> scores = [];
        foreach (int[] chunk in ids.OrderBy(i => i).Chunk(ChunkSize))
        {
            List<int> part = [.. chunk];
            var characters = await db.Characters.AsNoTracking()
                .Where(c => part.Contains(c.Id))
                .Select(c => new { c.Id, c.Level, c.AccountId, c.Race })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var c in characters)
            {
                (uint hk, uint dk, double cp) = sums.GetValueOrDefault(c.Id);
                states.TryGetValue(c.Id, out CharacterHonorRow? state);
                scores.Add(new HonorWeeklyScore(c.Id, c.Level, c.AccountId, c.Race, state?.RankPoints ?? 0f, state?.HighestRank ?? 0, hk, dk, (float)cp));
            }
        }

        scores.Sort((a, b) => a.CharacterId.CompareTo(b.CharacterId));
        return scores;
    }

    public async Task ApplyMaintenanceAsync(HonorMaintenanceBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // FlushRankPoints starts by clearing every standing (HonorMgr.cpp:243).
            await db.Set<CharacterHonorRow>().Where(r => r.Standing > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Standing, 0u), cancellationToken).ConfigureAwait(false);

            // Last update per character wins; the City Protector set may name characters without a state row.
            Dictionary<int, HonorRankUpdate> latest = [];
            foreach (HonorRankUpdate update in batch.Updates)
            {
                latest[update.CharacterId] = update;
            }

            HashSet<int> protectors = batch.CityProtectors is null ? [] : [.. batch.CityProtectors];
            List<int> wanted = [.. latest.Keys.Union(protectors)];
            Dictionary<int, CharacterHonorRow> rows = [];
            foreach (int[] chunk in wanted.Chunk(ChunkSize))
            {
                List<int> part = [.. chunk];
                foreach (CharacterHonorRow row in await db.Set<CharacterHonorRow>().Where(r => part.Contains(r.CharacterId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows[row.CharacterId] = row;
                }
            }

            foreach (int id in wanted)
            {
                if (!rows.TryGetValue(id, out CharacterHonorRow? row))
                {
                    row = new CharacterHonorRow { CharacterId = id };
                    db.Set<CharacterHonorRow>().Add(row);
                    rows[id] = row;
                }

                if (latest.TryGetValue(id, out HonorRankUpdate? update))
                {
                    row.RankPoints = HonorRounding.OneDecimal(update.RankPoints);
                    row.Standing = update.Standing;
                    row.HighestRank = update.HighestRank;
                    row.LastWeekHk = update.WeekHk;
                    row.LastWeekCp = HonorRounding.OneDecimal(update.WeekCp);
                    row.StoredHk += (int)update.WeekHk;
                    row.StoredDk += (int)update.WeekDk;
                }
            }

            if (batch.CityProtectors is not null)
            {
                foreach (CharacterHonorRow row in rows.Values)
                {
                    row.CityProtector = protectors.Contains(row.CharacterId);
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (batch.CityProtectors is not null)
            {
                // Every other character loses the flag (SetCityRanks clears it for all first).
                List<int> keep = [.. protectors];
                await db.Set<CharacterHonorRow>().Where(r => r.CityProtector && !keep.Contains(r.CharacterId))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.CityProtector, false), cancellationToken).ConfigureAwait(false);
            }

            // "Not includes weekend day, for correct view in honor tab for group Yesterday" (HonorMgr.cpp:266-267).
            await db.Set<HonorCpRow>().Where(r => r.Date < batch.DeleteCpBefore)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await StageMaintenanceAsync(batch.NewState, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (beforeCommit is not null)
            {
                await beforeCommit(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task StageMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken)
    {
        HonorMaintenanceRow? row = await db.Set<HonorMaintenanceRow>()
            .FirstOrDefaultAsync(r => r.Id == HonorMaintenanceRow.SingletonId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new HonorMaintenanceRow { Id = HonorMaintenanceRow.SingletonId };
            db.Set<HonorMaintenanceRow>().Add(row);
        }

        row.LastDay = state.LastDay;
        row.NextDay = state.NextDay;
        row.Marker = state.Marker;
    }

    private Task<bool> CharacterExistsAsync(int characterId, CancellationToken cancellationToken)
        => db.Characters.AnyAsync(c => c.Id == characterId, cancellationToken);

    private static CharacterHonorState ToState(CharacterHonorRow r)
        => new(r.RankPoints, r.HighestRank, r.Standing, r.LastWeekHk, r.LastWeekCp, r.StoredHk, r.StoredDk, r.PvpFlags, r.CityProtector);

    private static void Apply(CharacterHonorRow row, CharacterHonorState s)
    {
        row.RankPoints = HonorRounding.OneDecimal(s.RankPoints);
        row.HighestRank = s.HighestRank;
        row.Standing = s.Standing;
        row.LastWeekHk = s.LastWeekHk;
        row.LastWeekCp = HonorRounding.OneDecimal(s.LastWeekCp);
        row.StoredHk = s.StoredHk;
        row.StoredDk = s.StoredDk;
        row.PvpFlags = s.PvpFlags;
        row.CityProtector = s.CityProtector;
    }
}
