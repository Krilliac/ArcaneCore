using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.WorldState;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.WorldState;

/// <summary>Explored zones (<see cref="ExploredZonesDataModule"/>) on the SQLite / MariaDB / PostgreSQL matrix.</summary>
public sealed class ExploredZonesStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Text_UsesTheVmangosFormat_AndParsingIsStrict()
    {
        uint[] words = new uint[64];
        words[0] = 1;
        words[63] = 0xFFFFFFFF;
        string text = ExploredZonesText.Format(words);
        Assert.StartsWith("1 0 ", text, StringComparison.Ordinal);
        Assert.EndsWith(" 4294967295 ", text, StringComparison.Ordinal); // one trailing space per value, as vmangos writes it
        Assert.Equal(words, ExploredZonesText.Parse(text));
        Assert.Equal(words, ExploredZonesText.Parse(text.TrimEnd()));

        Assert.Throws<InvalidDataException>(() => ExploredZonesText.Parse("1 2 3"));                                        // wrong count
        Assert.Throws<InvalidDataException>(() => ExploredZonesText.Parse(string.Join(' ', Enumerable.Repeat("1", 65))));    // too many
        Assert.Throws<InvalidDataException>(() => ExploredZonesText.Parse(string.Join(' ', Enumerable.Repeat("x", 64))));    // not numeric
        Assert.Throws<InvalidDataException>(() => ExploredZonesText.Parse(string.Join(' ', Enumerable.Repeat("4294967296", 64)))); // out of range
        Assert.Throws<InvalidDataException>(() => ExploredZonesText.Parse(string.Join(' ', Enumerable.Repeat("-1", 64))));
        Assert.Throws<ArgumentException>(() => ExploredZonesText.Format(new uint[10]));
    }

    [Fact]
    public void Module_HasItsConstantVersion_AndACleanup()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is ExploredZonesDataModule);
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(ExploredZonesDataModule.Version, module.SchemaVersion);
        Assert.Contains(CharacterDataCleanups.All, c => c is ExploredZonesDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Words_RoundTripBitExact_AndMissingRowIsNull(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int[] ids) = await CreateAsync(provider, "Explorer", "Other");
        Assert.Null(await LoadAsync(connection, ids[0]));

        uint[] words = new uint[64];
        words[1] = 0x00000008; // word 1 bit 3
        words[2] = 0xFFFFFFFF;
        words[5] = 0x80000000;
        words[63] = 0x7FFFFFFF;
        await WriteAsync(connection, s => s.SaveAsync(ids[0], words));
        Assert.Equal(words, await LoadAsync(connection, ids[0]));
        Assert.Null(await LoadAsync(connection, ids[1]));

        words[1] |= 1; // update in place
        await WriteAsync(connection, s => s.SaveAsync(ids[0], words));
        Assert.Equal(words, await LoadAsync(connection, ids[0]));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(1, await verify.Set<ExploredZonesEntity>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MalformedRow_FailsTheLoad_InsteadOfZeroing(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int[] ids) = await CreateAsync(provider, "Broken");
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            db.Set<ExploredZonesEntity>().Add(new ExploredZonesEntity { CharacterId = ids[0], Zones = "1 2 3" });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(connection, ids[0]));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MissingCharacter_IsIgnored_DeleteIsIdempotent_AndCharacterDeletionRemovesTheRow(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int[] ids) = await CreateAsync(provider, "Gone", "Kept");
        uint[] words = new uint[64];
        words[0] = 5;
        await WriteAsync(connection, s => s.SaveAsync(9999, words)); // deleted while queued: ignored
        Assert.Null(await LoadAsync(connection, 9999));

        await WriteAsync(connection, s => s.SaveAsync(ids[0], words));
        await WriteAsync(connection, s => s.SaveAsync(ids[1], words));
        await WriteAsync(connection, s => s.DeleteAsync(ids[1]));
        await WriteAsync(connection, s => s.DeleteAsync(ids[1]));
        Assert.Null(await LoadAsync(connection, ids[1]));
        await WriteAsync(connection, s => s.SaveAsync(ids[1], words));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(ids[0], accountId: 31));
        }

        Assert.Null(await LoadAsync(connection, ids[0]));
        Assert.Equal(words, await LoadAsync(connection, ids[1]));

        // a replayed write for the deleted character is ignored, not resurrected
        await WriteAsync(connection, s => s.SaveAsync(ids[0], words));
        Assert.Null(await LoadAsync(connection, ids[0]));
    }

    private async Task<(DatabaseConnectionOptions Connection, int[] Ids)> CreateAsync(DatabaseProvider provider, params string[] names)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        var ids = new List<int>();
        foreach (string name in names)
        {
            ids.Add((await store.CreateAsync(new CharacterRecord { AccountId = 31, Name = name, Race = 1, Class = 1, Level = 10 })).Id);
        }

        return (connection, [.. ids]);
    }

    private static async Task WriteAsync(DatabaseConnectionOptions connection, Func<IExploredZonesStore, Task> write)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await write(new EfExploredZonesStore(db));
    }

    private static async Task<uint[]?> LoadAsync(DatabaseConnectionOptions connection, int characterId)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await new EfExploredZonesStore(db).LoadAsync(characterId);
    }
}
