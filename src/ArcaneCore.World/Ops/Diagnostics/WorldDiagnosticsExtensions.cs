using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>The world daemon's one-line diagnostics wiring (Program.cs): the Kernel hooks plus the world tick context.</summary>
public static class WorldDiagnosticsExtensions
{
    /// <summary>
    /// <see cref="DiagnosticsHostingExtensions.UseArcaneDiagnostics"/> and a <see cref="WorldCrashContext"/>,
    /// so every crash report of the world daemon carries the tick number, uptime and online count. The
    /// context is created when the diagnostics host starts, before the world thread exists, so no tick is missed.
    /// </summary>
    public static IHostApplicationBuilder UseWorldDiagnostics(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseArcaneDiagnostics();
        builder.Services.AddSingleton<ICrashContextProvider, WorldCrashContext>();
        return builder;
    }
}
