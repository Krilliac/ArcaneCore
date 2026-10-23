using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>The social memory survives a bot stop and a server restart, the way set-asides do.</summary>
public sealed class PlayerbotSocialMemoryPersistenceTests
{
    internal sealed class MemoryRelationStore : IPlayerbotRelationStore
    {
        private readonly Dictionary<Guid, PlayerbotRelation[]> _rows = [];
        public bool ThrowOnReplace { get; set; }
        public int Replaces { get; private set; }

        public Task<IReadOnlyList<PlayerbotRelation>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlayerbotRelation>>([.. _rows.Values.SelectMany(rows => rows)]);

        public Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotRelation> rows, CancellationToken cancellationToken = default)
        {
            if (ThrowOnReplace) throw new InvalidOperationException("relation-storage-refused");
            Replaces++;
            _rows[botId] = [.. rows];
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Relation_NotedBeforeStop_IsPresentAfterRestart()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var store = new MemoryRelationStore();
        Guid id;
        await using (WorldTestHost first = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotRelationStore>(store)))
        {
            var feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            await feature.StartupAsync(default);
            id = Assert.IsType<Guid>((await feature.CreateAsync("Relbot", 1, 1)).BotId);
            Assert.True((await feature.StartAsync(id.ToString())).Success);
            int characterId = (await owners.FindAsync(id))!.CharacterId;
            feature.Social.Memory.Note(id, characterId, 4242, false, PlayerbotSocialEvent.Grouped);
            feature.Social.Memory.SetTopic(id, 4242, "greeting", "Hello there.");
            await feature.ShutdownBeforeWorldStopAsync();
        }

        PlayerbotRelation saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal((id, 4242UL, (short)3), (saved.BotId, saved.OtherGuid, saved.Affinity));

        await using WorldTestHost second = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotRelationStore>(store));
        var restarted = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await restarted.StartupAsync(default);
        Assert.True(restarted.Social.Memory.TryGet(id, 4242, out PlayerbotRelation loaded));
        Assert.Equal("greeting", loaded.LastTopic);
        Assert.Equal("Hello there.", loaded.Summary);
        Assert.True((await restarted.StartAsync(id.ToString())).Success);
        int writes = store.Replaces;
        await restarted.ShutdownBeforeWorldStopAsync();
        Assert.Equal(writes, store.Replaces); // nothing changed: nothing written
    }

    [Fact]
    public async Task ThrowingStore_NeverBlocksTheStop_AndLeavesTheBotStopped()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var store = new MemoryRelationStore { ThrowOnReplace = true };
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            extraServices: services => services.AddSingleton<IPlayerbotRelationStore>(store));
        var feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Faultrel", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        feature.Social.Memory.Note(id, (await owners.FindAsync(id))!.CharacterId, 77, false, PlayerbotSocialEvent.TradeCompleted);

        await feature.ShutdownBeforeWorldStopAsync();

        Assert.Equal(ManagedPlayerbotState.Stopped, (await owners.FindAsync(id))!.State);
        Assert.Empty(await store.LoadAllAsync());
    }
}
