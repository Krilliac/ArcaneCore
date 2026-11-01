using ArcaneCore.Data;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Kernel.Cluster.Transfer;
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
        services.AddOptions<GatewayHandoffOptions>().Bind(configuration.GetSection(GatewayHandoffOptions.SectionName)).ValidateOnStart();
        services.AddOptions<GatewayControlOptions>().Bind(configuration.GetSection(GatewayControlOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GatewayHandoffOptions>, GatewayHandoffOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GatewayControlOptions>, GatewayControlOptionsValidator>());
        services.AddSingleton<GatewayServer>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayServer>());
        if (configuration.GetSection(GatewayHandoffOptions.SectionName).GetValue<bool>(nameof(GatewayHandoffOptions.Enabled)))
        {
            AddHandoff(services, configuration);
        }

        return services;
    }

    /// <summary>
    /// Gateway:Handoff:Enabled (design _wf/handoff-control-DESIGN.md sections 1 and 6): the characters database (the handoff journal; schema validation only, nothing is created or upgraded and no
    /// initializer runs), the handoff service with its standby links, and the recovery loop. Registered after the server, so they start after its worker links exist and stop before it.
    /// Not registered when the key is off: the gateway then has no characters connection and behaves as before.
    /// </summary>
    private static void AddHandoff(IServiceCollection services, IConfiguration configuration)
    {
        services.PostConfigure<DatabaseOptions>(database => database.Upgrade.ValidateOnly = true);
        services.AddCharacterDatabase(configuration);
        services.AddSingleton(sp => new GatewayHandoffService(
            sp.GetRequiredService<GatewayServer>(), sp.GetRequiredService<IWorldHandoffStore>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddHostedService(sp => sp.GetRequiredService<GatewayHandoffService>());
        services.AddHostedService(sp => new GatewayHandoffRecoveryService(
            sp.GetRequiredService<GatewayHandoffService>(),
            sp.GetRequiredService<IWorldHandoffStore>(),
            sp.GetRequiredService<IOptions<GatewayHandoffOptions>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayHandoffRecoveryService>()));
    }

    private sealed class GatewayHandoffOptionsValidator : IValidateOptions<GatewayHandoffOptions>
    {
        public ValidateOptionsResult Validate(string? name, GatewayHandoffOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            IReadOnlyList<string> problems = options.Validate();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }

    private sealed class GatewayControlOptionsValidator : IValidateOptions<GatewayControlOptions>
    {
        public ValidateOptionsResult Validate(string? name, GatewayControlOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            IReadOnlyList<string> problems = options.Validate();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
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
