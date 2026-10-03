using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Fishing;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The special loot sources of the world daemon (an <see cref="IWorldFeature"/>, discovered): fishing and Pick Pocket. It hangs on the maps' game object systems
/// of <see cref="GameObjectLootFeature"/> (which stays untouched) and on the spell system of <see cref="SpellFeature"/>:
/// per map a <see cref="FishingService"/> (bobber timers, the click, holes), the use handler of fishing bobbers and a <see cref="PickpocketLoot"/>,
/// and once per spell system the TRANS_DOOR effect of the fishing spells and the Pick Pocket check and effect. Missing collaborators (no spell feature, no game object feature) leave fishing off, never half on.
/// Options come from the <c>SpecialLoot</c> section (<see cref="SpecialLootOptions"/>); all defaults are the retail behaviour.
/// </summary>
public sealed class SpecialLootFeature(IServiceProvider services, ILogger<SpecialLootFeature> logger) : IWorldFeature
{
    private readonly Dictionary<Map, FishingService> _fishing = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Map, PickpocketLoot> _pickpockets = new(ReferenceEqualityComparer.Instance);
    private WorldRuntime? _world;

    public SpecialLootOptions Options { get; } = new();

    /// <summary>The fishing service of this exact map instance (world thread), or null.</summary>
    public FishingService? FindFishing(Map map) => _fishing.GetValueOrDefault(map);

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        services.GetService<IConfiguration>()?.GetSection(SpecialLootOptions.SectionName).Bind(Options);
        // Posted after GameObjectLootFeature's own install (it attaches first: type names order), so its map systems exist when ours run.
        world.Post(Install);
    }

    private void Install()
    {
        WorldRuntime world = _world!;
        if (services.GetService<GameObjectLootFeature>() is null || services.GetService<SpellFeature>() is not { } spells)
        {
            logger.LogInformation("Special loot: the game object or spell feature is missing, fishing is off");
            return;
        }

        // Fishing needs the "fishing pole equipped" requirement; it is the generic equipped-item rule of every spell with one.
        EquippedItemCastCheck.Install(spells.System);
        new FishingSpells(() => _fishing.Values).Register(spells.System);
        new PickpocketSpells(map => _pickpockets.GetValueOrDefault(map)).Register(spells.System);
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps.ToArray())
        {
            OnMapCreated(map);
        }
    }

    private void OnMapCreated(Map map)
    {
        if (_fishing.ContainsKey(map) || services.GetService<GameObjectLootFeature>()?.GetOrCreateSystem(map) is not { } objects)
        {
            return;
        }

        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        var fishing = new FishingService(map, objects, Options.Fishing, () => spells.System, new Random(), services.GetService<IFishingTerrain>());
        map.AddUpdater(fishing);
        objects.RegisterUseHandler(GameObjectType.FishingNode, fishing.UseBobber);
        _fishing.Add(map, fishing);

        if (objects.Loot is { } loot)
        {
            var pockets = new PickpocketLoot(loot);
            map.Combat.UnitKilled += pockets.OnCreatureKilled;
            _pickpockets.Add(map, pockets);
        }
    }

    private void OnMapUnloading(Map map)
    {
        _fishing.Remove(map);
        if (_pickpockets.Remove(map, out PickpocketLoot? pockets))
        {
            map.Combat.UnitKilled -= pockets.OnCreatureKilled;
        }
    }
}