using System.Runtime.ExceptionServices;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The process-wide crash hooks: <see cref="AppDomain.UnhandledException"/>,
/// <see cref="TaskScheduler.UnobservedTaskException"/> and, in a Debug build of the Kernel with
/// <c>Diagnostics:FirstChanceExceptions</c> on, <see cref="AppDomain.FirstChanceException"/>. Each writes a
/// <see cref="CrashReport"/> (exception and managed stack, process and runtime, thread, GC and working
/// set, the invariant counters, the daemon's <see cref="ICrashContextProvider"/>s) and then honours
/// <c>Diagnostics:OnUnhandled</c> / <c>Diagnostics:OnUnobservedTask</c> (docs/ops/invariants.md).
/// <para>
/// Ownership: one instance per process (<see cref="Install"/> is idempotent and <see cref="Reconfigure"/>
/// swaps the sink, options and providers, so the host installs a standard-error sink before it is built
/// and the logger sink once it is). The hooks run on the crashing thread; a report is never attempted
/// re-entrantly on that thread (a sink or provider that throws cannot recurse). Nothing here allocates
/// until a hook fires.
/// </para>
/// </summary>
public sealed class CrashHandler : IDisposable
{
    private static readonly object InstallGate = new();
    private static CrashHandler? _installed;

    [ThreadStatic]
    private static bool _reporting;

    private readonly UnhandledExceptionEventHandler _onUnhandled;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _onUnobserved;
    private readonly EventHandler<FirstChanceExceptionEventArgs> _onFirstChance;
    private volatile DiagnosticsOptions _options;
    private volatile ICrashSink _sink;
    private volatile IReadOnlyList<ICrashContextProvider> _providers;
    private volatile bool _firstChanceHooked;
    private bool _disposed;

    private CrashHandler(DiagnosticsOptions options, ICrashSink sink, IReadOnlyList<ICrashContextProvider> providers)
    {
        _options = options;
        _sink = sink;
        _providers = providers;
        _onUnhandled = OnUnhandled;
        _onUnobserved = OnUnobserved;
        _onFirstChance = OnFirstChance;
    }

    /// <summary>The installed handler, or null before <see cref="Install"/>.</summary>
    public static CrashHandler? Current
    {
        get
        {
            lock (InstallGate)
            {
                return _installed;
            }
        }
    }

    /// <summary>The options in force.</summary>
    public DiagnosticsOptions Options => _options;

    /// <summary>Where reports go (standard error until the host starts, then the logger).</summary>
    public ICrashSink Sink => _sink;

    /// <summary>Whether the first-chance hook is attached (Debug Kernel build with the option on).</summary>
    public bool FirstChanceHooked => _firstChanceHooked;

    /// <summary>
    /// Attach the hooks (once per process) or, when already attached, apply the new options, sink and
    /// providers. Returns the process's handler.
    /// </summary>
    public static CrashHandler Install(DiagnosticsOptions options, ICrashSink sink, IReadOnlyList<ICrashContextProvider>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        lock (InstallGate)
        {
            if (_installed is { } existing)
            {
                existing.Reconfigure(options, sink, providers);
                return existing;
            }

            var handler = new CrashHandler(options, sink, providers ?? []);
            AppDomain.CurrentDomain.UnhandledException += handler._onUnhandled;
            TaskScheduler.UnobservedTaskException += handler._onUnobserved;
            handler.HookFirstChance();
            _installed = handler;
            return handler;
        }
    }

    /// <summary>Swap options, sink and providers without detaching the hooks (no window without a handler).</summary>
    public void Reconfigure(DiagnosticsOptions options, ICrashSink sink, IReadOnlyList<ICrashContextProvider>? providers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        _options = options;
        _sink = sink;
        _providers = providers ?? [];
        HookFirstChance();
    }

    /// <summary>Render the report this handler would write for <paramref name="exception"/>, with the current providers.</summary>
    public string Render(CrashKind kind, Exception? exception) => CrashReport.Render(kind, exception, _providers, DateTime.UtcNow);

    /// <summary>Detach the hooks (tests). The daemons keep them until the process ends.</summary>
    public void Dispose()
    {
        lock (InstallGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
            TaskScheduler.UnobservedTaskException -= _onUnobserved;
            if (_firstChanceHooked)
            {
                AppDomain.CurrentDomain.FirstChanceException -= _onFirstChance;
                _firstChanceHooked = false;
            }

            if (ReferenceEquals(_installed, this))
            {
                _installed = null;
            }
        }
    }

    private void HookFirstChance()
    {
#if DEBUG
        if (_options.FirstChanceExceptions && !_firstChanceHooked)
        {
            AppDomain.CurrentDomain.FirstChanceException += _onFirstChance;
            _firstChanceHooked = true;
        }
        else if (!_options.FirstChanceExceptions && _firstChanceHooked)
        {
            AppDomain.CurrentDomain.FirstChanceException -= _onFirstChance;
            _firstChanceHooked = false;
        }
#endif
    }

    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        Report(CrashKind.Unhandled, exception, LogLevel.Critical, terminating: true);
        Terminate(_options.OnUnhandled == UnhandledExceptionPolicy.FailFast, exception);
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        UnobservedTaskPolicy policy = _options.OnUnobservedTask;
        bool terminating = policy != UnobservedTaskPolicy.Log;
        Report(CrashKind.UnobservedTask, e.Exception, terminating ? LogLevel.Critical : LogLevel.Error, terminating);
        if (!terminating)
        {
            e.SetObserved();
            return;
        }

        Terminate(policy == UnobservedTaskPolicy.FailFast, e.Exception);
    }

    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        // Every throw in the process lands here, including throws inside the sink: the thread-static
        // guard in Report makes a nested one a no-op.
        if (_reporting)
        {
            return;
        }

        Report(CrashKind.FirstChance, e.Exception, LogLevel.Debug, terminating: false);
    }

    private void Report(CrashKind kind, Exception? exception, LogLevel level, bool terminating)
    {
        if (_reporting)
        {
            return;
        }

        _reporting = true;
        try
        {
            string report = CrashReport.Render(kind, exception, _providers, DateTime.UtcNow);
            ICrashSink sink = _sink;
            try
            {
                sink.Write(level, report, exception);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"crash sink {sink.GetType().Name} failed: {ex.GetType().Name}: {ex.Message}");
            }

            // The logging pipeline is asynchronous; a report that only entered its queue is lost when the
            // process aborts a moment later, so the terminal paths write it to standard error as well.
            if (terminating && !sink.IsSynchronous)
            {
                Console.Error.WriteLine(report);
                Console.Error.Flush();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"crash report failed: {ex}");
        }
        finally
        {
            _reporting = false;
        }
    }

    private static void Terminate(bool failFast, Exception? exception)
    {
        if (failFast)
        {
            // FailFast writes its own line and the exception to standard error / the event log, then aborts
            // (SIGABRT 134 on Linux, 0x80131623 on Windows); a dump follows when DOTNET_DbgEnableMiniDump is set.
            Environment.FailFast(exception is null ? "ArcaneCore: unhandled exception" : "ArcaneCore: " + exception.GetType().FullName + ": " + exception.Message, exception);
        }

        Environment.Exit(ExitCodes.UnhandledException);
    }
}
