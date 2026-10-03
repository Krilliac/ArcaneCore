using ArcaneCore.Kernel.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Social;

/// <summary>An in-memory <see cref="ISocialStore"/> for the end-to-end host (thread-safe).</summary>
internal sealed class InMemorySocialStore : ISocialStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(int Character, int Other), SocialFlags> _social = [];
    private readonly Dictionary<int, GuildData> _guilds = [];

    public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<SocialEntry> entries = [.. _social.Where(e => e.Key.Character == characterId).Select(e => new SocialEntry(e.Key.Other, e.Value))];
            return Task.FromResult(entries);
        }
    }

    public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (flags == SocialFlags.None)
            {
                _social.Remove((characterId, otherId));
            }
            else
            {
                _social[(characterId, otherId)] = flags;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<GuildData> guilds = [.. _guilds.Values];
            return Task.FromResult(guilds);
        }
    }

    public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _guilds[guild.Id] = guild;
        }

        return Task.CompletedTask;
    }

    public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _guilds.Remove(guildId);
        }

        return Task.CompletedTask;
    }

    public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            foreach ((int Character, int Other) key in _social.Keys.Where(k => k.Character == characterId || k.Other == characterId).ToArray())
            {
                _social.Remove(key);
            }

            foreach (GuildData guild in _guilds.Values.ToArray())
            {
                _guilds[guild.Id] = guild with { Members = [.. guild.Members.Where(m => m.CharacterId != characterId)] };
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Registers <see cref="InMemorySocialStore"/> in every <see cref="WorldTestHost"/>.</summary>
internal sealed class SocialTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services) => services.AddSingleton<ISocialStore, InMemorySocialStore>();
}
