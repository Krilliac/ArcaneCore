using ArcaneCore.Game.Social;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World;

/// <summary>DI wiring for the world daemon (also used by the end-to-end tests).</summary>
public static class WorldServiceCollectionExtensions
{
    /// <summary>
    /// The opcode handler groups: every non-abstract <see cref="IOpcodeHandlerGroup"/> in this
    /// assembly (parameterless constructor), ordered by full type name. Discovered rather than
    /// listed so parallel features never edit this file; registering one opcode twice still
    /// fails at startup (<see cref="OpcodeTable"/>).
    /// </summary>
    public static IReadOnlyList<IOpcodeHandlerGroup> HandlerGroups { get; } = AssemblyDiscovery.CreateAll<IOpcodeHandlerGroup>();

    public static OpcodeTable BuildOpcodeTable()
    {
        var table = new OpcodeTable();
        foreach (IOpcodeHandlerGroup group in HandlerGroups)
        {
            group.Register(table);
        }

        return table;
    }

    /// <summary>Every <see cref="IWorldServiceModule"/> of this assembly (parameterless constructor), ordered by full type name.</summary>
    public static IReadOnlyList<IWorldServiceModule> ServiceModules { get; } = AssemblyDiscovery.CreateAll<IWorldServiceModule>();

    public static IServiceCollection AddWorldDaemon(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorldOptions>(configuration.GetSection(WorldOptions.SectionName));
        services.Configure<CharacterFencingOptions>(configuration.GetSection(CharacterFencingOptions.SectionName));
        services.Configure<Cluster.Tooling.ClusterNodeBridgeOptions>(configuration.GetSection(Cluster.Tooling.ClusterNodeBridgeOptions.SectionName));
        services.Configure<WorldRuntimeOptions>(configuration.GetSection(WorldOptions.SectionName));
        services.Configure<WorldSessionOptions>(configuration.GetSection(WorldOptions.SectionName));
        services.Configure<SocialOptions>(configuration.GetSection(SocialOptions.SectionName));
        services.Configure<HotReloadOptions>(configuration.GetSection(HotReloadOptions.SectionName));
        services.Configure<HotCodeOptions>(configuration.GetSection(HotCodeOptions.SectionName));
        // Slow-update thresholds live in their own vmangos-named section (docs/areas/ops-perf.md).
        services.PostConfigure<WorldRuntimeOptions>(o => configuration.GetSection(PerformanceLogOptions.SectionName).Bind(o.Perf));
        services.Configure<Bans.BanOptions>(configuration.GetSection(Bans.BanOptions.SectionName));
        services.Configure<Game.AntiCheat.AntiCheatOptions>(configuration.GetSection(Game.AntiCheat.AntiCheatOptions.SectionName));
        services.Configure<AntiCheat.PacketHeuristics.PacketHeuristicsOptions>(configuration.GetSection(AntiCheat.PacketHeuristics.PacketHeuristicsOptions.SectionName));
        services.Configure<Warden.WardenOptions>(configuration.GetSection(Warden.WardenOptions.SectionName));
        // Subsystem modules (the playerbot pack's options and services, bot QA): discovered, so the core names none of their types.
        foreach (IWorldServiceModule module in ServiceModules)
        {
            module.Register(services, configuration);
        }

        services.AddSingleton(_ => BuildOpcodeTable());
        services.AddSingleton(Net.DevOpcodeTracker.Shared); // stripped developer opcodes: dropped, counted apart from the unhandled ones, metric arcanecore.net.opcode.dev_dropped
        services.AddSingleton(Net.UnhandledOpcodeTracker.Shared); // client opcodes with no handler: counted, warned once, metric arcanecore.net.opcode.unhandled
        services.AddSingleton(sp => new Ops.Diagnostics.WiringAudit(sp)); // .server wiring and the start-up summary (docs/ops/wiring-audit.md)
        services.AddSingleton(sp => ChatCommands.CreateTable(configuration, sp));
        services.AddSingleton(sp => new CommandTableSource(sp.GetRequiredService<CommandTable>()));
        services.AddWorldFeatures();
        services.AddSingleton<CharacterDirectory>();
        services.AddSingleton<CharacterDeletionReconciler>();
        services.TryAddSingleton<IAccountSessionClaimStore, InMemoryAccountSessionClaimStore>();
        services.AddSingleton(sp => new SessionRegistry(sp.GetRequiredService<IAccountSessionClaimStore>()));
        services.AddSingleton<ISessionClaims>(sp => sp.GetRequiredService<SessionRegistry>());
        services.TryAddSingleton<WorldWriterGuard>(); // the characters database's single-writer file lock (WorldStartup takes it before the schema initializers)
        services.TryAddSingleton<ShutdownDrain>(); // the stall-only shutdown lanes of the write queues (WorldHost begins them)
        services.AddSingleton<CharacterSaveQueue>();
        services.AddSingleton<ICharacterSaveQueue>(sp => sp.GetRequiredService<CharacterSaveQueue>());
        services.AddSingleton<CharacterPersistenceBarrier>();
        services.AddSingleton(sp => Gm.Diagnostics.WorldPersistenceProbes.CreateRegistry(sp)); // every write-behind queue, for .arcane queues
        services.AddSingleton(sp => new WorldRuntime(
            sp.GetRequiredService<IOptions<WorldRuntimeOptions>>().Value,
            sp.GetRequiredService<ICharacterSaveQueue>(),
            sp.GetRequiredService<ILogger<WorldRuntime>>()));

        // Order matters: the world starts before the listener and stops after it.
        services.AddHostedService<WorldHost>();
        services.AddHostedService<WorldServer>();
        Cluster.Gateway.GatewayWorkerServiceCollectionExtensions.AddGatewayWorker(services, configuration); // Cluster:Worker (Tier 3a), off by default: registers nothing
        Cluster.Social.ClusterNodeHosting.AddClusterNodeRole(services, configuration); // Cluster:Node (M15.2), Combined by default: registers only the options

        services.AddOpsAnnouncer(configuration); // staff notices / world messages (World:Announce); the hot swap lanes report through it

        // Off by default: with World:HotCode:Enabled unset no hot-code object is registered at all.
        if (configuration.IsHotCodeEnabled())
        {
            services.AddHotCode();
        }

        // Separately opt-in: the module lane (load, replace, unload code) works without the dotnet-watch runner.
        if (configuration.IsHotModulesEnabled())
        {
            services.AddHotModules();
            services.AddFeatureCommands(); // .feature detach/attach (docs/design/hot-reload-tiers.md, section 4.2)
        }

        return services;
    }
}
