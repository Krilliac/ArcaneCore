using System.Data.Common;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Real transactions held across asynchronous suspension, then committed, interrupted, or retried.</summary>
public sealed class QuestRewardAsyncStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> Interruptions()
    {
        foreach (object[] provider in Providers())
        {
            yield return [provider[0], false];
            yield return [provider[0], true];
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DelayedCommit_AfterRealSaveChangesRemainsInvisibleUntilDurableCommit(DatabaseProvider provider)
    {
        using var deadline = TestDeadline();
        Seed seed = await CreateAsync(provider, deadline.Token);
        var gate = new AfterRewardSaveGate();
        await using CharacterDbContext db = Context(seed.Connection, gate);
        Task<QuestRewardCommitResult> commit = new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request, deadline.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(deadline.Token);
            Assert.False(commit.IsCompleted);
            Assert.NotNull(db.Database.CurrentTransaction);
            await AssertPersistedAsync(seed, rewarded: false, deadline.Token);

            gate.Release.TrySetResult(true);
            Assert.Equal(QuestRewardCommitResult.Committed, await commit.WaitAsync(deadline.Token));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            await AssertPersistedAsync(seed, rewarded: true, deadline.Token);
            Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitFreshAsync(seed, deadline.Token));
            await AssertPersistedAsync(seed, rewarded: true, deadline.Token);
        }
        finally
        {
            gate.Release.TrySetResult(true);
            await ObserveCompletionAsync(commit);
        }
    }

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task InterruptionWhileAwaitingAfterSaveChanges_RollsBackEverythingAndRetainedRequestCanRetry(
        DatabaseProvider provider, bool cancel)
    {
        using var deadline = TestDeadline();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Seed seed = await CreateAsync(provider, deadline.Token);
        var gate = new AfterRewardSaveGate { ThrowAfterRelease = !cancel };
        await using CharacterDbContext db = Context(seed.Connection, gate);
        Task<QuestRewardCommitResult> commit = new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request, cancellation.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(deadline.Token);
            Assert.False(commit.IsCompleted);
            Assert.NotNull(db.Database.CurrentTransaction);
            await AssertPersistedAsync(seed, rewarded: false, deadline.Token);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
            }
            else
            {
                gate.Release.TrySetResult(true);
                await Assert.ThrowsAsync<IOException>(() => commit);
            }

            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(0, await db.SaveChangesAsync(deadline.Token));
            await AssertPersistedAsync(seed, rewarded: false, deadline.Token);
            Assert.Equal(QuestRewardCommitResult.Committed, await CommitFreshAsync(seed, deadline.Token));
            Assert.Equal(QuestRewardCommitResult.AlreadyRewarded, await CommitFreshAsync(seed, deadline.Token));
            await AssertPersistedAsync(seed, rewarded: true, deadline.Token);
        }
        finally
        {
            gate.Release.TrySetResult(true);
            await ObserveCompletionAsync(commit);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RetainedRequest_DetachesMutableBuffersAcrossAnAwaitAndReplayCannotChangeItsDurableReward(DatabaseProvider provider)
    {
        using var deadline = TestDeadline();
        Seed seed = await CreateAsync(provider, deadline.Token);
        InventoryItemData[] before = seed.Request.Before.Inventory!.Items.ToArray();
        InventoryItemData[] after = seed.Request.After.Inventory!.Items.ToArray();
        int[] charges = after[0].Item.Charges.ToArray();
        uint[] enchantments = after[0].Item.Enchantments.ToArray();
        after[0] = after[0] with { Item = after[0].Item with { Charges = charges, Enchantments = enchantments } };
        ActionButton[] buttons = seed.Request.After.ActionButtons!.ToArray();
        CharacterQuestRewardRequest retained = seed.Request with
        {
            Before = seed.Request.Before with { Inventory = new InventorySnapshot(before) },
            After = seed.Request.After with { Inventory = new InventorySnapshot(after), ActionButtons = buttons },
        };
        var gate = new FirstReadGate();
        await using CharacterDbContext db = Context(seed.Connection, gate);
        Task<QuestRewardCommitResult> commit = new EfCharacterQuestRewardStore(db).CommitAsync(retained, deadline.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(deadline.Token);
            Assert.False(commit.IsCompleted);
            before[0] = before[0] with { Slot = 38, Item = before[0].Item with { Count = 999 } };
            charges[0] = 888;
            enchantments[0] = 777;
            after[0] = after[0] with { Item = after[0].Item with { Count = 666 } };
            buttons[0] = new ActionButton(0, 555, 0);
            gate.Release.TrySetResult(true);

            Assert.Equal(QuestRewardCommitResult.Committed, await commit.WaitAsync(deadline.Token));
            await AssertPersistedAsync(seed, rewarded: true, deadline.Token);
            await using CharacterDbContext replay = TestContexts.Create<CharacterDbContext>(seed.Connection);
            Assert.Equal(QuestRewardCommitResult.AlreadyRewarded,
                await new EfCharacterQuestRewardStore(replay).CommitAsync(retained, deadline.Token));
            await AssertPersistedAsync(seed, rewarded: true, deadline.Token);
        }
        finally
        {
            gate.Release.TrySetResult(true);
            await ObserveCompletionAsync(commit);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<Seed> CreateAsync(DatabaseProvider provider, CancellationToken token)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema, cancellationToken: token);
        var characters = new EfCharacterStore(db);
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = 77, Name = "AsyncReward", Race = 1, Class = 1, Level = 10,
        }, token);
        var before = new CharacterState(character.Id, 0, 12, 1, 2, 3, 0, 10, 50,
            Money: 100, Home: new HomeBind(0, 12, 1, 2, 3), ActionButtons: [new ActionButton(0, 117, 0)],
            Inventory: new InventorySnapshot([new InventoryItemData(0, 23, Item(100, 117, 4))]));
        await characters.SaveStateAsync(before, token);
        var expected = new CharacterQuestStatus(character.Id, 900003, 1, false, false, 0,
            2, 0, 0, 0, 0, 0, 0, 0, 0);
        await new EfCharacterQuestStore(db).SaveQuestsAsync(character.Id, [expected], token);
        CharacterState after = before with
        {
            Money = 1334, ActionButtons = [new ActionButton(0, 118, 0)],
            Inventory = new InventorySnapshot([
                new InventoryItemData(0, 23, Item(100, 117, 2)),
                new InventoryItemData(0, 24, Item(101, 900040, 1)),
                new InventoryItemData(0, 25, Item(102, 900042, 1)),
            ]),
        };
        return new Seed(connection, new CharacterQuestRewardRequest(before, after, expected,
            expected with { Rewarded = true, RewardChoice = 900042 }));
    }

    private static async Task<QuestRewardCommitResult> CommitFreshAsync(Seed seed, CancellationToken token)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        return await new EfCharacterQuestRewardStore(db).CommitAsync(seed.Request, token);
    }

    private static async Task AssertPersistedAsync(Seed seed, bool rewarded, CancellationToken token)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(seed.Connection);
        CharacterState expected = rewarded ? seed.Request.After : seed.Request.Before;
        CharacterRecord character = Assert.IsType<CharacterRecord>(await new EfCharacterStore(db).GetByIdAsync(expected.Id, token));
        Assert.Equal((expected.MapId, expected.ZoneId, expected.X, expected.Y, expected.Z, expected.Orientation,
                expected.Level, expected.PlayedTime, expected.Money, expected.ActionBarToggles),
            (character.MapId, character.ZoneId, character.X, character.Y, character.Z, character.Orientation,
                character.Level, character.PlayedTime, character.Money, character.ActionBarToggles));
        Assert.Equal(expected.Home, new HomeBind(character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ));
        Assert.Equal(expected.ActionButtons, await new EfCharacterStore(db).GetActionButtonsAsync(expected.Id, token));
        CharacterQuestStatus quest = Assert.Single((await new EfCharacterQuestStore(db).LoadAsync(expected.Id, token)).Quests);
        Assert.Equal(rewarded ? seed.Request.RewardedQuest : seed.Request.ExpectedQuest, quest);
        InventoryItemData[] items = (await new EfItemStore(db).GetInventoryAsync(expected.Id, token)).OrderBy(item => item.Item.Guid).ToArray();
        InventoryItemData[] wanted = expected.Inventory!.Items.OrderBy(item => item.Item.Guid).ToArray();
        Assert.Equal(wanted.Length, items.Length);
        for (int index = 0; index < items.Length; index++)
        {
            Assert.Equal((wanted[index].ContainerGuid, wanted[index].Slot), (items[index].ContainerGuid, items[index].Slot));
            Assert.Equal(wanted[index].Item with { Charges = items[index].Item.Charges, Enchantments = items[index].Item.Enchantments }, items[index].Item);
            Assert.Equal(wanted[index].Item.Charges, items[index].Item.Charges);
            Assert.Equal(wanted[index].Item.Enchantments, items[index].Item.Enchantments);
        }
    }

    private static ItemInstanceData Item(uint guid, uint entry, uint count) => new()
    {
        Guid = guid, Entry = entry, Count = count, Durability = 17, Creator = 7, Flags = 1,
        Charges = [-1, 0, 0, 0, 2], Enchantments = Enumerable.Range(1, 21).Select(value => (uint)value).ToArray(),
    };

    private static CharacterDbContext Context(DatabaseConnectionOptions connection, IInterceptor interceptor)
        => new(new DbContextOptionsBuilder<CharacterDbContext>(TestContexts.Options<CharacterDbContext>(connection))
            .AddInterceptors(interceptor).Options);

    private static async Task ObserveCompletionAsync(Task task)
    {
        try { await task; }
        catch { /* The test observes the expected interruption; cleanup never leaves a transaction running. */ }
    }

    private sealed record Seed(DatabaseConnectionOptions Connection, CharacterQuestRewardRequest Request);

    private sealed class AfterRewardSaveGate : SaveChangesInterceptor
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        internal bool ThrowAfterRelease { get; init; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!Entered.Task.IsCompleted && eventData.Context?.ChangeTracker.Entries<CharacterQuestStatusRow>().Any(entry => entry.Entity.Rewarded) == true)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (ThrowAfterRelease)
                {
                    throw new IOException("Synthetic asynchronous interruption after actual reward SaveChanges.");
                }
            }

            return result;
        }
    }

    private sealed class FirstReadGate : DbCommandInterceptor
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Entered.Task.IsCompleted)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CancellationTokenSource TestDeadline() => new(TimeSpan.FromSeconds(30));
}
