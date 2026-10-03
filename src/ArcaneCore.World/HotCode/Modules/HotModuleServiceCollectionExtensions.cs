using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>Appends <c>.hotmodule</c> and loads the <c>LoadOnStart</c> modules once the world runs.</summary>
internal sealed class HotModuleHost(
    ModuleHost modules,
    CommandTableSource commands,
    HotCodeAudit audit,
    IOptions<HotCodeOptions> options,
    ILogger<HotModuleHost> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CommandAddResult added = commands.TryAdd([HotModuleCommands.Create(modules, audit)]);
        if (!added.Applied)
        {
            logger.LogError("The .hotmodule command was not registered: {Error}", added.Error);
        }

        logger.LogWarning(
            "Hot code MODULES are ENABLED (World:HotCode:Modules:Enabled): code in {Directory} can be loaded into this process by an Administrator with .hotmodule and runs with full server trust.",
            options.Value.Modules.Directory);
        if (string.IsNullOrWhiteSpace(options.Value.Modules.Allowlist))
        {
            logger.LogWarning("No module allowlist is configured (World:HotCode:Modules:Allowlist): any dll in the module directory can be loaded.");
        }
        else
        {
            logger.LogInformation("Module allowlist: {Allowlist} (a module whose SHA-256 is not listed is refused).", options.Value.Modules.Allowlist);
        }

        // Not awaited: the commit needs the world thread, which starts after this service.
        foreach (string name in options.Value.Modules.LoadOnStart)
        {
            _ = Task.Run(async () =>
            {
                ModuleResult result = await modules.LoadAsync(name).ConfigureAwait(false);
                if (!result.Ok)
                {
                    logger.LogError("Module '{Name}' (LoadOnStart) was not loaded: {Detail}", name, result.Detail);
                }
            }, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class HotModuleServiceCollectionExtensions
{
    /// <summary>Whether <c>World:HotCode:Modules:Enabled</c> is set. Nothing module-related is registered otherwise.</summary>
    public static bool IsHotModulesEnabled(this IConfiguration configuration)
        => configuration.GetSection(HotCodeOptions.SectionName).Get<HotCodeOptions>()?.Modules.Enabled == true;

    public static IServiceCollection AddHotModules(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new HotCodeAudit(sp.GetRequiredService<IOptions<HotCodeOptions>>().Value.AuditLogPath));
        services.TryAddSingleton<IHotCodeWorld>(sp => new WorldRuntimeHotCodeWorld(sp.GetRequiredService<WorldRuntime>()));
        services.AddSingleton(sp => new ModuleHost(
            sp.GetRequiredService<IOptions<HotCodeOptions>>().Value.Modules,
            sp.GetRequiredService<IHotCodeWorld>(),
            sp.GetRequiredService<OpcodeTable>(),
            sp.GetRequiredService<CommandTableSource>(),
            sp.GetRequiredService<HotCodeAudit>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.HotModules")));
        services.AddHostedService<HotModuleHost>();
        return services;
    }
}
