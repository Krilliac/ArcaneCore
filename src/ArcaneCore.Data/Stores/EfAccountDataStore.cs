using ArcaneCore.Data.Characters;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>EF Core implementation of <see cref="IAccountDataStore"/> (characters database).</summary>
public sealed class EfAccountDataStore(CharacterDbContext db) : IAccountDataStore
{
    public async Task<AccountSettings> GetAsync(int accountId, CancellationToken cancellationToken = default)
    {
        var settings = new AccountSettings();
        List<AccountDataRow> rows = await db.AccountData.AsNoTracking()
            .Where(r => r.AccountId == accountId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (AccountDataRow row in rows.Where(r => r.Type < AccountSettings.DataTypeCount))
        {
            settings.Data[row.Type] = new AccountDataEntry(row.Time, row.Data);
        }

        AccountTutorialRow? tutorial = await db.AccountTutorials.AsNoTracking()
            .FirstOrDefaultAsync(r => r.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (tutorial is not null)
        {
            uint[] t = settings.Tutorials;
            (t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7]) =
                (tutorial.Tut0, tutorial.Tut1, tutorial.Tut2, tutorial.Tut3, tutorial.Tut4, tutorial.Tut5, tutorial.Tut6, tutorial.Tut7);
        }

        return settings;
    }

    public async Task SaveDataAsync(int accountId, int type, AccountDataEntry entry, CancellationToken cancellationToken = default)
    {
        AccountDataRow? row = await db.AccountData
            .FirstOrDefaultAsync(r => r.AccountId == accountId && r.Type == type, cancellationToken)
            .ConfigureAwait(false);

        if (entry.Data.Length == 0)
        {
            if (row is not null)
            {
                db.AccountData.Remove(row);
            }
        }
        else if (row is null)
        {
            db.AccountData.Add(new AccountDataRow { AccountId = accountId, Type = (byte)type, Time = entry.Time, Data = entry.Data });
        }
        else
        {
            row.Time = entry.Time;
            row.Data = entry.Data;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task SaveTutorialsAsync(int accountId, IReadOnlyList<uint> tutorials, CancellationToken cancellationToken = default)
    {
        if (tutorials.Count != AccountSettings.TutorialWordCount)
        {
            throw new ArgumentException($"expected {AccountSettings.TutorialWordCount} tutorial words", nameof(tutorials));
        }

        AccountTutorialRow? row = await db.AccountTutorials
            .FirstOrDefaultAsync(r => r.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new AccountTutorialRow { AccountId = accountId };
            db.AccountTutorials.Add(row);
        }

        (row.Tut0, row.Tut1, row.Tut2, row.Tut3, row.Tut4, row.Tut5, row.Tut6, row.Tut7) =
            (tutorials[0], tutorials[1], tutorials[2], tutorials[3], tutorials[4], tutorials[5], tutorials[6], tutorials[7]);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }
}
