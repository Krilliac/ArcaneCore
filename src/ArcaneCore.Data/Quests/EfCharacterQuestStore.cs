using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Quests;

/// <summary>EF Core implementation of <see cref="ICharacterQuestStore"/> (characters schema v3).</summary>
public sealed class EfCharacterQuestStore(CharacterDbContext db) : ICharacterQuestStore
{
    /// <summary>vmangos TaxiMaskSize (8 words cover node ids 1..256).</summary>
    public const int TaxiMaskSize = 8;

    public async Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        List<CharacterQuestStatus> quests = await db.Set<CharacterQuestStatusRow>().AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Quest)
            .Select(r => new CharacterQuestStatus(r.CharacterId, r.Quest, r.Status, r.Rewarded, r.Explored, r.Timer,
                r.MobCount1, r.MobCount2, r.MobCount3, r.MobCount4, r.ItemCount1, r.ItemCount2, r.ItemCount3, r.ItemCount4, r.RewardChoice))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        CharacterTaxiRow? taxi = await db.Set<CharacterTaxiRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        uint[] mask = taxi is null ? [] : [taxi.Mask0, taxi.Mask1, taxi.Mask2, taxi.Mask3, taxi.Mask4, taxi.Mask5, taxi.Mask6, taxi.Mask7];
        return new CharacterQuestData(quests, mask);
    }

    public async Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts, CancellationToken cancellationToken = default)
    {
        if (!await CharacterExistsAsync(characterId, cancellationToken).ConfigureAwait(false))
        {
            return; // deleted while the save was queued (as EfCharacterStore.SaveStateAsync)
        }

        DbSet<CharacterQuestStatusRow> set = db.Set<CharacterQuestStatusRow>();
        // A List, not an array: C# 14 binds array.Contains to the span overload, which EF cannot translate.
        List<uint> touched = upserts.Select(u => u.Quest).Distinct().ToList();
        Dictionary<uint, CharacterQuestStatusRow> existing = await set
            .Where(r => r.CharacterId == characterId && touched.Contains(r.Quest))
            .ToDictionaryAsync(r => r.Quest, cancellationToken).ConfigureAwait(false);

        foreach (CharacterQuestStatus status in upserts)
        {
            // Rewards are permanent history: progress that never saw the reward (Rewarded false)
            // cannot reopen it. A repeatable taken again after its reward carries Rewarded true.
            if (existing.TryGetValue(status.Quest, out CharacterQuestStatusRow? rewarded) && rewarded.Rewarded && !status.Rewarded)
            {
                continue;
            }

            if (!existing.TryGetValue(status.Quest, out CharacterQuestStatusRow? row))
            {
                row = new CharacterQuestStatusRow { CharacterId = characterId, Quest = status.Quest };
                set.Add(row);
                existing[status.Quest] = row;
            }

            row.Status = status.Status;
            row.Rewarded = status.Rewarded;
            row.Explored = status.Explored;
            row.Timer = status.Timer;
            row.MobCount1 = status.MobCount1;
            row.MobCount2 = status.MobCount2;
            row.MobCount3 = status.MobCount3;
            row.MobCount4 = status.MobCount4;
            row.ItemCount1 = status.ItemCount1;
            row.ItemCount2 = status.ItemCount2;
            row.ItemCount3 = status.ItemCount3;
            row.ItemCount4 = status.ItemCount4;
            row.RewardChoice = status.RewardChoice;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask, CancellationToken cancellationToken = default)
    {
        if (!await CharacterExistsAsync(characterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        DbSet<CharacterTaxiRow> set = db.Set<CharacterTaxiRow>();
        CharacterTaxiRow? row = await set.FirstOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new CharacterTaxiRow { CharacterId = characterId };
            set.Add(row);
        }

        uint Word(int i) => i < mask.Count ? mask[i] : 0;
        row.Mask0 = Word(0);
        row.Mask1 = Word(1);
        row.Mask2 = Word(2);
        row.Mask3 = Word(3);
        row.Mask4 = Word(4);
        row.Mask5 = Word(5);
        row.Mask6 = Word(6);
        row.Mask7 = Word(7);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private Task<bool> CharacterExistsAsync(int characterId, CancellationToken cancellationToken)
        => db.Set<CharacterRecord>().AsNoTracking().AnyAsync(c => c.Id == characterId, cancellationToken);
}
