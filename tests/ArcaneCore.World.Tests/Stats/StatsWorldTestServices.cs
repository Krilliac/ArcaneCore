using ArcaneCore.Kernel.WorldData.PlayerStats;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Stats;

/// <summary>
/// The player base data double for <see cref="WorldTestHost"/>. Registered only for hosts started inside
/// <see cref="StatsTestContent.Use"/>, so every other test keeps running without a content store (the stats
/// feature then leaves the progression configuration alone).
/// </summary>
internal sealed class StatsWorldTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        if (StatsTestContent.Current is { } content)
        {
            services.AddSingleton<IPlayerStatsContentStore>(content);
        }
    }
}

internal sealed class StatsTestContent(PlayerStatsContent content) : IPlayerStatsContentStore
{
    private static readonly AsyncLocal<StatsTestContent?> Ambient = new();

    public static StatsTestContent? Current => Ambient.Value;

    public int Loads { get; private set; }

    public IDisposable Use()
    {
        Ambient.Value = this;
        return new Scope();
    }

    public Task<PlayerStatsContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        Loads++;
        return Task.FromResult(content);
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }
}
