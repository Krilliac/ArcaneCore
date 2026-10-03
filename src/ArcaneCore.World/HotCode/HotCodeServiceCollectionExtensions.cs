using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.HotCode;

/// <summary>Connects the metadata-update handler to the refresh for the life of the host.</summary>
internal sealed class HotCodeHost(HotCodeRefresh refresh, CommandTableSource commands, HotCodeAudit audit, ILogger<HotCodeHost> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CommandAddResult added = commands.TryAdd([HotCodeCommands.Create(refresh, audit)]);
        if (!added.Applied)
        {
            logger.LogError("The .hotcode command was not registered: {Error}", added.Error);
        }

        HotCodeMetadataHandler.Activate(refresh);
        logger.LogWarning("Code hot reload refresh is active: new opcode handlers, chat commands and default map updaters are picked up after a code edit.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        HotCodeMetadataHandler.Deactivate(refresh);
        return Task.CompletedTask;
    }
}

public static class HotCodeServiceCollectionExtensions
{
    /// <summary>
    /// Whether <c>World:HotCode:Enabled</c> is set. Everything in <see cref="AddHotCode"/> is
    /// registered only then: with it off no hot-code object exists in the container.
    /// </summary>
    public static bool IsHotCodeEnabled(this IConfiguration configuration)
        => configuration.GetSection(HotCodeOptions.SectionName).Get<HotCodeOptions>()?.Enabled == true;

    public static IServiceCollection AddHotCode(this IServiceCollection services)
    {
        services.AddSingleton<HotCodeState>();
        services.AddSingleton(sp => new HotCodeAudit(sp.GetRequiredService<IOptions<HotCodeOptions>>().Value.AuditLogPath));
        services.AddSingleton<IHotCodeCatalog, AssemblyHotCodeCatalog>();
        services.AddSingleton<IHotCodeWorld>(sp => new WorldRuntimeHotCodeWorld(sp.GetRequiredService<WorldRuntime>()));
        services.AddSingleton(sp => new HotCodeRefresh(
            sp.GetRequiredService<HotCodeState>(),
            sp.GetRequiredService<IHotCodeWorld>(),
            sp.GetRequiredService<OpcodeTable>(),
            sp.GetRequiredService<CommandTableSource>(),
            sp.GetRequiredService<IHotCodeCatalog>(),
            sp.GetRequiredService<HotCodeAudit>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.HotCode"),
            onCodeEdited: () =>
            {
                foreach (Map map in sp.GetRequiredService<WorldRuntime>().Maps)
                {
                    map.ClearUpdaterFaults();
                }
            }));

        // The fault breaker is part of the dev runner: a hot-patched updater that throws every tick
        // is skipped after N ticks instead of flooding the log. An explicit World value wins.
        services.AddOptions<WorldRuntimeOptions>().PostConfigure<IOptions<HotCodeOptions>>((runtime, hot) =>
        {
            if (runtime.MaxConsecutiveUpdaterFaults == 0)
            {
                runtime.MaxConsecutiveUpdaterFaults = Math.Max(0, hot.Value.MaxConsecutiveFaults);
            }
        });
        services.AddHostedService<HotCodeHost>();
        return services;
    }
}
