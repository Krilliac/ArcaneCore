using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Transports;

/// <summary>
/// Ships and zeppelins in the world daemon (discovered <see cref="IWorldFeature"/>; docs/areas/transports.md). With
/// <c>World:Transports:Enabled</c> (off by default until the content is present) it builds a route for every
/// <c>gameobject_template</c> type 15 row whose TaxiPathNode.dbc path is usable (vmangos <c>TransportMgr::LoadTransportTemplates</c>),
/// applies the <c>transports</c> period overrides, and installs the world's <see cref="TransportSystem"/>: ships sail on the world
/// clock, players board and leave them through their movement, and a passenger whose ship changes maps is carried along by a far
/// teleport that keeps it aboard. Restart-only. Optional dependencies (teleports, spells) degrade: without the teleport feature a
/// passenger is left at the dock when its ship changes maps.
/// </summary>
public sealed class TransportFeature(IServiceProvider services, ILogger<TransportFeature> logger) : IWorldFeature
{
    private WorldRuntime? _world;
    private IReadOnlyDictionary<uint, uint> _periods = new Dictionary<uint, uint>();

    /// <summary>The bound options (after <see cref="Attach"/>).</summary>
    public TransportOptions Options { get; private set; } = new();

    /// <summary>The world's transport system once installed (world thread), or null while transports are off.</summary>
    public TransportSystem? System { get; private set; }

    /// <summary>The routes that could not be built and why (after install; for the GM command and diagnostics).</summary>
    public IReadOnlyList<(uint Entry, TransportTemplateError Error)> Refused { get; private set; } = [];

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the transport feature is already attached");
        }

        _world = world;
        TransportOptions options = services.GetService<TransportOptions>() ?? new TransportOptions();
        if (services.GetService<TransportOptions>() is null)
        {
            services.GetService<IConfiguration>()?.GetSection(TransportOptions.SectionName).Bind(options);
        }

        Options = options;
        if (!options.Enabled)
        {
            logger.LogInformation("Transports are disabled (World:Transports:Enabled)");
            return;
        }

        using (IServiceScope scope = services.CreateScope())
        {
            // Fail closed like the other content stores: a database that cannot be read stops the start.
            if (scope.ServiceProvider.GetService<ITransportDataStore>() is { } store)
            {
                _periods = TransportPeriods.Select(store.LoadAsync().GetAwaiter().GetResult());
            }
        }

        world.Post(Install);
    }

    /// <summary>Build the routes and start the ships (world thread, after every feature attached and its content loaded).</summary>
    private void Install()
    {
        WorldRuntime world = _world!;
        GameObjectContent content = services.GetService<GameObjectLootFeature>()?.Content ?? GameObjectContent.Empty;
        TaxiPathNodeCatalog paths = LoadPathNodes();
        MapRegistry registry = WorldMaps.Of(world).Registry;

        var templates = new List<TransportTemplate>();
        var refused = new List<(uint, TransportTemplateError)>();
        foreach (GameObjectTemplate gameObject in content.Templates.Where(t => t.Type == TransportTemplateBuilder.MoTransportType).OrderBy(t => t.Entry))
        {
            if (Options.Entries.Count > 0 && !Options.Entries.Contains(gameObject.Entry))
            {
                continue;
            }

            TransportTemplate? template = TransportTemplateBuilder.Build(
                gameObject, paths, mapId => registry.Find(mapId)?.Instanceable ?? false,
                _periods.TryGetValue(gameObject.Entry, out uint period) ? period : null, out TransportTemplateError error);
            if (template is not null && template.MapsUsed.Any(m => !registry.Contains(m)))
            {
                template = null;
                error = TransportTemplateError.BadPath; // a map without a map_template row cannot be entered
            }

            if (template is null)
            {
                refused.Add((gameObject.Entry, error));
                logger.LogWarning("Transport {Entry} ({Name}) not built: {Error} (path {Path})", gameObject.Entry, gameObject.Name, error, gameObject.GetData(0));
                continue;
            }

            templates.Add(template);
        }

        foreach (uint entry in _periods.Keys.Where(e => templates.All(t => t.Entry != e) && refused.All(r => r.Item1 != e)))
        {
            logger.LogWarning("Invalid gameobject {Entry} in transports table", entry); // vmangos TransportMgr.cpp:78
        }

        Refused = refused;
        var system = new TransportSystem(world, templates, logger)
        {
            TeleportPassenger = (player, mapId, x, y, z, o) => services.GetService<TeleportFeature>() is { } teleport
                && teleport.Teleports.TeleportTo(player, mapId, x, y, z, o, TeleportOptions.NotLeaveTransport),
            PreparePassengerForMapChange = PrepareForMapChange,
            TeleportToHomebind = player => services.GetService<TeleportFeature>() is { } teleport && teleport.Teleports.TeleportToHomebind(player),
        };
        TransportSystem.Register(world, system);
        System = system;
        system.Install();
    }

    // vmangos TeleportTransport (Transport.cpp:170-174): no fear or confusion across the map change, combat stopped with the pets
    // and the spell being cast interrupted (CombatStopWithPets(true)).
    private void PrepareForMapChange(Player player)
    {
        if (services.GetService<SpellFeature>()?.System is { } spells)
        {
            spells.RemoveAurasByType(player, AuraType.ModConfuse);
            spells.RemoveAurasByType(player, AuraType.ModFear);
            spells.CancelCast(player, 0);
            spells.CancelChannel(player);
        }

        if (player.Map is { } map)
        {
            map.Combat.CombatStop(player);
            if (player.PetGuid is { IsEmpty: false } petGuid && map.FindObject(petGuid) is Unit pet)
            {
                map.Combat.CombatStop(pet);
            }
        }
    }

    // The same sources as the NPC services' flight paths: a registered catalog (tests), else the configured TaxiPathNode.dbc.
    private TaxiPathNodeCatalog LoadPathNodes()
    {
        if (services.GetService<TaxiPathNodeCatalog>() is { } catalog)
        {
            return catalog;
        }

        var npc = new NpcServiceOptions();
        services.GetService<IConfiguration>()?.GetSection(NpcServiceOptions.SectionName).Bind(npc);
        if (string.IsNullOrWhiteSpace(npc.TaxiPathNodeDbcPath))
        {
            logger.LogWarning("Transports are enabled but {Section}:TaxiPathNodeDbcPath is not set; no ship has a path", NpcServiceOptions.SectionName);
            return TaxiPathNodeCatalog.Empty;
        }

        return NpcServiceDbcReaders.LoadTaxiPathNodes(npc.TaxiPathNodeDbcPath);
    }
}
