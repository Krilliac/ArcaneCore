using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Talents;

/// <summary>The single-writer talent state queue: ordering, retain-on-failure reconciliation and character deletion.</summary>
public sealed class TalentPersistenceTests : IAsyncLifetime
{
    private readonly MemoryTalentStore _store = new();
    private readonly ServiceProvider _provider;
    private readonly TalentPersistence _persistence;

    public TalentPersistenceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICharacterTalentStore>(_store);
        _provider = services.BuildServiceProvider();
        _persistence = new TalentPersistence(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        _persistence.Start();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _persistence.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Changes_AreWrittenInOrder()
    {
        _persistence.SaveRespec(1, new RespecState(3, 100));
        _persistence.SetDisabled(1, 30, true);
        _persistence.SetDisabled(1, 10, true);
        _persistence.SetDisabled(1, 30, false);
        _persistence.SaveRespec(1, new RespecState(4, 200));
        await _persistence.FlushAsync();

        Assert.Equal(new CharacterTalentState(4, 200), _store.RespecOf(1));
        Assert.Equal<uint>([10], _store.DisabledOf(1));
    }

    [Fact]
    public async Task ARedundantDisabledChange_QueuesNothing()
    {
        _persistence.SetDisabled(2, 7, true);
        await _persistence.FlushAsync();
        int writes = _store.Writes;

        _persistence.SetDisabled(2, 7, true);    // already disabled
        _persistence.SetDisabled(2, 8, false);   // never disabled
        await _persistence.FlushAsync();

        Assert.Equal(writes, _store.Writes);
    }

    [Fact]
    public async Task LoadCharacter_ReadsTheStore_AndSeedsTheDesiredState()
    {
        await _store.SaveAsync(3, new CharacterTalentState(2, 50));
        await _store.AddDisabledAsync(3, [5, 6]);

        (CharacterTalentState? respec, IReadOnlyList<uint> disabled) = await _persistence.LoadCharacterAsync(3, _store);

        Assert.Equal(new CharacterTalentState(2, 50), respec);
        Assert.Equal<uint>([5, 6], disabled);
        int writes = _store.Writes;
        _persistence.SetDisabled(3, 5, true);    // already known as disabled from the load: nothing to write
        await _persistence.FlushAsync();
        Assert.Equal(writes, _store.Writes);
    }

    [Fact]
    public async Task LoadCharacter_OfAnUnknownCharacter_IsEmpty()
    {
        (CharacterTalentState? respec, IReadOnlyList<uint> disabled) = await _persistence.LoadCharacterAsync(99, _store);

        Assert.Null(respec);
        Assert.Empty(disabled);
    }

    [Fact]
    public async Task AFailedWrite_IsRetained_AndReconciledOnceTheStoreRecovers()
    {
        _store.FailWrites = true;
        _persistence.SaveRespec(4, new RespecState(1, 10));
        _persistence.SetDisabled(4, 9, true);
        await _persistence.FlushAsync();

        Assert.Contains(4, _persistence.FailedCharacters);
        Assert.Null(_store.RespecOf(4));
        await Assert.ThrowsAsync<IOException>(() => _persistence.FlushCharacterAsync(4));   // still failing: surfaced, still retained
        Assert.Contains(4, _persistence.FailedCharacters);

        _store.FailWrites = false;
        await _persistence.FlushCharacterAsync(4);

        Assert.Empty(_persistence.FailedCharacters);
        Assert.Equal(new CharacterTalentState(1, 10), _store.RespecOf(4));
        Assert.Equal<uint>([9], _store.DisabledOf(4));
    }

    [Fact]
    public async Task Reconciliation_AlsoRemovesRowsThatShouldNoLongerExist()
    {
        await _store.AddDisabledAsync(5, [1, 2]);
        await _persistence.LoadCharacterAsync(5, _store);
        _store.FailWrites = true;
        _persistence.SetDisabled(5, 1, false);          // the removal fails
        await _persistence.FlushAsync();
        _store.FailWrites = false;

        await _persistence.FlushCharacterAsync(5);

        Assert.Equal<uint>([2], _store.DisabledOf(5));
    }

    [Fact]
    public async Task DeleteCharacter_DropsTheStateAndTheRows()
    {
        _persistence.SaveRespec(6, new RespecState(1, 1));
        _persistence.SetDisabled(6, 4, true);
        await _persistence.FlushAsync();

        _persistence.DeleteCharacter(6);
        await _persistence.FlushAsync();

        Assert.Null(_store.RespecOf(6));
        Assert.Empty(_store.DisabledOf(6));
        (CharacterTalentState? respec, _) = await _persistence.LoadCharacterAsync(6, null);
        Assert.Null(respec);                              // the desired state is gone too
    }

    [Fact]
    public async Task TheDeleteHook_DrainsQueuedWrites_ThenRemovesTheCharactersState()
    {
        var feature = new TalentFeature(_provider, _provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TalentFeature>.Instance);
        feature.Persistence.Start();
        var hook = new TalentCharacterDeleteHook(feature) { DrainTimeout = TimeSpan.FromSeconds(5) };
        var character = new CharacterRecord { Id = 7, Name = "Deleted" };
        feature.Persistence.SaveRespec(7, new RespecState(2, 2));
        feature.Persistence.SetDisabled(7, 3, true);

        await hook.OnCharacterDeletingAsync(null!, character);
        Assert.Equal(new CharacterTalentState(2, 2), _store.RespecOf(7));         // queued writes reached the store first
        await hook.OnCharacterDeletedAsync(null!, character);
        await hook.OnCharacterDeletedAsync(null!, character);                      // idempotent

        Assert.Null(_store.RespecOf(7));
        Assert.Empty(_store.DisabledOf(7));
        await feature.DisposeAsync();
    }

    [Fact]
    public async Task WithoutAStoreRegistration_ChangesAreDroppedQuietly()
    {
        var empty = new ServiceCollection().BuildServiceProvider();
        await using var persistence = new TalentPersistence(empty.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        persistence.Start();

        persistence.SaveRespec(8, new RespecState(1, 1));
        await persistence.FlushAsync();

        Assert.Empty(persistence.FailedCharacters);
    }
}
