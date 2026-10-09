using ArcaneCore.Kernel.WorldData;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.GridTerrain;

/// <summary>Static world-database map content for the end-to-end teleport tests.</summary>
internal sealed class InMemoryMapDataStore : IMapDataStore
{
    public const uint DeadminesTrigger = 78;
    public const uint ShortcutTrigger = 79;
    public const uint OrphanTeleport = 80;

    public static MapContent Content { get; } = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(36, 0, MapType.Instance, 0, 10, 0, 0, -11208.4f, 1672.3f, "Deadmines", ""),
        ],
        [new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn Forest", 2, 0)],
        [
            // A 10 × 10 × 10 box just west of the human start (-8949.95, -132.493, 83.5312).
            new AreaTriggerTemplate(DeadminesTrigger, 0, -8960f, -132.5f, 83.5f, 0, 10, 10, 10, 0, "Test dungeon entrance"),
            // A 3-yard sphere just east of it.
            new AreaTriggerTemplate(ShortcutTrigger, 0, -8940f, -132.5f, 83.5f, 3, 0, 0, 0, 0, "Test shortcut"),
        ],
        [
            new AreaTriggerTeleport(DeadminesTrigger, "Deadmines Entrance", "", 10, 36, -16.4f, -383.07f, 61.78f, 1.86f),
            new AreaTriggerTeleport(ShortcutTrigger, "Shortcut", "", 0, 0, -8913.23f, 554.633f, 93.7944f, 0.5f),
            new AreaTriggerTeleport(OrphanTeleport, "No trigger row", "", 0, 0, 1, 1, 1, 0), // skipped at load
        ],
        [
            new GameTele(1, -8913.23f, 554.633f, 93.7944f, 0f, 0, "Stormwind"),
            new GameTele(2, -441.8f, -2596.08f, 96.2155f, 1f, 1, "Crossroads"),
            new GameTele(3, 1f, 1f, 1f, 0f, 99, "Nowhere"),
        ]);

    public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Content);
}

/// <summary>Registers <see cref="InMemoryMapDataStore"/> in every <see cref="WorldTestHost"/>.</summary>
internal sealed class MapTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore, InMemoryMapDataStore>();
        services.AddSingleton<IGameTeleStore, InMemoryGameTeleStore>();
    }
}

internal sealed class InMemoryGameTeleStore : IGameTeleStore
{
    private readonly List<GameTele> _rows = [.. InMemoryMapDataStore.Content.GameTeles];

    public Task<GameTele?> AddAsync(GameTele location, CancellationToken cancellationToken = default)
    {
        lock (_rows)
        {
            if (_rows.Any(t => string.Equals(t.Name, location.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult<GameTele?>(null);
            }

            GameTele added = location with { Id = _rows.Max(t => t.Id) + 1 };
            _rows.Add(added);
            return Task.FromResult<GameTele?>(added);
        }
    }

    public Task<GameTele?> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_rows)
        {
            GameTele? row = _rows.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                _rows.Remove(row);
            }

            return Task.FromResult(row);
        }
    }
}
