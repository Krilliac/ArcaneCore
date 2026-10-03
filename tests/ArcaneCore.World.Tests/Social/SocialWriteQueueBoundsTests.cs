using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// The social write queue coalesces pending writes per key, bounds what can wait, and keeps what
/// storage refuses (the Codex game-logic finding: an unbounded queue that dropped writes). No test
/// here depends on timing: a gated store holds the single consumer so the queue content is exact.
/// </summary>
public sealed class SocialWriteQueueBoundsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AlternatingAddRemove_OfOneRow_KeepsOneWrite_AndPersistsOnlyTheLastState()
    {
        await using Rig rig = Rig.Create();
        await rig.HoldConsumerAsync();

        for (int i = 0; i < 5_000; i++)
        {
            Assert.True(rig.Queue.TrySetSocial(1, 2, i % 2 == 0 ? SocialFlags.Friend : SocialFlags.None));
            Assert.True(rig.Queue.TrySetSocial(1, 3, i % 2 == 0 ? SocialFlags.Ignored : SocialFlags.Friend));
        }

        // The held write plus one write per distinct row: not one per packet.
        Assert.Equal(3, rig.Queue.Pending);
        Assert.Equal(3, rig.Queue.PendingRows);

        rig.ReleaseConsumer();
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(SocialFlags.None, rig.Store.Row(1, 2));       // last op (i = 4999) cleared it
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(1, 3));
        Assert.Equal(1, rig.Store.SetCalls(1, 2));
        Assert.Equal(1, rig.Store.SetCalls(1, 3));
        Assert.Equal(0, rig.Queue.PendingRows);
    }

    [Fact]
    public async Task APurge_IsABarrier_ASetAfterItIsNeverMergedIntoOneBeforeIt()
    {
        await using Rig rig = Rig.Create();
        await rig.HoldConsumerAsync();
        rig.Queue.SetSocial(1, 2, SocialFlags.Friend);
        rig.Queue.PurgeCharacter(2);
        rig.Queue.SetSocial(1, 2, SocialFlags.Ignored);

        rig.ReleaseConsumer();
        await rig.Queue.FlushAsync().WaitAsync(Wait);

        // In order: friend row written, purged, ignore row written.
        Assert.Equal(SocialFlags.Ignored, rig.Store.Row(1, 2));
        Assert.Equal(2, rig.Store.SetCalls(1, 2));
    }

    [Fact]
    public async Task Random_OperationSequences_EndInTheSameStateAsApplyingEveryOperationInOrder()
    {
        for (int seed = 0; seed < 150; seed++)
        {
            var random = new Random(seed);
            await using Rig rig = Rig.Create();
            var model = new RecordingStore();
            await rig.HoldConsumerAsync();

            int ops = random.Next(1, 60);
            int releaseAt = random.Next(0, ops + 1);   // some sequences run partly while the consumer works
            for (int i = 0; i < ops; i++)
            {
                if (i == releaseAt)
                {
                    rig.ReleaseConsumer();
                }

                switch (random.Next(10))
                {
                    case < 6:
                        int c = random.Next(1, 4);
                        int o = random.Next(1, 4);
                        var flags = (SocialFlags)random.Next(0, 4);
                        Assert.True(rig.Queue.TrySetSocial(c, o, flags));
                        await model.SetSocialAsync(c, o, flags);
                        break;
                    case 6:
                        int purged = random.Next(1, 4);
                        rig.Queue.PurgeCharacter(purged);
                        await model.PurgeCharacterAsync(purged);
                        break;
                    case 7:
                        int g = random.Next(1, 3);
                        GuildData guild = Guild(g, random.Next(1000));
                        rig.Queue.SaveGuild(guild);
                        await model.SaveGuildAsync(guild);
                        break;
                    case 8:
                        int gone = random.Next(1, 3);
                        rig.Queue.DeleteGuild(gone);
                        await model.DeleteGuildAsync(gone);
                        break;
                    default:
                        if (i >= releaseAt)
                        {
                            await rig.Queue.FlushAsync().WaitAsync(Wait);   // a barrier mid-sequence changes nothing
                        }

                        break;
                }
            }

            rig.ReleaseConsumer();
            await rig.Queue.FlushAsync().WaitAsync(Wait);
            Assert.True(
                model.Rows.OrderBy(r => r.Key).SequenceEqual(rig.Store.Rows.Where(r => r.Key != (99, 99)).OrderBy(r => r.Key)),
                $"rows differ for seed {seed}");
            Assert.True(
                model.Guilds.OrderBy(g => g.Key).Select(g => (g.Key, g.Value.Motd)).SequenceEqual(rig.Store.Guilds.OrderBy(g => g.Key).Select(g => (g.Key, g.Value.Motd))),
                $"guilds differ for seed {seed}");
            Assert.Equal(0, rig.Queue.PendingRows);
        }
    }

    [Fact]
    public async Task PerCharacterLimit_RefusesANewRow_ButStillMergesIntoAWaitingOne()
    {
        await using Rig rig = Rig.Create(new SocialWriteQueueOptions { MaxPendingPerCharacter = 3, MaxPendingTotal = 100, RetryDelayMs = 1 });
        await rig.HoldConsumerAsync();

        Assert.True(rig.Queue.TrySetSocial(1, 10, SocialFlags.Friend));
        Assert.True(rig.Queue.TrySetSocial(1, 11, SocialFlags.Friend));
        Assert.True(rig.Queue.TrySetSocial(1, 12, SocialFlags.Friend));
        Assert.False(rig.Queue.TrySetSocial(1, 13, SocialFlags.Friend));   // a fourth row is refused ...
        Assert.True(rig.Queue.TrySetSocial(1, 12, SocialFlags.None));      // ... but a waiting one still takes changes
        Assert.True(rig.Queue.TrySetSocial(2, 10, SocialFlags.Friend));    // and another character is unaffected
        Assert.Equal(5, rig.Queue.PendingRows);   // the parked write of character 99 counts too

        rig.ReleaseConsumer();
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(0, rig.Queue.PendingRows);
        Assert.Equal(SocialFlags.None, rig.Store.Row(1, 12));
        Assert.Equal(SocialFlags.None, rig.Store.Row(1, 13));              // the refused row was not queued
        Assert.True(rig.Queue.TrySetSocial(1, 13, SocialFlags.Friend));    // room again once written
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(SocialFlags.Friend, rig.Store.Row(1, 13));
    }

    [Fact]
    public async Task GlobalLimit_RefusesANewRowOfAnyCharacter()
    {
        await using Rig rig = Rig.Create(new SocialWriteQueueOptions { MaxPendingPerCharacter = 100, MaxPendingTotal = 4, RetryDelayMs = 1 });
        await rig.HoldConsumerAsync();

        Assert.True(rig.Queue.TrySetSocial(1, 10, SocialFlags.Friend));
        Assert.True(rig.Queue.TrySetSocial(2, 10, SocialFlags.Friend));
        Assert.True(rig.Queue.TrySetSocial(3, 10, SocialFlags.Friend));
        Assert.False(rig.Queue.TrySetSocial(4, 10, SocialFlags.Friend));
        Assert.True(rig.Queue.TrySetSocial(3, 10, SocialFlags.Ignored));
        rig.ReleaseConsumer();
        await rig.Queue.FlushAsync().WaitAsync(Wait);
        Assert.Equal(SocialFlags.Ignored, rig.Store.Row(3, 10));
        Assert.Equal(SocialFlags.None, rig.Store.Row(4, 10));
    }

    [Fact]
    public async Task DefaultLimits_AreFarAboveWhatALegitimatePlayerReaches()
    {
        var options = new SocialWriteQueueOptions();
        // vmangos list limits are 50 friends + 25 ignores; a player replacing both whole lists at once stays well inside.
        Assert.True(options.MaxPendingPerCharacter >= 2 * (PlayerSocial.FriendLimit + PlayerSocial.IgnoreLimit));
        Assert.True(options.MaxPendingTotal >= 10 * options.MaxPendingPerCharacter);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WriteAfterStop_IsRefused()
    {
        await using Rig rig = Rig.Create();
        await rig.Queue.StopAsync();
        Assert.False(rig.Queue.TrySetSocial(1, 2, SocialFlags.Friend));
        Assert.Equal(0, rig.Queue.PendingRows);
    }

    [Fact]
    public async Task Configuration_BindsFromTheSocialWriteQueueSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["World:Social:WriteQueue:MaxPendingPerCharacter"] = "7",
                ["World:Social:WriteQueue:MaxPendingTotal"] = "99",
            }).Build();
        var directory = new ArcaneCore.World.Characters.CharacterDirectory();
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var world = new ArcaneCore.Game.Maps.WorldRuntime(
            new ArcaneCore.Game.Maps.WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NoopQueue(), NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);
        await using var feature = new SocialFeature(directory, provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance, configuration);
        feature.Attach(world);
        Assert.Equal(7, feature.WriteQueueOptions.MaxPendingPerCharacter);
        Assert.Equal(99, feature.WriteQueueOptions.MaxPendingTotal);
        world.Stop();
        await feature.StopAsync();
    }

    private static GuildData Guild(int id, int tag) => new(
        id, $"G{id}", 1, $"motd{tag}", "", 0, 0, 0, 0, 0, 0,
        [new GuildRankData(0, "Leader", 0xFF)], [new GuildMemberData(1, 0, "", "", 1, 0, 0)]);

    private sealed class NoopQueue : ArcaneCore.Game.Maps.ICharacterSaveQueue
    {
        public void Enqueue(ArcaneCore.Kernel.Characters.CharacterState state)
        {
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Rig(RecordingStore store, SocialWriteQueueOptions options)
        {
            Store = store;
            _services = new ServiceCollection().AddScoped<ISocialStore>(_ => store).BuildServiceProvider();
            Queue = new SocialWriteQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, options);
            Queue.Start();
        }

        public RecordingStore Store { get; }

        public SocialWriteQueue Queue { get; }

        public static Rig Create(SocialWriteQueueOptions? options = null)
            => new(new RecordingStore(), options ?? new SocialWriteQueueOptions { RetryDelayMs = 1 });

        /// <summary>Park the consumer inside a write of an unrelated row (character 99), so everything after queues up untouched.</summary>
        public async Task HoldConsumerAsync()
        {
            Store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.SetSocial(99, 99, SocialFlags.Friend);
            await Store.GateEntered.Task.WaitAsync(Wait);
        }

        public void ReleaseConsumer() => Store.Gate?.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            ReleaseConsumer();
            try
            {
                await Queue.StopAsync().WaitAsync(Wait);
            }
            catch (InvalidOperationException)
            {
                // a test that leaves a failure retained says so itself
            }

            await _services.DisposeAsync();
        }
    }
}

/// <summary>A store that applies every call to in-memory tables exactly as <c>EfSocialStore</c> does, and can be gated or made to fail.</summary>
internal sealed class RecordingStore : ISocialStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(int, int), SocialFlags> _rows = [];
    private readonly Dictionary<(int, int), int> _setCalls = [];
    private readonly Dictionary<int, GuildData> _guilds = [];
    private int _failures;

    /// <summary>When set, a write of row (99, 99) waits for it (and announces itself on <see cref="GateEntered"/>).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource GateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Make the next <paramref name="count"/> calls of any write throw; int.MaxValue: until <see cref="Heal"/>.</summary>
    public void FailNext(int count)
    {
        lock (_lock)
        {
            _failures = count;
        }
    }

    public void Heal() => FailNext(0);

    public int Attempts { get; private set; }

    public IReadOnlyDictionary<(int, int), SocialFlags> Rows
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<(int, int), SocialFlags>(_rows);
            }
        }
    }

    public IReadOnlyDictionary<int, GuildData> Guilds
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<int, GuildData>(_guilds);
            }
        }
    }

    public SocialFlags Row(int c, int o)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault((c, o));
        }
    }

    public int SetCalls(int c, int o)
    {
        lock (_lock)
        {
            return _setCalls.GetValueOrDefault((c, o));
        }
    }

    public async Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
    {
        if (characterId == 99 && otherId == 99 && Gate is { } gate)
        {
            GateEntered.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        lock (_lock)
        {
            Attempts++;
            ThrowIfFailing();
            _setCalls[(characterId, otherId)] = _setCalls.GetValueOrDefault((characterId, otherId)) + 1;
            if (flags == SocialFlags.None)
            {
                _rows.Remove((characterId, otherId));
            }
            else
            {
                _rows[(characterId, otherId)] = flags;
            }
        }
    }

    public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Attempts++;
            ThrowIfFailing();
            _guilds[guild.Id] = guild;
        }

        return Task.CompletedTask;
    }

    public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Attempts++;
            ThrowIfFailing();
            _guilds.Remove(guildId);
        }

        return Task.CompletedTask;
    }

    public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Attempts++;
            ThrowIfFailing();
            foreach ((int, int) key in _rows.Keys.Where(k => k.Item1 == characterId || k.Item2 == characterId).ToArray())
            {
                _rows.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<SocialEntry>>(
                [.. _rows.Where(r => r.Key.Item1 == characterId).OrderBy(r => r.Key.Item2).Select(r => new SocialEntry(r.Key.Item2, r.Value))]);
        }
    }

    public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GuildData>>([]);

    private void ThrowIfFailing()
    {
        if (_failures > 0)
        {
            if (_failures != int.MaxValue)
            {
                _failures--;
            }

            throw new IOException("injected social store failure");
        }
    }
}
