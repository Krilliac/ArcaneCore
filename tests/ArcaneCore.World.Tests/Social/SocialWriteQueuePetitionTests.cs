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
