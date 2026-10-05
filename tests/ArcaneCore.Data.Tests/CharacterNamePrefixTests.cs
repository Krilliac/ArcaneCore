using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CharacterNamePrefixTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PrefixQuery_FiltersBeforeLimitingDistinctOwners_AndOrdersByAccount(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        // Nonmatching smaller account ids and multiple matching characters must not consume the account cap.
        await store.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Other" });
        await store.CreateAsync(new CharacterRecord { AccountId = 9, Name = "Patlast" });
        await store.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Patfirst" });
        await store.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Patsecond" });
        await store.CreateAsync(new CharacterRecord { AccountId = 6, Name = "Patmiddle" });
        Assert.Equal([3, 6], await store.FindAccountIdsByNamePrefixAsync("pAt", 2));
        Assert.Equal([3, 6, 9], await store.FindAccountIdsByNamePrefixAsync("PAT", 0));
        Assert.Empty(await store.FindAccountIdsByNamePrefixAsync("zzz", 2));
        // The command previously used StartsWith, not SQL wildcard matching.
        Assert.Empty(await store.FindAccountIdsByNamePrefixAsync("Pat%", 2));
        Assert.Empty(await store.FindAccountIdsByNamePrefixAsync("Pat_", 2));
        Assert.Empty(await store.FindAccountIdsByNamePrefixAsync(new string('x', 2048), 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.FindAccountIdsByNamePrefixAsync("Pat", -1));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.FindAccountIdsByNamePrefixAsync(
            new string('x', 2048), 2, new CancellationToken(canceled: true)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PrefixQuery_PreservesOrdinalCaseMatchingAcrossSupportedScripts_BeforeTheCap(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        // Distinct suffixes permit accent/case-folding unique indexes while the
        // prefix assertions still require ordinal matching before the cap.
        await store.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Remo" });
        await store.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Rémy" });
        await store.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Жанна" });
        await store.CreateAsync(new CharacterRecord { AccountId = 4, Name = "東京" });
        await store.CreateAsync(new CharacterRecord { AccountId = 5, Name = "ẞanka" });
        await store.CreateAsync(new CharacterRecord { AccountId = 6, Name = "ßanni" });
        await store.CreateAsync(new CharacterRecord { AccountId = 7, Name = "Ёлка" });
        await store.CreateAsync(new CharacterRecord { AccountId = 8, Name = "Ａｌｐｈａ" });
        Assert.Equal([1], await store.FindAccountIdsByNamePrefixAsync("re", 1));
        Assert.Equal([2], await store.FindAccountIdsByNamePrefixAsync("rÉ", 1));
        Assert.Equal([2], await store.FindAccountIdsByNamePrefixAsync("ré", 1));
        Assert.Equal([3], await store.FindAccountIdsByNamePrefixAsync("жА", 1));
        Assert.Equal([4], await store.FindAccountIdsByNamePrefixAsync("東", 1));
        Assert.Equal([7], await store.FindAccountIdsByNamePrefixAsync("ёЛ", 1));
        Assert.Equal([8], await store.FindAccountIdsByNamePrefixAsync("ａＬ", 1));
        Assert.Empty(await store.FindAccountIdsByNamePrefixAsync("AL", 1));
        Assert.Empty(await store.FindAccountIdsByNamePrefixAsync("жАнНадлиннее", 1));
        IReadOnlyList<int> sharpS = await store.FindAccountIdsByNamePrefixAsync("ß", 0);
        Assert.Equal(new[] { (5, "ẞanka"), (6, "ßanni") }.Where(c => c.Item2.StartsWith("ß", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Item1), sharpS);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
