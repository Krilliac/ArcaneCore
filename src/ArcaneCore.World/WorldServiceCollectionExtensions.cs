using ArcaneCore.Game.Social;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.HotCode.Modules;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    public static IServiceCollection AddWorldDaemon(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorldOptions>(configuration.GetSection(WorldOptions.SectionName));
        services.Configure<WorldRuntimeOptions>(configuration.GetSection(WorldOptions.SectionName));
        services.Configure<WorldSessionOptions>(configuration.GetSection(WorldOptions.SectionName));
        services.Configure<SocialOptions>(configuration.GetSection(SocialOptions.SectionName));
        services.Configure<HotCodeOptions>(configuration.GetSection(HotCodeOptions.SectionName));
        // Slow-update thresholds live in their own vmangos-named section (docs/areas/ops-perf.md).
        services.PostConfigure<WorldRuntimeOptions>(o => configuration.GetSection(PerformanceLogOptions.SectionName).Bind(o.Perf));
        services.Configure<Bans.BanOptions>(configuration.GetSection(Bans.BanOptions.SectionName));
        services.Configure<Game.AntiCheat.AntiCheatOptions>(configuration.GetSection(Game.AntiCheat.AntiCheatOptions.SectionName));
        services.Configure<Warden.WardenOptions>(configuration.GetSection(Warden.WardenOptions.SectionName));
        services.Configure<Playerbots.PlayerbotOptions>(options => Playerbots.PlayerbotOptions.ApplyConfiguration(options, configuration));
        services.AddSingleton<Playerbots.IPlayerbotService>(sp => sp.GetRequiredService<Playerbots.ManagedPlayerbotFeature>());

        services.AddSingleton(_ => BuildOpcodeTable());
        services.AddSingleton(sp => ChatCommands.CreateTable(configuration, sp));
        services.AddSingleton(sp => new CommandTableSource(sp.GetRequiredService<CommandTable>()));
        services.AddWorldFeatures();
        services.AddSingleton<CharacterDirectory>();
        services.AddSingleton<CharacterDeletionReconciler>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<CharacterSaveQueue>();
        services.AddSingleton<ICharacterSaveQueue>(sp => sp.GetRequiredService<CharacterSaveQueue>());
        services.AddSingleton(sp => new WorldRuntime(
            sp.GetRequiredService<IOptions<WorldRuntimeOptions>>().Value,
            sp.GetRequiredService<ICharacterSaveQueue>(),
            sp.GetRequiredService<ILogger<WorldRuntime>>()));

        // Order matters: the world starts before the listener and stops after it.
        services.AddHostedService<WorldHost>();
        services.AddHostedService<WorldServer>();

        // Off by default: with World:HotCode:Enabled unset no hot-code object is registered at all.
        if (configuration.IsHotCodeEnabled())
        {
            services.AddHotCode();
        }

        // Separately opt-in: the module lane (load, replace, unload code) works without the dotnet-watch runner.
        if (configuration.IsHotModulesEnabled())
        {
            services.AddHotModules();
        }

        return services;
    }
}
