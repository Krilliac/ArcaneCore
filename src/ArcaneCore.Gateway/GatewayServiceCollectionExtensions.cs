using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Gateway.Handoff;
using ArcaneCore.Gateway.Transfers;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.ServerConsole;
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
        bool handoffEnabled = configuration.GetSection(GatewayHandoffOptions.SectionName).GetValue<bool>(nameof(GatewayHandoffOptions.Enabled));
        bool transfersEnabled = configuration.GetSection(GatewayTransferOptions.SectionName).GetValue<bool>(nameof(GatewayTransferOptions.Enabled));
        // Gateway:Handoff:* and Gateway:Control:* are validated (at host start and whenever the options are read) only while the feature that reads them is on: an invalid value under a key
        // that is off must not stop a gateway that never uses it. Gateway:Control:* are the reply timeouts of every control channel, so the transfer hosting needs them valid too.
        OptionsBuilder<GatewayHandoffOptions> handoffOptions = services.AddOptions<GatewayHandoffOptions>().Bind(configuration.GetSection(GatewayHandoffOptions.SectionName));
        OptionsBuilder<GatewayControlOptions> controlOptions = services.AddOptions<GatewayControlOptions>().Bind(configuration.GetSection(GatewayControlOptions.SectionName));
        if (handoffEnabled)
        {
            handoffOptions.ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GatewayHandoffOptions>, GatewayHandoffOptionsValidator>());
        }

        if (handoffEnabled || transfersEnabled)
        {
            controlOptions.ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GatewayControlOptions>, GatewayControlOptionsValidator>());
        }

        services.AddOptions<GatewayTransferOptions>().Bind(configuration.GetSection(GatewayTransferOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GatewayTransferOptions>, GatewayTransferOptionsValidator>());
        // A child of the cluster host supervisor stops gracefully on its stdin "drain" line or EOF (the same registration the realm and world make). It sits before the server, so the
        // server's StopAsync (client and worker links) runs through the normal host shutdown that StopApplication begins, and the reader is released last.
        services.TryAddSingleton(_ => ConsoleInputSource.Default());
        services.AddHostedService<SupervisedChildDrain>();
        services.AddSingleton<GatewayServer>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayServer>());
        if (handoffEnabled)
        {
            AddHandoff(services, configuration);
        }

        if (transfersEnabled)
        {
            // After the handoff hosting: the transfer service coordinates with the handoff service when both are on.
            AddTransfers(services, configuration, handoffEnabled);
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
        EnsureCharacterDatabase(services, configuration);
        services.AddSingleton(sp => new GatewayHandoffService(
            sp.GetRequiredService<GatewayServer>(), sp.GetRequiredService<IWorldHandoffStore>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddHostedService(sp => sp.GetRequiredService<GatewayHandoffService>());
        // The operator's .cluster restart, relayed by the workers over the control channel (design section 3.4).
        services.AddSingleton(sp => new GatewayRestartOrchestration(
            sp.GetRequiredService<GatewayServer>(), sp.GetRequiredService<GatewayHandoffService>(), null, sp.GetRequiredService<ILoggerFactory>()));
        services.AddHostedService(sp => sp.GetRequiredService<GatewayRestartOrchestration>());
        services.AddHostedService(sp => new GatewayHandoffRecoveryService(
            sp.GetRequiredService<GatewayHandoffService>(),
            sp.GetRequiredService<IWorldHandoffStore>(),
            sp.GetRequiredService<IOptions<GatewayHandoffOptions>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayHandoffRecoveryService>()));
    }

    /// <summary>
    /// Gateway:Transfers:Enabled (design _wf/handoff-control-DESIGN.md sections 1, 4 and 6): the characters database (the transfer journal; validate only), the transfer service with its slot state machine and the recovery loop.
    /// Registered after the server and the handoff hosting, so they start after the worker links exist and stop before them. Not registered when the key is off.
    /// </summary>
    private static void AddTransfers(IServiceCollection services, IConfiguration configuration, bool handoffEnabled)
    {
        EnsureCharacterDatabase(services, configuration);
        services.AddSingleton(sp =>
        {
            EfTransferDecisionStore journal = sp.GetRequiredService<EfTransferDecisionStore>();
            GatewayHandoffService? handoffs = handoffEnabled ? sp.GetRequiredService<GatewayHandoffService>() : null;
            return new GatewayTransferService(
                sp.GetRequiredService<GatewayServer>(),
                journal,
                journal,
                sp.GetRequiredService<IOptions<GatewayTransferOptions>>().Value,
                handoffs is null ? null : sp.GetRequiredService<IWorldHandoffStore>(),
                handoffs,
                sp.GetRequiredService<ILoggerFactory>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<GatewayTransferService>());
        services.AddHostedService(sp => new GatewayTransferRecoveryService(
            sp.GetRequiredService<GatewayTransferService>(),
            sp.GetRequiredService<IOptions<GatewayTransferOptions>>().Value,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayTransferRecoveryService>()));
    }

    /// <summary>The gateway's characters connection, validate-only with no initializer, registered once however many hostings need it (the handoff journal, the transfer journal).</summary>
    private static void EnsureCharacterDatabase(IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(CharacterDbContext)))
        {
            return;
        }

        services.PostConfigure<DatabaseOptions>(database => database.Upgrade.ValidateOnly = true);
        services.AddCharacterDatabase(configuration);
    }

    private sealed class GatewayTransferOptionsValidator : IValidateOptions<GatewayTransferOptions>
    {
        public ValidateOptionsResult Validate(string? name, GatewayTransferOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            IReadOnlyList<string> problems = options.Validate();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
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
