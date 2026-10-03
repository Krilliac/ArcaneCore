using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Items;

/// <summary>
/// Attaches <see cref="ItemMaintenanceUpdater"/> to every map: timed items tick down and
/// map/area-limited items are removed (see <see cref="ItemMaintenance"/>). Options come from
/// <see cref="ItemsFeature.Options"/>.
/// </summary>
public sealed class ItemMaintenanceFeature(ItemsFeature items) : IWorldFeature
{
    private readonly HashSet<Map> _maps = [];

    public void Attach(WorldRuntime world) => world.MapCreated += OnMapCreated;

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            map.AddUpdater(new ItemMaintenanceUpdater(items.Options, TimeProvider.System));
        }
    }
}
