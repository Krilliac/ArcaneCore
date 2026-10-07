using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World;

/// <summary>Binds the <c>Net:Protection</c> section for the world daemon (docs/ops/netguard.md).</summary>
public static class NetProtectionServiceExtensions
{
    /// <summary>
    /// Bind <see cref="NetProtectionOptions"/> from <c>Net:Protection</c>. <see cref="Net.WorldServer"/>
    /// builds its <see cref="Kernel.Net.NetGuard"/> from these options when it starts; without this
    /// call the defaults apply (every protection on), so a missing line cannot switch anything off.
    /// The values are also validated by <c>check-config</c> (<see cref="Ops.Validation.NetProtectionConfigChecks"/>).
    /// </summary>
    public static IServiceCollection AddNetProtection(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NetProtectionOptions>(configuration.GetSection(NetProtectionOptions.SectionName));
        return services;
    }
}
