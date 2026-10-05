using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// After a deletion the write queues forget the character
/// (<see cref="CharacterSaveQueue.ForgetCharacter"/>, <see cref="QuestNpcPersistence.ForgetCharacter"/>):
/// a retained failed snapshot is neither retried nor rewritten at shutdown, and a later
/// character that reuses the id inherits no hold, quarantine or journal.
/// </summary>
public sealed class ForgetDeletedCharacterTests
{
    [Fact]
    public async Task SaveQueue_ForgetsHoldsQuarantineAndTheRetainedFailure()
    {
        var store = new FlakyCharacterStore { Fail = true };
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        queue.Enqueue(State(1, money: 44));
        await Assert.ThrowsAsync<IOException>(() => queue.FlushCharacterAsync(1));
        queue.HoldCharacter(1);
        queue.QuarantineCharacter(1);

        queue.ForgetCharacter(1);

        Assert.False(queue.IsHeld(1));
        Assert.False(queue.IsQuarantined(1));
        store.Fail = false;
        await queue.FlushCharacterAsync(1);
        Assert.Empty(store.Saved); // the deleted character's snapshot is not retried
        queue.Enqueue(State(1, money: 55)); // a new character with the reused id saves normally
        await queue.FlushCharacterAsync(1);
        Assert.Equal(55u, Assert.Single(store.Saved).Money);
        await queue.StopAsync(); // nothing retained, so shutdown reports no failure
    }

    [Fact]
    public async Task QuestQueue_ForgetsTheSnapshot_SoShutdownAndAReusedIdNeverWriteIt()
    {
        var storage = new QuestStorage { Fail = true };
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<ICharacterQuestStore>(_ => storage).BuildServiceProvider();
        await using var queue = new QuestNpcPersistence(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.LoadCharacter(7, new CharacterQuestData([Row(7, 101, 3)], [1]), queue.CaptureLoadRevision(7));
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(7));
        queue.QuarantineCharacter(7);

        queue.ForgetCharacter(7);

        storage.Fail = false;
        int attempts = storage.Attempts.Count;
        Assert.False(queue.IsQuarantined(7));
        Assert.Equal(0, queue.CaptureLoadRevision(7)); // a reused id starts from nothing
        await queue.FlushCharacterAsync(7);
        await queue.DisposeAsync(); // no shutdown snapshot and no undrained failure for 7
        Assert.Equal(attempts, storage.Attempts.Count);
    }

    [Fact]
    public async Task QuestQueue_RefusesToForgetWhileWritesAreQueued()
    {
        var storage = new QuestStorage { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<ICharacterQuestStore>(_ => storage).BuildServiceProvider();
        await using var queue = new QuestNpcPersistence(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        queue.SaveQuests(7, [Row(7, 101, 3)]);

        Assert.Throws<InvalidOperationException>(() => queue.ForgetCharacter(7));
        storage.Gate.TrySetResult();
        await queue.FlushCharacterAsync(7).WaitAsync(TimeSpan.FromSeconds(5));
        queue.ForgetCharacter(7);
        Assert.Equal(0, queue.CaptureLoadRevision(7));
    }

    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 0, 0, 1, 0, Money: money);

    private static CharacterQuestStatus Row(int character, uint quest, byte status) =>
        new(character, quest, status, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class FlakyCharacterStore : ICharacterStore
    {
        private readonly InMemoryCharacterStore _inner = new();
        public volatile bool Fail;
        public ConcurrentQueue<CharacterState> Saved { get; } = new();

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                return Task.FromException(new IOException("storage unavailable"));
            }

            Saved.Enqueue(state);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => _inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => _inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => _inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => _inner.GetAllIdentitiesAsync(cancellationToken);
        public Task<IReadOnlyList<int>> FindAccountIdsByNamePrefixAsync(string prefix, int limit, CancellationToken cancellationToken = default) => _inner.FindAccountIdsByNamePrefixAsync(prefix, limit, cancellationToken);
    }

    private sealed class QuestStorage : ICharacterQuestStore
    {
        public volatile bool Fail;
        public TaskCompletionSource? Gate;
        public ConcurrentQueue<string> Attempts { get; } = new();

        public Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(CharacterQuestData.Empty);

        public async Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue($"quests:{characterId}");
            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            if (Fail)
            {
                throw new InvalidOperationException("quest storage unavailable");
            }
        }

        public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask, CancellationToken cancellationToken = default)
        {
            Attempts.Enqueue($"taxi:{characterId}");
            return Fail ? Task.FromException(new InvalidOperationException("quest storage unavailable")) : Task.CompletedTask;
        }
    }
}
