using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.World.Ops.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// The conventional namespace for AddXxx extensions, so Program.cs needs one added line and no new using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The world daemon's operator diagnostics wiring (docs/ops/diagnostics.md).</summary>
public static class WorldOpsDiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Bind <c>Ops:Diagnostics</c> and register the allocation profiler and the process-dump service that
    /// <c>.debug alloc</c> and <c>.debug dump</c> use. Nothing runs until a command asks: the profiler subscribes to the runtime only
    /// while a sample is running, and the dump service writes nothing unless <c>Ops:Diagnostics:Dumps:Enabled</c> is set.
    /// </summary>
    public static IServiceCollection AddWorldOpsDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new OpsDiagnosticsOptions();
        configuration.GetSection(OpsDiagnosticsOptions.SectionName).Bind(options);
        string logFile = LogFileSinkPlan.Resolve(configuration).Path;
        services.AddSingleton(options);
        services.AddSingleton(sp => new AllocationProfiler(logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.Diagnostics.Alloc")));
        services.AddSingleton(sp => new ProcessDumpService(
            options.Dumps, ProcessDumpService.ResolveDirectory(options.Dumps, logFile), sp.GetRequiredService<ILoggerFactory>().CreateLogger("ArcaneCore.Diagnostics.Dump")));
        return services;
    }
}
