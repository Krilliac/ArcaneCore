using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The quest journal and known flight paths as the daemon saves and reloads them, across
/// the existing SQLite, MariaDB and PostgreSQL matrix. Every operation uses a fresh context
/// so progress must survive in the database rather than in EF's change tracker.
/// </summary>
public sealed class QuestStoreTests : IAsyncLifetime
{
    // vmangos QuestDef.h QuestStatus, also src/ArcaneCore.Game/Quests/QuestDefines.cs.
    // Data tests do not reference the Game assembly.
    private const byte Complete = 1;
    private const byte Incomplete = 3;
    private const byte Failed = 5;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Journal_ReloadsMultipleStatuses_WithAllObjectiveCountsRewardAndTimer(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterRecord[] characters) =
            await CreateAsync(provider, "Questing", "Unstarted");
        int id = characters[0].Id;
        CharacterQuestStatus[] journal =
        [
            // Unix end time beyond signed 32-bit seconds: persistence must not truncate it.
            new(id, 903, Incomplete, false, false, 2_147_483_700L, 1, 2, 3, 4, 11, 12, 13, 14, 0),
            new(id, 901, Complete, true, true, 0, 5, 6, 7, 8, 15, 16, 17, 18, 5),
            new(id, 902, Failed, false, true, 0, 9, 10, 11, 12, 19, 20, 21, 22, 2),
        ];

        await WriteAsync(connection, store => store.SaveQuestsAsync(id, journal));

        CharacterQuestData reloaded = await LoadAsync(connection, id);
        Assert.Equal(journal.OrderBy(q => q.Quest), reloaded.Quests);
        Assert.Empty(reloaded.TaxiMask);
        CharacterQuestData unstarted = await LoadAsync(connection, characters[1].Id);
        Assert.Empty(unstarted.Quests);
        Assert.Empty(unstarted.TaxiMask);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TimedFailureDelta_UpdatesOnlyTouchedQuests_AndPreservesOtherCharacters(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterRecord[] characters) =
            await CreateAsync(provider, "Timed", "Observer");
        int id = characters[0].Id;
        int otherId = characters[1].Id;
        var timed = new CharacterQuestStatus(id, 910, Incomplete, false, false, 1_900_000_123,
            1, 3, 5, 7, 2, 4, 6, 8, 0);
        var untouched = new CharacterQuestStatus(id, 911, Complete, true, true, 0,
            11, 12, 13, 14, 21, 22, 23, 24, 4);
        CharacterQuestStatus other = timed with { CharacterId = otherId, MobCount1 = 30, ItemCount4 = 40 };
        await WriteAsync(connection, store => store.SaveQuestsAsync(id, [timed, untouched]));
        await WriteAsync(connection, store => store.SaveQuestsAsync(otherId, [other]));

        // The ordered journal sends only changed rows. Failing a timed quest clears its
        // deadline; it must retain earned counters without replacing the whole journal.
        CharacterQuestStatus failed = timed with { Status = Failed, Timer = 0 };
        var accepted = new CharacterQuestStatus(id, 912, Incomplete, false, false, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0);
        await WriteAsync(connection, store => store.SaveQuestsAsync(id, [failed, accepted]));
        await WriteAsync(connection, store => store.SaveQuestsAsync(id, [failed, accepted]));

        Assert.Equal([failed, untouched, accepted], (await LoadAsync(connection, id)).Quests);
        Assert.Equal([other], (await LoadAsync(connection, otherId)).Quests);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(4, await db.Set<CharacterQuestStatusRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TaxiMask_ReloadsAllEightWords_AndReplacementIsIdempotent(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterRecord[] characters) =
            await CreateAsync(provider, "Flyer", "Companion");
        int id = characters[0].Id;
        int otherId = characters[1].Id;
        uint[] original = [1, 0x8000_0000, 4, 0x5555_5555, 0xAAAA_AAAA, 0x4000_0000, 0, uint.MaxValue];
        uint[] otherMask = [0, 2, 0, 8, 0, 32, 0, 128];
        await WriteAsync(connection, store => store.SaveTaxiMaskAsync(id, original));
        await WriteAsync(connection, store => store.SaveTaxiMaskAsync(otherId, otherMask));
        Assert.Equal(original, (await LoadAsync(connection, id)).TaxiMask);

        uint[] replacement = [0x8000_0000, 0, 0x20, 0, 0x100, 0, 0x4000, 1];
        await WriteAsync(connection, store => store.SaveTaxiMaskAsync(id, replacement));
        await WriteAsync(connection, store => store.SaveTaxiMaskAsync(id, replacement));
        Assert.Equal(replacement, (await LoadAsync(connection, id)).TaxiMask);
        Assert.Equal(otherMask, (await LoadAsync(connection, otherId)).TaxiMask);

        // Replacing with an empty set of nodes clears every previously learned bit.
        uint[] cleared = new uint[8];
        await WriteAsync(connection, store => store.SaveTaxiMaskAsync(id, cleared));
        Assert.Equal(cleared, (await LoadAsync(connection, id)).TaxiMask);
        Assert.Equal(otherMask, (await LoadAsync(connection, otherId)).TaxiMask);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(2, await db.Set<CharacterTaxiRow>().CountAsync());
        Assert.Empty(await db.Set<CharacterQuestStatusRow>().ToListAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task QueuedJournalAndTaxiWrites_AfterCharacterDeletion_DoNotCreateRows(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, CharacterRecord[] characters) =
            await CreateAsync(provider, "Departing", "Remaining");
        int deletedId = characters[0].Id;
        int survivingId = characters[1].Id;
        var queued = new CharacterQuestStatus(deletedId, 920, Incomplete, false, false, 1_900_000_999,
            2, 4, 6, 8, 1, 3, 5, 7, 0);
        CharacterQuestStatus surviving = queued with { CharacterId = survivingId, Status = Complete, Timer = 0 };
        uint[] mask = [1, 2, 4, 8, 16, 32, 64, 0x8000_0000];
        await WriteAsync(connection, store => store.SaveQuestsAsync(survivingId, [surviving]));
        await WriteAsync(connection, store => store.SaveTaxiMaskAsync(survivingId, mask));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            ICharacterStore store = new EfCharacterStore(db);
            Assert.True(await store.DeleteAsync(deletedId, characters[0].AccountId));
        }

        // Snapshots were captured while the character existed but reach persistence after
        // deletion. The character's absence makes both pending writes harmless.
        await WriteAsync(connection, store => store.SaveQuestsAsync(deletedId, [queued]));
        await WriteAsync(connection, store => store.SaveTaxiMaskAsync(deletedId, mask));
        CharacterQuestData deleted = await LoadAsync(connection, deletedId);
        Assert.Empty(deleted.Quests);
        Assert.Empty(deleted.TaxiMask);
        CharacterQuestData remaining = await LoadAsync(connection, survivingId);
        Assert.Equal([surviving], remaining.Quests);
        Assert.Equal(mask, remaining.TaxiMask);

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.False(await verify.Characters.AnyAsync(c => c.Id == deletedId));
        Assert.Equal(survivingId, (await verify.Set<CharacterQuestStatusRow>().SingleAsync()).CharacterId);
        Assert.Equal(survivingId, (await verify.Set<CharacterTaxiRow>().SingleAsync()).CharacterId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<(DatabaseConnectionOptions Connection, CharacterRecord[] Characters)> CreateAsync(
        DatabaseProvider provider, params string[] names)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        ICharacterStore store = new EfCharacterStore(db);
        var characters = new List<CharacterRecord>();
        foreach (string name in names)
        {
            characters.Add(await store.CreateAsync(new CharacterRecord
            {
                AccountId = 77,
                Name = name,
                Race = 1,
                Class = 1,
                Level = 10,
            }));
        }

        return (connection, [.. characters]);
    }

    private static async Task WriteAsync(DatabaseConnectionOptions connection, Func<ICharacterQuestStore, Task> write)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        ICharacterQuestStore store = new EfCharacterQuestStore(db);
        await write(store);
    }

    private static async Task<CharacterQuestData> LoadAsync(DatabaseConnectionOptions connection, int characterId)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        ICharacterQuestStore store = new EfCharacterQuestStore(db);
        return await store.LoadAsync(characterId);
    }
}
