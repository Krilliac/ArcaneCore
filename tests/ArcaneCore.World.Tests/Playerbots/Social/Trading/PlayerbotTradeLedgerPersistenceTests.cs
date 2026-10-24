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
}
