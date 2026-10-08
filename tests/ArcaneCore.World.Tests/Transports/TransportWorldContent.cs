using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Tests.Duel;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Transports;

/// <summary>
/// Synthetic ship content for world tests (there is no client TaxiPathNode.dbc on this machine): two routes starting at the
/// human start (-8949.95, -132.49, 83.53) on map 0, speed 10 yd/s, acceleration 1 yd/s², straight along +x.
/// <list type="bullet">
/// <item><see cref="Ferry"/>: waits <see cref="FirstStopSeconds"/> at the start, then sails 300 yd east to a second stop.</item>
/// <item><see cref="Crossing"/>: waits <see cref="FirstStopSeconds"/> 50 yd north of the start, sails 200 yd east and changes to map 1
/// at (1100, 0, 83.53), where it waits 5 s and sails on.</item>
/// <item><see cref="Broken"/>: a type 15 row whose path does not exist (refused, the server starts anyway).</item>
/// </list>
/// The game object content also has the Duel Flag (21680) so ship duels can be fought.
/// </summary>
internal static class TransportWorldContent
{
    public const uint Ferry = 990600;
    public const uint Crossing = 990601;
    public const uint Broken = 990602;
    public const uint FerryPath = 9101;
    public const uint CrossingPath = 9102;
    public const uint FirstStopSeconds = 60;
    public const float StartX = -8949.95f;
    public const float StartY = -132.49f;
    public const float StartZ = 83.53f;
    public const float CrossingY = StartY + 50f;
    public const float FarX = 1100f;
    public const uint FerryPeriodOverride = 123_456;

    public static void Register(IServiceCollection services, bool enabled = true, bool withPeriodOverride = false)
    {
        services.AddSingleton(new TransportOptions { Enabled = enabled });
        services.AddSingleton(Paths());
        services.AddScoped<IGameObjectDataStore>(_ => new Store());
        if (withPeriodOverride)
        {
            services.AddScoped<ITransportDataStore>(_ => new PeriodStore());
        }
    }

    public static TaxiPathNodeCatalog Paths() => new(
    [
        Node(FerryPath, 0, 0, StartX - 100, StartY),
        Node(FerryPath, 1, 0, StartX, StartY, flags: 2, delay: FirstStopSeconds),
        Node(FerryPath, 2, 0, StartX + 100, StartY),
        Node(FerryPath, 3, 0, StartX + 200, StartY),
        Node(FerryPath, 4, 0, StartX + 300, StartY, flags: 2, delay: 10),
        Node(FerryPath, 5, 0, StartX + 400, StartY),

        Node(CrossingPath, 0, 0, StartX - 100, CrossingY),
        Node(CrossingPath, 1, 0, StartX, CrossingY, flags: 2, delay: FirstStopSeconds),
        Node(CrossingPath, 2, 0, StartX + 100, CrossingY),
        Node(CrossingPath, 3, 0, StartX + 200, CrossingY),
        Node(CrossingPath, 4, 0, StartX + 300, CrossingY),
        Node(CrossingPath, 5, 1, FarX - 100, 0),
        Node(CrossingPath, 6, 1, FarX, 0, flags: 2, delay: 5),
        Node(CrossingPath, 7, 1, FarX + 100, 0),
        Node(CrossingPath, 8, 1, FarX + 200, 0),
        Node(CrossingPath, 9, 1, FarX + 300, 0),
    ]);

    public static GameObjectTemplate Ship(uint entry, uint path)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = path;
        data[1] = 10;
        data[2] = 1;
        return new GameObjectTemplate
        {
            Entry = entry, Type = TransportTemplateBuilder.MoTransportType, DisplayId = 3031, Name = "Synthetic ship " + entry,
            Faction = 35, Flags = 0x28, Data = data,
        };
    }

    private static TaxiPathNodeRecord Node(uint path, uint index, uint map, float x, float y, uint flags = 0, uint delay = 0)
        => new(path * 100 + index, path, index, map, x, y, StartZ, flags, delay);

    private sealed class Store : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken)
        {
            var flag = new GameObjectTemplate
            {
                Entry = DuelWorldHost.FlagEntry, Type = (uint)GameObjectType.DuelArbiter, DisplayId = 787, Name = "Duel Flag",
                Data = new uint[GameObjectTemplate.DataCount],
            };
            return Task.FromResult(new GameObjectContent([flag, Ship(Ferry, FerryPath), Ship(Crossing, CrossingPath), Ship(Broken, 4242)], [], [], [], []));
        }
    }

    private sealed class PeriodStore : ITransportDataStore
    {
        public Task<IReadOnlyList<TransportPeriodRow>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TransportPeriodRow>>(
            [
                new TransportPeriodRow(Ferry, 0, "Synthetic ferry", 1),
                new TransportPeriodRow(Ferry, 4695, "Synthetic ferry", FerryPeriodOverride),
                new TransportPeriodRow(Ferry, 6005, "Synthetic ferry", 2), // a later client: ignored
                new TransportPeriodRow(777, 0, "Not a ship", 3),
            ]);
    }
}
