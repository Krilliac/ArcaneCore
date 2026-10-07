using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// Host wiring for the <c>Diagnostics</c> section: one call from each daemon's <c>Program.cs</c>
/// (<c>builder.UseArcaneDiagnostics()</c>; the world daemon's <c>UseWorldDiagnostics</c> calls it and adds
/// its tick context). The crash hooks are attached at once with a standard-error sink, so an exception
/// during host build or schema initialisation is already reported; the hosted service re-points them at
/// the real logger when the host starts.
/// </summary>
public static class DiagnosticsHostingExtensions
{
    /// <summary>
    /// Bind <c>Diagnostics</c>, install the crash hooks and configure <see cref="Invariant"/>. A value the
    /// binder cannot convert (an unknown policy name) is a configuration error: the message is written to
    /// standard error and the process exits with <see cref="ExitCodes.InvalidConfiguration"/> (78) before
    /// anything binds or touches a database, the same fail-closed contract as <c>check-config</c>.
    /// </summary>
    public static IHostApplicationBuilder UseArcaneDiagnostics(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        IConfigurationSection section = builder.Configuration.GetSection(DiagnosticsOptions.SectionName);
        DiagnosticsOptions options;
        try
        {
            options = section.Get<DiagnosticsOptions>() ?? new DiagnosticsOptions();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"ERROR {DiagnosticsOptions.SectionName}: {ex.Message} Fix: use one of the documented values (docs/reference/configuration.md).");
            Environment.Exit(ExitCodes.InvalidConfiguration);
            return builder; // unreachable; keeps the compiler's definite assignment happy
        }

        Invariant.Configure(options, logger: null);
        CrashHandler.Install(options, StandardErrorCrashSink.Instance);

        builder.Services.Configure<DiagnosticsOptions>(section);
        builder.Services.AddHostedService<DiagnosticsHostedService>();
        return builder;
    }
}

/// <summary>
/// Re-points the crash hooks and the invariant log at the host's logger once it exists, and collects
/// the daemon's <see cref="ICrashContextProvider"/>s. Registered first, so it starts before the world and
/// the listeners. The hooks are deliberately not detached on stop: a crash during shutdown is still a crash.
/// </summary>
internal sealed class DiagnosticsHostedService(
    IOptions<DiagnosticsOptions> options,
    IEnumerable<ICrashContextProvider> providers,
    ILoggerFactory loggerFactory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        DiagnosticsOptions value = options.Value;
        ILogger crashLogger = loggerFactory.CreateLogger("ArcaneCore.Diagnostics.Crash");
        Invariant.Configure(value, loggerFactory.CreateLogger("ArcaneCore.Diagnostics.Invariant"));
        CrashHandler handler = CrashHandler.Install(value, new LoggerCrashSink(crashLogger), [.. providers]);

        crashLogger.LogInformation(
            "Diagnostics: OnInvariant={OnInvariant}, BreakOnInvariant={Break}, InvariantLogLimit={LogLimit}, OnUnhandled={OnUnhandled}, OnUnobservedTask={OnUnobserved}, kernel build {Build}",
            value.OnInvariant, value.BreakOnInvariant, value.InvariantLogLimit, value.OnUnhandled, value.OnUnobservedTask, CrashReport.BuildConfiguration);
        if (value.FirstChanceExceptions && !handler.FirstChanceHooked)
        {
            crashLogger.LogWarning("Diagnostics:FirstChanceExceptions is on but this is a Release build of the Kernel; the first-chance hook is compiled out and the key is ignored");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
