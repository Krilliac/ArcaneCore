using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
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
    /// <summary>The opcode handler groups, in registration order.</summary>
    public static IReadOnlyList<IOpcodeHandlerGroup> HandlerGroups { get; } =
    [
        new CharacterHandlers(),
        new AccountDataHandlers(),
        new QueryHandlers(),
        new MovementHandlers(),
        new PlayerHandlers(),
        new LogoutHandlers(),
        new ChatHandlers(),
    ];

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

        services.AddSingleton(_ => BuildOpcodeTable());
        services.AddSingleton(_ => BuiltinCommands.Create());
        services.AddSingleton<CharacterDirectory>();
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
        return services;
    }
}
