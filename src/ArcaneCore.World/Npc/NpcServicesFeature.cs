using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    private ILogger<NpcServicesFeature> Logger => logger;
    private readonly HashSet<Map> _maps = [];
    private readonly object _loadLock = new();
    private Tables? _tables;
    private WorldRuntime? _world;
    private InventoryItemService? _items;
    private readonly ConditionalWeakTable<Player, TaxiFlightRoute> _stagedFlights = new();
    // Guids whose character_taxi_flight row may exist, so a landing does not issue a blocking delete on the map thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _persistedRoutes = new();
    private TaxiFlightWriteQueue? _routeWrites;
    private NpcStore? _npcs;

    public NpcServiceOptions Options { get; } = new();

    /// <summary>The flight system of the current NPC content (replaced when the quest feature rebuilds its services).</summary>
    public TaxiFlightSystem? Flights { get; private set; }

    /// <summary>The queue the logout save and the landing delete go through (tests); null before <see cref="Attach"/>.</summary>
    internal TaxiFlightWriteQueue? RouteWrites => _routeWrites;

    /// <summary>Drain and retry this character's taxi-flight route writes; faults if any remain undrained.</summary>
    public Task FlushCharacterAsync(int characterId) => _routeWrites?.FlushCharacterAsync(characterId) ?? Task.CompletedTask;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _routeWrites = new TaxiFlightWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), logger, services.GetService<ShutdownDrain>()?.Features);
        _routeWrites.FatalFailure += ex =>
        {
            FaultLedger.Shared.Record("npc-services.route-writes-fatal", ex);
            logger.LogCritical(ex, "Stopping application after fatal taxi flight write failure");
            try
            {
                services.GetService<IHostApplicationLifetime>()?.StopApplication();
            }
            catch (ObjectDisposedException disposedEx)
            {
                logger.LogDebug(disposedEx, "Host application lifetime was already disposed while stopping after fatal taxi flight write failure");
            }
            catch (Exception stopEx) when (stopEx is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("npc.services", stopEx);
                logger.LogFault("npc-services.stop-application", stopEx, "Stopping application after fatal taxi flight write failure failed");
            }
        };
        _routeWrites.Start();
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
        _npcs = npcs;
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
            Honor = dependencies.Honor ?? services.GetService<Honor.HonorFeature>()?.ActiveService,
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
                services.GetService<TaxiPathNodeCatalog>() ?? (TryGetPath(Options.TaxiPathNodeDbcPath, out string? taxiPath)
                    ? NpcServiceDbcReaders.LoadTaxiPathNodes(taxiPath) : TaxiPathNodeCatalog.Empty),
                services.GetService<SkillLineAbilityCatalog>() ?? (TryGetPath(Options.SkillLineAbilityDbcPath, out string? skillPath)
                    ? NpcServiceDbcReaders.LoadSkillLineAbilities(skillPath) : SkillLineAbilityCatalog.Empty),
                services.GetService<RepairCostTable>() ?? (TryGetPath(Options.DurabilityCostsDbcPath, out string? durabilityCostsPath) && TryGetPath(Options.DurabilityQualityDbcPath, out string? durabilityQualityPath)
                    ? NpcServiceDbcReaders.LoadRepairCosts(durabilityCostsPath, durabilityQualityPath) : RepairCostTable.Empty),
                services.GetService<BankBagSlotPriceTable>() ?? (TryGetPath(Options.BankBagSlotPricesDbcPath, out string? bankBagPath)
                    ? NpcServiceDbcReaders.LoadBankBagSlotPrices(bankBagPath) : BankBagSlotPriceTable.Empty));
            // Configured overrides of the imported creature_template service fields fail closed: an entry 0, an unknown trainer type,
            // a non-playable trainer class or a race above 8 refuses startup instead of silently refusing every trainee.
            foreach (NpcTemplateMetadata configured in Options.NpcTemplates)
            {
                ValidateMetadata(configured);
            }

            _tables = tables;
            logger.LogInformation(
                "NPC services: {Paths} flight paths with waypoints, {Abilities} skill line abilities, repair prices {Repair}",
                tables.PathNodes.PathCount, tables.Abilities.Count, tables.Repair.IsEmpty ? "absent" : "loaded");
            return tables;
        }
    }

    private static bool TryGetPath(string? path, [NotNullWhen(true)] out string? validPath)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            validPath = path;
            return true;
        }

        validPath = null;
        return false;
    }

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            map.AddUpdater(new FlightUpdater(this));
        }
    }

    /// <summary>Drain queued route writes on shutdown; throws, naming the characters, while a write is still not durable.</summary>
    public async Task StopAsync()
    {
        if (_routeWrites is not null)
        {
            await _routeWrites.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for earlier route writes (a logout save or a landing delete) and faults the login while this character's write is
    /// retained and still cannot be persisted, so a stale or missing stored route never becomes the live state.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        if (_routeWrites is not null)
        {
            await _routeWrites.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        }

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
        TaxiNode? source = Node(route.Nodes[0]);
        TaxiNode? destination = Node(route.Nodes[^1]);
        if (destination is not null && destination.MapId == player.MapId
            && Math.Abs(player.X - destination.X) < 0.5f
            && Math.Abs(player.Y - destination.Y) < 0.5f
            && Math.Abs(player.Z - destination.Z) < 0.5f)
        {
            ClearSavedRoute(player);
            return;
        }

        // ContinueTaxiFlight asks for the other faction's mount when this node's own is missing
        // (vmangos Player.cpp:18118 → GetTaxiMountDisplayId allowed_alt_team, ObjectMgr.cpp:7423-7434).
        // A flight-master takeoff does not; only a resumed route does.
        uint mount = 0;
        if (source is { } start)
        {
            bool alliance = player.Team == Team.Alliance;
            mount = alliance ? start.MountAlliance : start.MountHorde;
            if (mount == 0)
            {
                mount = alliance ? start.MountHorde : start.MountAlliance;
            }
        }
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
            // The queue owns the write from here. A save that cannot be persisted falls back to what the synchronous save
            // did: the character is put back at the leg's departure node and the route is cleared (ReturnToDeparture); with no
            // departure node to return to, the save is retained and the login barrier refuses a relog while it is not durable.
            _persistedRoutes.TryRemove((uint)player.Guid.Low, out _);
            _routeWrites?.Save((int)player.Guid.Low, route, ReturnToDeparture(player, route));
        }
        else if (_routeWrites is { } writes && writes.HasRetainedFailure((int)player.Guid.Low))
        {
            writes.RequestRetry((int)player.Guid.Low); // an early retry; the login barrier and shutdown still retry
        }

        _items?.SessionEnded(player);
    }

    /// <summary>
    /// The logout save's fallback (world thread, while the player is still in the world): a snapshot of the character at the
    /// departure node of the leg it was flying, queued behind the logout snapshot through the character save queue when the
    /// route could not be saved. Null when the node is unknown or on another map, so the player could not be put there.
    /// </summary>
    private Action? ReturnToDeparture(Player player, TaxiFlightRoute route)
    {
        if (services.GetService<ICharacterSaveQueue>() is not { } saves
            || Node(route.Nodes[0]) is not { } start || start.MapId != player.MapId)
        {
            return null;
        }

        int characterId = (int)player.Guid.Low;
        CharacterState atDeparture = player.CreateSnapshotAt(_world?.NowMs ?? 0, start.MapId, start.X, start.Y, start.Z);
        return () =>
        {
            logger.LogError("Could not save taxi route for character {Character}; returning to the departure node", characterId);
            saves.Enqueue(atDeparture);
        };
    }

    /// <summary>A taxi node of the current NPC content (the quest feature's store, or the one <see cref="Extend"/> received).</summary>
    private TaxiNode? Node(uint id) => (services.GetService<QuestNpcFeature>()?.Services.Npcs ?? _npcs)?.Node(id);

    private void OnFlightLanded(Player player, uint destination) => _world?.SavePlayer(player);

    private void OnFlightEnded(Player player) => ClearSavedRoute(player);

    private void ClearSavedRoute(Player player)
    {
        if (!_persistedRoutes.TryRemove((uint)player.Guid.Low, out _))
        {
            return;
        }

        _routeWrites?.Delete((int)player.Guid.Low);
    }

    private static void ValidateMetadata(NpcTemplateMetadata row)
    {
        if (row.Entry == 0 || !Enum.IsDefined(row.TrainerType)
            || !IsValidTrainerClass(row.TrainerClass) || row.TrainerRace > 8)
        {
            throw new InvalidDataException($"invalid configured NPC service metadata for entry {row.Entry}");
        }
    }

    /// <summary>0 (none) or a vanilla playable class (vmangos Classes: no 6 or 10).</summary>
    private static bool IsValidTrainerClass(byte value) => value is 0 or 1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 11;

    private sealed record Tables(TaxiPathNodeCatalog PathNodes, SkillLineAbilityCatalog Abilities, RepairCostTable Repair, BankBagSlotPriceTable BankSlots);

    /// <summary>Forwards each map's update to the current flight system.</summary>
    private sealed class FlightUpdater(NpcServicesFeature feature) : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
            if (feature.Flights is not { } flights)
            {
                return;
            }

            try
            {
                flights.Update(map, diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultRecording recording = FaultLedger.Shared.Record("npc-services.flight-update", ex);
                if (recording.ShouldLog)
                {
                    feature.Logger.LogError(
                        ex,
                        "Taxi flight update failed on map {MapId} instance {InstanceId} (diff={DiffMs} ms)",
                        map.MapId,
                        map.InstanceId,
                        diffMs);
                }
            }
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
            if (feature.Flights is not { } flights)
            {
                return;
            }

            try
            {
                flights.OnPlayerRemoved(map, player);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultRecording recording = FaultLedger.Shared.Record("npc-services.flight-player-removed", ex);
                if (recording.ShouldLog)
                {
                    feature.Logger.LogError(
                        ex,
                        "Taxi flight cleanup failed for player {PlayerGuid} on map {MapId} instance {InstanceId}",
                        player.Guid,
                        map.MapId,
                        map.InstanceId);
                }
            }
        }
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
