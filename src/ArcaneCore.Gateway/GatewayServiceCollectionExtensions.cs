using ArcaneCore.Data;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Gateway;

/// <summary>The gateway daemon's complete service registration.</summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Logging, the <c>Gateway</c> and <c>Net:Protection</c> sections, the auth database (session keys, account status and
    /// bans: the gateway reads the same rows the world's own handshake reads) and the <see cref="GatewayServer"/>.
    /// </summary>
    public static IServiceCollection AddGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddArcaneCoreLogging(configuration);
        services.AddOptions<GatewayOptions>().Bind(configuration.GetSection(GatewayOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>());
        services.Configure<NetProtectionOptions>(configuration.GetSection(NetProtectionOptions.SectionName));
        services.AddAuthDatabase(configuration);
        services.Configure<ArcaneCore.World.Warden.WardenOptions>(configuration.GetSection(ArcaneCore.World.Warden.WardenOptions.SectionName));
        // Warden runs here, not on the worker (the gateway holds the session key and the client stream). The worker derives the same manifest from
        // its own Warden section and refuses verdicts from a gateway that differs.
        services.AddSingleton(sp => new GatewayWarden(
            sp.GetRequiredService<IOptions<GatewayOptions>>().Value.RealmId,
            sp.GetRequiredService<IOptions<ArcaneCore.World.Warden.WardenOptions>>().Value,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger<GatewayWarden>()));
        // The realm-list row of the clustered realm (no world writes it in worker mode): registered before the server so it starts first (Starting) and stops last (Stopped).
        services.AddSingleton<GatewayRealmLiveness>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayRealmLiveness>());
        services.AddSingleton<GatewayServer>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayServer>());
        return services;
    }

    private sealed class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
    {
        public ValidateOptionsResult Validate(string? name, GatewayOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            IReadOnlyList<string> problems = options.Validate();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }
}
