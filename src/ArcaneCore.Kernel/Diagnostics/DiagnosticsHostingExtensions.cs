using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Logging;
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
        FaultLedger.Shared.Configure(options);
        CrashHandler.Install(options, StandardErrorCrashSink.Instance);

        builder.Services.AddSingleton(FaultLedger.Shared);
        builder.Services.AddSingleton(AnomalyLedger.Shared);
        builder.Services.Configure<DiagnosticsOptions>(section);
        builder.Services.AddHostedService<DiagnosticsHostedService>();
        return builder;
    }
}

/// <summary>
/// Re-points the crash hooks and the invariant log at the host's logger once it exists, and collects
/// the daemon's <see cref="ICrashContextProvider"/>s. Registered first, so it starts before the world and
/// the listeners. The hooks are deliberately not detached on stop: a crash during shutdown is still a crash.
/// It also gives the crash dialog the log file path and, with <c>Diagnostics:FaultDialog</c>, subscribes the
/// new-fault dialog to the fault ledger until the host stops.
/// </summary>
internal sealed class DiagnosticsHostedService(
    IOptions<DiagnosticsOptions> options,
    IEnumerable<ICrashContextProvider> providers,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime? lifetime = null,
    IOptions<ArcaneLoggingOptions>? loggingOptions = null) : IHostedService
{
    private Action<FaultNotice>? _faultDialog;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        DiagnosticsOptions value = options.Value;
        ILogger crashLogger = loggerFactory.CreateLogger("ArcaneCore.Diagnostics.Crash");
        Invariant.Configure(value, loggerFactory.CreateLogger("ArcaneCore.Diagnostics.Invariant"));
        FaultLedger.Shared.Configure(value);
        CrashJournalPaths.ConfigureAndReplay(FaultLedger.Shared, AppContext.BaseDirectory, CrashJournalPaths.ProcessName(), crashLogger);

        AnomalyLedger.Shared.Configure(loggerFactory.CreateLogger("ArcaneCore.Diagnostics.Anomaly"), value.FaultLogLimit);
        CrashHandler handler = CrashHandler.Install(value, new LoggerCrashSink(crashLogger), [.. providers]);

        crashLogger.LogInformation(
            "Diagnostics: OnInvariant={OnInvariant}, BreakOnInvariant={Break}, InvariantLogLimit={LogLimit}, OnUnhandled={OnUnhandled}, OnUnobservedTask={OnUnobserved}, BeepOnStartup={BeepOnStartup}, BeepOnException={BeepOnException}, kernel build {Build}",
            value.OnInvariant, value.BreakOnInvariant, value.InvariantLogLimit, value.OnUnhandled, value.OnUnobservedTask, value.BeepOnStartup, value.BeepOnException, CrashReport.BuildConfiguration);
        if (value.FirstChanceExceptions && !handler.FirstChanceHooked)
        {
            crashLogger.LogWarning("Diagnostics:FirstChanceExceptions is on but this is a Release build of the Kernel; the first-chance hook is compiled out and the key is ignored");
        }

        ConfigureDialogs(value, crashLogger);

        if (value.BeepOnStartup)
        {
            if (lifetime is { } appLifetime && !appLifetime.ApplicationStarted.IsCancellationRequested)
            {
                appLifetime.ApplicationStarted.Register(static () => CrashAlert.TryBeep(bypassCooldown: true));
            }
            else
            {
                CrashAlert.TryBeep(bypassCooldown: true);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _faultDialog, null) is { } subscribed)
        {
            FaultLedger.Shared.FaultNoticed -= subscribed;
        }

        return Task.CompletedTask;
    }

    private void ConfigureDialogs(DiagnosticsOptions value, ILogger crashLogger)
    {
        LogFileSinkPlan? logFile = loggingOptions is null ? null : LogFileSinkPlan.Resolve(loggingOptions.Value);
        CrashDialog.LogFilePath = logFile is { Enabled: true, Path.Length: > 0 } ? Path.GetFullPath(logFile.Path) : null;
        if (!value.CrashDialog && !value.FaultDialog)
        {
            return;
        }

        string? whyNot = CrashDialog.EnvironmentProbe().WhyNot();
        crashLogger.LogInformation(
            "Diagnostics: CrashDialog={CrashDialog}, FaultDialog={FaultDialog}: {Availability}",
            value.CrashDialog, value.FaultDialog, whyNot is null ? "dialogs open in this session" : "no dialog opens in this session (" + whyNot + ")");
        if (value.FaultDialog && _faultDialog is null)
        {
            Action<Exception> onFailure = ex =>
            {
                FaultLedger.Shared.Record(CrashDialog.FailureSite, ex);
                crashLogger.LogFault("diagnostics-crash-dialog", ex, "the new-fault dialog failed");
            };
            _faultDialog = notice => CrashDialog.TryShowFault(enabled: true, notice, onFailure);
            FaultLedger.Shared.FaultNoticed += _faultDialog;
        }
    }
}
