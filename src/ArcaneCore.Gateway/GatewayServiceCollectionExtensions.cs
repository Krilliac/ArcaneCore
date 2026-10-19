using ArcaneCore.Data;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
