using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Reload;

/// <summary>Finds the <c>HotReload</c> options a reloadable consults when it runs (a host without them gets the retail defaults).</summary>
internal static class ReloadPolicy
{
    public static HotReloadOptions Resolve(IServiceProvider services)
        => services.GetService<IOptions<HotReloadOptions>>()?.Value
            ?? services.GetService<ReloadFeature>()?.Options
            ?? new HotReloadOptions();

    public static bool KeepsEmptyTables(IServiceProvider services) => Resolve(services).EmptyTables == EmptyTablePolicy.KeepLoaded;
}
