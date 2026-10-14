using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

public sealed class QuestNpcOwnershipPersistenceTests
{
    [Fact]
    public async Task TransientRecoveryAndShutdown_RetainTheOriginalOwnershipToken()
    {
        var storage = new TokenStorage { FailuresRemaining = 1 };
        await using ServiceProvider provider = Provider(storage);
        var queue = Queue(provider);
        CharacterOwnershipToken token = Token(7, 3);
        queue.LoadCharacter(7, new CharacterQuestData([Row(7, 101)], [1, 2]), queue.CaptureLoadRevision(7), token);

        queue.SaveQuests(7, [Row(7, 101, status: 5)], token);
        queue.SaveTaxiMask(7, [9, 10], token);
        await queue.FlushCharacterAsync(7).WaitAsync(TimeSpan.FromSeconds(5));
        storage.FailuresRemaining = 1;
        queue.SaveQuests(7, [Row(7, 102, status: 5)], token);
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(storage.Tokens);
        Assert.All(storage.Tokens, observed => Assert.Equal(token, observed));
        Assert.True(storage.Attempts.Count >= 7, "flush recovery and a second failed write recovered during shutdown should be observable");
    }

    [Fact]
    public async Task OwnershipRejection_IsGlobalTerminalAndRaisesFatalOnce()
    {
        var storage = new TokenStorage { RejectWrites = true, HoldFirstQuest = true };
        await using ServiceProvider provider = Provider(storage);
        var queue = Queue(provider);
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        int fatalCount = 0;
        queue.FatalFailure += error =>
        {
            Interlocked.Increment(ref fatalCount);
            fatal.TrySetResult(error);
        };
        CharacterOwnershipToken old = Token(7, 1);
        queue.LoadCharacter(7, CharacterQuestData.Empty, queue.CaptureLoadRevision(7), old);
        queue.LoadCharacter(8, CharacterQuestData.Empty, queue.CaptureLoadRevision(8), old);
        queue.SaveQuests(7, [Row(7, 202)], old);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SaveTaxiMask(8, [4], old);
        storage.Release.TrySetResult();

        CharacterOwnershipRejectedException rejection = Assert.IsType<CharacterOwnershipRejectedException>(
            await fatal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("world", rejection.Scope);
        Assert.Equal(1, Volatile.Read(ref fatalCount));
        Assert.Equal(1, storage.Attempts.Count(attempt => attempt.Kind == "quest"));
        Assert.Equal(0, storage.Attempts.Count(attempt => attempt.Kind == "taxi"));

        Assert.Throws<InvalidOperationException>(() => queue.SaveQuests(7, [Row(7, 303)], old));
        Assert.Throws<InvalidOperationException>(() => queue.SaveTaxiMask(7, [8], old));
        Assert.Throws<InvalidOperationException>(() => { _ = queue.FlushCharacterAsync(7); });
        Assert.Throws<InvalidOperationException>(() => queue.QuarantineCharacter(7));
        Assert.Throws<InvalidOperationException>(() => queue.ForgetCharacter(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DisposeAsync().AsTask());
    }

    private static QuestNpcPersistence Queue(IServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

    private static ServiceProvider Provider(TokenStorage storage) => new ServiceCollection()
        .AddScoped<ICharacterQuestStore>(_ => new TokenStore(storage))
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static CharacterOwnershipToken Token(int characterId, long epoch) =>
        new("world", epoch, Guid.Parse($"00000000-0000-0000-0000-{characterId:000000000000}"));

    private static CharacterQuestStatus Row(int characterId, uint quest, byte status = 3) =>
        new(characterId, quest, status, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class TokenStorage
    {
        public readonly ConcurrentQueue<(string Kind, CharacterOwnershipToken? Token)> Attempts = new();
        public readonly ConcurrentQueue<CharacterOwnershipToken?> Tokens = new();
        public volatile bool RejectWrites;
        public int FailuresRemaining;
        public bool HoldFirstQuest;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TokenStore(TokenStorage storage) : ICharacterQuestStore, IDisposable
    {
        public Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(CharacterQuestData.Empty);

        public async Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
        {
            storage.Attempts.Enqueue(("quest", ownership));
            storage.Tokens.Enqueue(ownership);
            if (storage.HoldFirstQuest)
            {
                storage.Entered.TrySetResult();
                await storage.Release.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            ThrowIfRejectedOrTransient(ownership);
        }

        public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
        {
            storage.Attempts.Enqueue(("taxi", ownership));
            storage.Tokens.Enqueue(ownership);
            ThrowIfRejectedOrTransient(ownership);
            return Task.CompletedTask;
        }

        public void Dispose() { }

        private void ThrowIfRejectedOrTransient(CharacterOwnershipToken? ownership)
        {
            if (storage.RejectWrites)
                throw new CharacterOwnershipRejectedException(ownership?.Scope ?? "world", ownership?.Epoch ?? 0);
            if (Interlocked.CompareExchange(ref storage.FailuresRemaining, 0, 0) > 0)
            {
                Interlocked.Decrement(ref storage.FailuresRemaining);
                throw new IOException("transient quest storage failure");
            }
        }
    }
}
