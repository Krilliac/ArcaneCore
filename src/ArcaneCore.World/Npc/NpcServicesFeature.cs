using System.Runtime.CompilerServices;
using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Items;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Wires the NPC services' collaborators in the daemon: inventory-backed vendors, buyback,
/// repair and bank access (<see cref="InventoryItemService"/>), trainer learning over the spell
/// system (<see cref="SpellSystemLearner"/>), spirit-healer resurrection
/// (<see cref="SpiritHealerResurrection"/>), map properties, and taxi flights
/// (<see cref="TaxiFlightSystem"/>, updated by every map). Optional build-5875 tables and
/// creature metadata come from the "NpcServices" section (<see cref="NpcServiceOptions"/>).
/// Collaborators another feature already supplies are kept (<see cref="Extend"/> only fills gaps).
/// </summary>
public sealed class NpcServicesFeature(IServiceProvider services, ILogger<NpcServicesFeature> logger) : IWorldFeature, ICharacterHooks, IDisposable
{
    private readonly HashSet<Map> _maps = [];
    private readonly object _loadLock = new();
    private Tables? _tables;
    private WorldRuntime? _world;
    private InventoryItemService? _items;
    private readonly ConditionalWeakTable<Player, TaxiFlightRoute> _stagedFlights = new();
    // Guids whose character_taxi_flight row may exist, so a landing does not issue a blocking delete on the map thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _persistedRoutes = new();

    public NpcServiceOptions Options { get; } = new();

    /// <summary>The flight system of the current NPC content (replaced when the quest feature rebuilds its services).</summary>
    public TaxiFlightSystem? Flights { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        EnsureLoaded();
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        world.PlayerLoggedIn += OnPlayerLoggedIn;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
    }

    public void Dispose()
    {
        if (_world is { } world)
        {
            world.MapCreated -= OnMapCreated;
            world.MapUnloading -= OnMapUnloading;
            world.PlayerLoggedIn -= OnPlayerLoggedIn;
            world.PlayerLoggingOut -= OnPlayerLoggingOut;
        }

        _maps.Clear();
        if (Flights is { } flights)
        {
            flights.FlightEnded -= OnFlightEnded;
            flights.Landed -= OnFlightLanded;
        }
    }

    /// <summary>
    /// Fill the collaborators <paramref name="dependencies"/> lacks for <paramref name="npcs"/>
    /// (called by <see cref="QuestNpcFeature"/> whenever it builds its services).
    /// </summary>
    public QuestNpcDependencies Extend(QuestNpcDependencies dependencies, NpcStore npcs)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(npcs);
        Tables tables = EnsureLoaded();
        _items = new InventoryItemService(
            () => services.GetService<ItemsFeature>()?.Templates ?? ItemTemplateStore.Empty,
            tables.Repair, tables.BankSlots, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        IItemService items = dependencies.Items ?? _items;
        if (Flights is { } previous)
        {
            previous.FlightEnded -= OnFlightEnded;
            previous.Landed -= OnFlightLanded;
        }

        Flights = new TaxiFlightSystem(npcs, tables.PathNodes, MountDisplay, () => _world?.NowMs ?? 0, logger,
            player => services.GetService<SpellFeature>()?.System.RemoveAurasByType(player, AuraType.Mounted));
        Flights.FlightEnded += OnFlightEnded;
        Flights.Landed += OnFlightLanded;
        return dependencies with
        {
            Creatures = dependencies.Creatures is { } creatures && Options.NpcTemplates.Count > 0
                ? new NpcTemplateMetadataLookup(creatures, Options.NpcTemplates)
                : dependencies.Creatures,
            Items = items,
            Spells = dependencies.Spells ?? new SpellSystemLearner(() => services.GetService<SpellFeature>()?.System, tables.Abilities,
                () => services.GetService<Skills.SkillsFeature>() is { IsActive: true } skills ? skills.Catalog : null),
            Reputation = dependencies.Reputation ?? services.GetService<IPlayerReputation>(),
            Conditions = dependencies.Conditions ?? services.GetService<ConditionFeature>(),
            Flights = dependencies.Flights ?? Flights,
            Maps = dependencies.Maps ?? new WorldMapInfo(() => _world),
            Resurrection = dependencies.Resurrection
                ?? new SpiritHealerResurrection(() => services.GetService<SpellFeature>()?.System, items, p => _world?.SavePlayer(p)),
        };
    }

    private uint MountDisplay(uint creatureEntry)
        => services.GetService<CreatureWorldFeature>()?.Content.FindTemplate(creatureEntry)?.DisplayIds.FirstOrDefault(d => d != 0) ?? 0;

    private Tables EnsureLoaded()
    {
        lock (_loadLock)
        {
            if (_tables is { } loaded)
            {
                return loaded;
            }

            services.GetService<IConfiguration>()?.GetSection(NpcServiceOptions.SectionName).Bind(Options);
            var tables = new Tables(
                services.GetService<TaxiPathNodeCatalog>() ?? (Has(Options.TaxiPathNodeDbcPath)
                    ? NpcServiceDbcReaders.LoadTaxiPathNodes(Options.TaxiPathNodeDbcPath!) : TaxiPathNodeCatalog.Empty),
                services.GetService<SkillLineAbilityCatalog>() ?? (Has(Options.SkillLineAbilityDbcPath)
                    ? NpcServiceDbcReaders.LoadSkillLineAbilities(Options.SkillLineAbilityDbcPath!) : SkillLineAbilityCatalog.Empty),
                services.GetService<RepairCostTable>() ?? (Has(Options.DurabilityCostsDbcPath) && Has(Options.DurabilityQualityDbcPath)
                    ? NpcServiceDbcReaders.LoadRepairCosts(Options.DurabilityCostsDbcPath!, Options.DurabilityQualityDbcPath!) : RepairCostTable.Empty),
                services.GetService<BankBagSlotPriceTable>() ?? (Has(Options.BankBagSlotPricesDbcPath)
                    ? NpcServiceDbcReaders.LoadBankBagSlotPrices(Options.BankBagSlotPricesDbcPath!) : BankBagSlotPriceTable.Empty));
            _tables = tables;
            logger.LogInformation(
                "NPC services: {Paths} flight paths with waypoints, {Abilities} skill line abilities, repair prices {Repair}",
                tables.PathNodes.PathCount, tables.Abilities.Count, tables.Repair.IsEmpty ? "absent" : "loaded");
            return tables;
        }
    }

    private static bool Has(string? path) => !string.IsNullOrWhiteSpace(path);

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            map.AddUpdater(new FlightUpdater(this));
        }
    }

    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        if (session.Services.GetService<ICharacterTaxiFlightStore>() is { } store
            && await store.LoadAsync(character.Id).ConfigureAwait(false) is { } route)
        {
            // A route whose departure node vanished from the data is cleared at login rather than locking the character out.
            _persistedRoutes[(uint)player.Guid.Low] = 0;
            _stagedFlights.Add(player, route);
        }
    }

    private void OnPlayerLoggedIn(Player player)
    {
        _items?.SessionStarted(player);
        if (!_stagedFlights.TryGetValue(player, out TaxiFlightRoute? route))
        {
            return;
        }

        _stagedFlights.Remove(player);
        NpcStore? npcs = services.GetService<QuestNpcFeature>()?.Services.Npcs;
        TaxiNode? source = npcs?.Node(route.Nodes[0]);
        TaxiNode? destination = npcs?.Node(route.Nodes[^1]);
        if (destination is not null && destination.MapId == player.MapId
            && Math.Abs(player.X - destination.X) < 0.5f
            && Math.Abs(player.Y - destination.Y) < 0.5f
            && Math.Abs(player.Z - destination.Z) < 0.5f)
        {
            ClearSavedRoute(player);
            return;
        }

        uint mount = player.Team == Team.Alliance ? source?.MountAlliance ?? 0 : source?.MountHorde ?? 0;
        if (player.IsAlive && mount != 0 && Flights?.ResumeFlight(player, route, mount,
            (p, cost) => services.GetService<QuestNpcFeature>()?.Services.TryCharge(p, cost) == true) == true)
        {
            return;
        }

        // A route whose DBC data disappeared cannot leave the player stranded in midair.
        if (source is not null && source.MapId == player.MapId)
        {
            player.Relocate(source.X, source.Y, source.Z, player.Orientation, _world?.NowMs ?? 0);
            _world?.SavePlayer(player);
        }

        ClearSavedRoute(player);
    }

    private void OnMapUnloading(Map map) => _maps.Remove(map);

    private void OnPlayerLoggingOut(Player player)
    {
        if (Flights?.SuspendForLogout(player) is { } route)
        {
            bool saved = true;
            try
            {
                using IServiceScope scope = services.CreateScope();
                scope.ServiceProvider.GetRequiredService<ICharacterTaxiFlightStore>()
                    .SaveAsync((int)player.Guid.Low, route).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not save taxi route for character {Character}; returning to the departure node", player.Guid.Low);
                _persistedRoutes[(uint)player.Guid.Low] = 0;
                TaxiNode? start = services.GetService<QuestNpcFeature>()?.Services.Npcs.Node(route.Nodes[0]);
                if (start is not null && start.MapId == player.MapId)
                {
                    player.Relocate(start.X, start.Y, start.Z, player.Orientation, _world?.NowMs ?? 0);
                }

                ClearSavedRoute(player);
                saved = false;
            }

            if (saved)
            {
                _persistedRoutes.TryRemove((uint)player.Guid.Low, out _);
            }
        }

        _items?.SessionEnded(player);
    }

    private void OnFlightLanded(Player player, uint destination) => _world?.SavePlayer(player);

    private void OnFlightEnded(Player player) => ClearSavedRoute(player);

    private void ClearSavedRoute(Player player)
    {
        if (!_persistedRoutes.TryRemove((uint)player.Guid.Low, out _))
        {
            return;
        }

        try
        {
            using IServiceScope scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ICharacterTaxiFlightStore>()
                .DeleteAsync((int)player.Guid.Low).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clear taxi route for character {Character}", player.Guid.Low);
        }
    }

    private sealed record Tables(TaxiPathNodeCatalog PathNodes, SkillLineAbilityCatalog Abilities, RepairCostTable Repair, BankBagSlotPriceTable BankSlots);

    /// <summary>Forwards each map's update to the current flight system.</summary>
    private sealed class FlightUpdater(NpcServicesFeature feature) : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => feature.Flights?.Update(map, diffMs);

        public void OnPlayerRemoved(Map map, Player player) => feature.Flights?.OnPlayerRemoved(map, player);
    }

    /// <summary>
    /// Map properties: vmangos MapEntry::Instanceable from the map registry (an unknown map falls back to
    /// "everything but the continents"); area ids come from the map's terrain.
    /// </summary>
    private sealed class WorldMapInfo(Func<WorldRuntime?> world) : IMapInfo
    {
        public bool IsInstanceable(uint mapId) => world() is not { } w
            ? mapId is not (0 or 1)
            : Game.Maps.Templates.WorldMaps.Of(w).Registry.Find(mapId)?.Instanceable ?? mapId is not (0 or 1);

        public uint GetAreaId(uint mapId, float x, float y, float z)
            => world()?.Maps.FirstOrDefault(m => m.MapId == mapId)?.GetZoneAndAreaId(x, y, z).AreaId ?? 0;
    }
}
