using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>Petition writes travel the one ordered social write queue, after the guild writes before them.</summary>
public sealed class SocialWriteQueuePetitionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static GuildData Guild(int id) => new(id, "G" + id, 1, "m", "i", 0, -1, -1, -1, -1, -1, [], []);

    [Fact]
    public async Task GuildAndPetitionWrites_AreDeliveredInOrder_AndFlushWaitsForAll()
    {
        var log = new List<string>();
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<ISocialStore>(_ => new RecordingSocialStore(log))
            .AddScoped<IPetitionStore>(_ => new RecordingPetitionStore(log))
            .BuildServiceProvider();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();

        queue.SaveGuild(Guild(1));
        queue.SavePetition(new PetitionData(5, 1, 70, "Arcane", [new PetitionSignatureData(2, 2)]));
        queue.CompletePetition(Guild(2), 5);
        queue.DeletePetition(6);
        queue.PurgeCharacter(9);
        await queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(["social:SaveGuild:1", "petition:Save:5:1", "petition:Complete:2:5", "petition:Delete:6", "social:Purge:9", "petition:Purge:9"], log);
        Assert.Equal(0, queue.Pending);
        await queue.StopAsync();
    }

    [Fact]
    public async Task WithoutAPetitionStore_PetitionWritesAreSkipped_AndFlushStillCompletes()
    {
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();

        queue.SavePetition(new PetitionData(1, 1, 1, "x", []));
        queue.DeletePetition(1);
        await queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal(0, queue.Pending);
        await queue.StopAsync();
    }

    // --- completion ordering (wave 3 review F1): the completion rides the guild's key, never a stale snapshot ---

    [Fact]
    public async Task GuildChange_AfterCompletePetition_IsNotOverwrittenByTheCompletionSnapshot()
    {
        var store = new StatefulStores();
        await using ServiceProvider services = store.Build();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        store.Petitions[5] = new PetitionData(5, 1, 70, "Arcane", []);

        queue.SetSocial(1, 2, SocialFlags.Friend); // parks the consumer, so everything below is still queued
        await store.Parked.Task.WaitAsync(Wait);
        queue.SaveGuild(Guild(1));
        queue.CompletePetition(Guild(1), 5);
        queue.SaveGuild(Guild(1) with { Motd = "newer" });
        store.Release.SetResult();
        await queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal("newer", store.Guilds[1].Motd);
        Assert.False(store.Petitions.ContainsKey(5)); // the petition was still consumed
        await queue.StopAsync();
    }

    [Fact]
    public async Task DisbandingAGuild_AfterCompletePetition_IsNotUndone()
    {
        var store = new StatefulStores();
        await using ServiceProvider services = store.Build();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        store.Petitions[5] = new PetitionData(5, 1, 70, "Arcane", []);

        queue.SetSocial(1, 2, SocialFlags.Friend);
        await store.Parked.Task.WaitAsync(Wait);
        queue.SaveGuild(Guild(1));
        queue.CompletePetition(Guild(1), 5);
        queue.DeleteGuild(1);
        store.Release.SetResult();
        await queue.FlushAsync().WaitAsync(Wait);

        Assert.False(store.Guilds.ContainsKey(1)); // not recreated from the completion snapshot
        Assert.False(store.Petitions.ContainsKey(5));
        await queue.StopAsync();
    }

    [Fact]
    public async Task RetainedCompletion_IsNotReplayedOverANewerGuildWrite_AndTheNewerWriteStillConsumesThePetition()
    {
        var store = new StatefulStores { FailCompleteAttempts = 3 }; // exactly one write's worth of attempts
        await using ServiceProvider services = store.Build();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, new SocialWriteQueueOptions { RetryDelayMs = 0 });
        queue.Start();
        store.Petitions[5] = new PetitionData(5, 1, 70, "Arcane", []);

        queue.SaveGuild(Guild(1));
        queue.CompletePetition(Guild(1), 5);
        await queue.FlushAsync().WaitAsync(Wait); // the completion failed all attempts and is retained
        Assert.True(store.Petitions.ContainsKey(5));

        queue.SaveGuild(Guild(1) with { Motd = "newer" });
        await queue.FlushAsync().WaitAsync(Wait);

        Assert.Equal("newer", store.Guilds[1].Motd);      // the retained (older) snapshot never ran after it
        Assert.False(store.Petitions.ContainsKey(5));     // and the petition is still deleted
        await queue.StopAsync();
    }

    [Fact]
    public async Task PurgeCharacter_DiscardsTheRetainedPetitionOfThatCharacter()
    {
        var store = new StatefulStores { FailPetitionSaves = 3 };
        await using ServiceProvider services = store.Build();
        var queue = new SocialWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, new SocialWriteQueueOptions { RetryDelayMs = 0 });
        queue.Start();

        queue.SavePetition(new PetitionData(7, 9, 70, "Gone", []));
        await queue.FlushAsync().WaitAsync(Wait); // failed all attempts, retained
        queue.PurgeCharacter(9);
        await queue.FlushAsync().WaitAsync(Wait); // the purge succeeds, which retries whatever is still retained

        Assert.False(store.Petitions.ContainsKey(7));
        await queue.StopAsync(); // nothing retained is left: no throw
    }

    private sealed class StatefulStores
    {
        public Dictionary<int, GuildData> Guilds { get; } = [];

        public Dictionary<int, PetitionData> Petitions { get; } = [];

        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FailCompleteAttempts { get; set; }

        public int FailPetitionSaves { get; set; }

        public ServiceProvider Build() => new ServiceCollection()
            .AddScoped<ISocialStore>(_ => new Social(this))
            .AddScoped<IPetitionStore>(_ => new Petition(this))
            .BuildServiceProvider();

        private sealed class Social(StatefulStores s) : ISocialStore
        {
            public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

            public async Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
            {
                s.Parked.TrySetResult();
                await s.Release.Task;
            }

            public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([.. s.Guilds.Values]);

            public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
            {
                s.Guilds[guild.Id] = guild;
                return Task.CompletedTask;
            }

            public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default)
            {
                s.Guilds.Remove(guildId);
                return Task.CompletedTask;
            }

            public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class Petition(StatefulStores s) : IPetitionStore
        {
            public Task<IReadOnlyList<PetitionData>> GetPetitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PetitionData>>([.. s.Petitions.Values]);

            public Task SavePetitionAsync(PetitionData petition, CancellationToken cancellationToken = default)
            {
                if (s.FailPetitionSaves > 0)
                {
                    s.FailPetitionSaves--;
                    throw new InvalidOperationException("petition store down");
                }

                s.Petitions[petition.Id] = petition;
                return Task.CompletedTask;
            }

            public Task DeletePetitionAsync(int petitionId, CancellationToken cancellationToken = default)
            {
                s.Petitions.Remove(petitionId);
                return Task.CompletedTask;
            }

            public Task CompletePetitionAsync(GuildData guild, int petitionId, CancellationToken cancellationToken = default)
            {
                if (s.FailCompleteAttempts > 0)
                {
                    s.FailCompleteAttempts--;
                    throw new InvalidOperationException("petition store down");
                }

                s.Guilds[guild.Id] = guild;
                s.Petitions.Remove(petitionId);
                return Task.CompletedTask;
            }

            public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    private sealed class RecordingSocialStore(List<string> log) : ISocialStore
    {
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
        {
            log.Add("social:SaveGuild:" + guild.Id);
            return Task.CompletedTask;
        }

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        {
            log.Add("social:Purge:" + characterId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPetitionStore(List<string> log) : IPetitionStore
    {
        public Task<IReadOnlyList<PetitionData>> GetPetitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PetitionData>>([]);

        public Task SavePetitionAsync(PetitionData petition, CancellationToken cancellationToken = default)
        {
            log.Add($"petition:Save:{petition.Id}:{petition.Signatures.Count}");
            return Task.CompletedTask;
        }

        public Task DeletePetitionAsync(int petitionId, CancellationToken cancellationToken = default)
        {
            log.Add("petition:Delete:" + petitionId);
            return Task.CompletedTask;
        }

        public Task CompletePetitionAsync(GuildData guild, int petitionId, CancellationToken cancellationToken = default)
        {
            log.Add($"petition:Complete:{guild.Id}:{petitionId}");
            return Task.CompletedTask;
        }

        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        {
            log.Add("petition:Purge:" + characterId);
            return Task.CompletedTask;
        }
    }
}
