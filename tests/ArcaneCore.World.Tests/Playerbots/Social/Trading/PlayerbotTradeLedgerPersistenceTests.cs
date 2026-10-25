using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social.Trading;

/// <summary>P4 S3: the ledger survives a bot stop and a restart through <see cref="IPlayerbotTradeRecordStore"/>, and a deleted bot leaves no rows.</summary>
public sealed class PlayerbotTradeLedgerPersistenceTests
{
    private sealed class MemoryTradeRecordStore : IPlayerbotTradeRecordStore
    {
        private readonly Dictionary<Guid, PlayerbotTradeRecord[]> _rows = [];
        public int Replaces { get; private set; }

        // The checkpoint writes on a pool thread while tests poll LoadAllAsync, so the rows are guarded.
        public Task<IReadOnlyList<PlayerbotTradeRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            lock (_rows) return Task.FromResult<IReadOnlyList<PlayerbotTradeRecord>>([.. _rows.Values.SelectMany(rows => rows)]);
        }

        public Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotTradeRecord> rows, CancellationToken cancellationToken = default)
        {
            lock (_rows)
            {
                Replaces++;
                if (rows.Count == 0) _rows.Remove(botId);
                else _rows[botId] = [.. rows];
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ALedgerRow_NotedBeforeStop_IsPresentAfterRestart_AndAnUnchangedLedgerIsNotRewritten()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var store = new MemoryTradeRecordStore();
        Guid id;
        await using (WorldTestHost first = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotTradeRecordStore>(store)))
        {
            var feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            await feature.StartupAsync(default);
            id = Assert.IsType<Guid>((await feature.CreateAsync("Ledgerbot", 1, 1)).BotId);
            Assert.True((await feature.StartAsync(id.ToString())).Success);
            int characterId = (await owners.FindAsync(id))!.CharacterId;
            PlayerbotTradeLedger ledger = Assert.IsType<PlayerbotTradeLedger>(feature.Social.Ledger);
            ledger.NoteFairDeal(id, characterId, 4242);
            ledger.NoteScam(id, characterId, 4343);
            await feature.ShutdownBeforeWorldStopAsync();
        }

        PlayerbotTradeRecord[] saved = [.. (await store.LoadAllAsync()).OrderBy(r => r.OtherGuid)];
        Assert.Equal(2, saved.Length);
        Assert.Equal((id, 4242UL, 1, 0), (saved[0].BotId, saved[0].OtherGuid, saved[0].FairDeals, saved[0].Scams));
        Assert.Equal((4343UL, 1, 0, true), (saved[1].OtherGuid, saved[1].Scams, saved[1].FairDeals, saved[1].LastScamUnixMs > 0));

        await using WorldTestHost second = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotTradeRecordStore>(store));
        var restarted = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await restarted.StartupAsync(default);
        PlayerbotTradeLedger loaded = Assert.IsType<PlayerbotTradeLedger>(restarted.Social.Ledger);
        Assert.True(loaded.TryGet(id, 4242, out PlayerbotTradeRecord fair));
        Assert.Equal(1, fair.FairDeals);
        Assert.True(loaded.IsScammer(id, 4343));
        Assert.True((await restarted.StartAsync(id.ToString())).Success);
        int writes = store.Replaces;
        await restarted.ShutdownBeforeWorldStopAsync();
        Assert.Equal(writes, store.Replaces); // nothing changed: nothing written
    }

    [Fact]
    public async Task DeletingABot_ForgetsItsLedger_InMemoryAndInTheStore_AndKeepsTheOthers()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var store = new MemoryTradeRecordStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotTradeRecordStore>(store));
        var feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid gone = Assert.IsType<Guid>((await feature.CreateAsync("Goneledger", 1, 1)).BotId);
        Guid kept = Assert.IsType<Guid>((await feature.CreateAsync("Keptledger", 1, 1)).BotId);
        int goneCharacter = (await owners.FindAsync(gone))!.CharacterId, keptCharacter = (await owners.FindAsync(kept))!.CharacterId;
        PlayerbotTradeLedger ledger = Assert.IsType<PlayerbotTradeLedger>(feature.Social.Ledger);
        ledger.NoteScam(gone, goneCharacter, 4242);
        ledger.NoteScam(kept, keptCharacter, 4242);
        await store.ReplaceAsync(gone, ledger.Snapshot(gone));
        await store.ReplaceAsync(kept, ledger.Snapshot(kept));
        Assert.Equal(2, (await store.LoadAllAsync()).Count);

        Assert.True((await feature.DeleteAsync(gone.ToString())).Success);

        Assert.Empty(ledger.Snapshot(gone));
        Assert.True(ledger.IsScammer(kept, 4242));
        PlayerbotTradeRecord left = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(kept, left.BotId);
    }

    [Fact]
    public async Task DeletingABot_RemovesTheRowsOtherBotsHoldAboutIt_FromMemoryAndFromTheirStoredRows()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var store = new MemoryTradeRecordStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotTradeRecordStore>(store));
        var feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid gone = Assert.IsType<Guid>((await feature.CreateAsync("Goneabout", 1, 1)).BotId);
        Guid kept = Assert.IsType<Guid>((await feature.CreateAsync("Keptabout", 1, 1)).BotId);
        int goneCharacter = (await owners.FindAsync(gone))!.CharacterId, keptCharacter = (await owners.FindAsync(kept))!.CharacterId;
        ulong aboutGone = ArcaneCore.Game.ObjectGuid.Player((uint)goneCharacter).Value;
        PlayerbotTradeLedger ledger = Assert.IsType<PlayerbotTradeLedger>(feature.Social.Ledger);
        ledger.NoteScam(kept, keptCharacter, aboutGone);
        ledger.NoteScam(kept, keptCharacter, 4242);
        await store.ReplaceAsync(kept, ledger.Snapshot(kept));
        Assert.Equal(2, (await store.LoadAllAsync()).Count);

        Assert.True((await feature.DeleteAsync(gone.ToString())).Success);

        Assert.False(ledger.TryGet(kept, aboutGone, out _));
        Assert.True(ledger.TryGet(kept, 4242, out _));
        PlayerbotTradeRecord left = Assert.Single(await store.LoadAllAsync()); // the kept bot is not running: only the delete itself rewrote its stored rows
        Assert.Equal((kept, 4242UL), (left.BotId, left.OtherGuid));
    }

    /// <summary>Holds the first non-empty write of one bot until released; every other write goes straight through.</summary>
    private sealed class HoldingTradeRecordStore(IPlayerbotTradeRecordStore inner, Func<Guid> held) : IPlayerbotTradeRecordStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _heldOnce;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public Task<IReadOnlyList<PlayerbotTradeRecord>> LoadAllAsync(CancellationToken cancellationToken = default) => inner.LoadAllAsync(cancellationToken);

        public async Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotTradeRecord> rows, CancellationToken cancellationToken = default)
        {
            if (botId == held() && rows.Count > 0 && Interlocked.Exchange(ref _heldOnce, 1) == 0)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            await inner.ReplaceAsync(botId, rows, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task ASaveInFlight_WhenABotIsDeleted_CannotWriteBackTheRowAboutIt_BecauseTheWritesShareOneGate()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var backing = new MemoryTradeRecordStore();
        Guid runningId = Guid.Empty;
        var store = new HoldingTradeRecordStore(backing, () => runningId);
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotTradeRecordStore>(store));
        var feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        runningId = Assert.IsType<Guid>((await feature.CreateAsync("Runningrace", 1, 1)).BotId);
        Guid gone = Assert.IsType<Guid>((await feature.CreateAsync("Gonerace", 1, 1)).BotId);
        int runningCharacter = (await owners.FindAsync(runningId))!.CharacterId, goneCharacter = (await owners.FindAsync(gone))!.CharacterId;
        ulong aboutGone = ArcaneCore.Game.ObjectGuid.Player((uint)goneCharacter).Value;
        Assert.True((await feature.StartAsync(runningId.ToString())).Success);
        PlayerbotTradeLedger ledger = Assert.IsType<PlayerbotTradeLedger>(feature.Social.Ledger);
        ledger.NoteScam(runningId, runningCharacter, aboutGone);
        ledger.NoteScam(runningId, runningCharacter, 4242);

        // The running bot's stop snapshots both rows, then waits inside the store: its write is in flight with the row about the character that is about to be deleted.
        var stopping = feature.StopAsync(runningId.ToString());
        await store.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        // The deletion cleanup runs meanwhile: it forgets the row in memory and rewrites the running bot's rows without it. Both writes must queue behind the first.
        Task forgetting = feature.ForgetRelationsAsync(host.WorldServices, gone, goneCharacter);
        await Task.Delay(300);
        Assert.False(forgetting.IsCompleted); // not gated: it would have written already and the in-flight write would then land last, bringing the row back

        store.Release();
        Assert.True((await stopping.WaitAsync(TimeSpan.FromSeconds(30))).Success);
        await forgetting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(ledger.TryGet(runningId, aboutGone, out _));
        PlayerbotTradeRecord stored = Assert.Single(await backing.LoadAllAsync());
        Assert.Equal((runningId, 4242UL), (stored.BotId, stored.OtherGuid));
    }
}
