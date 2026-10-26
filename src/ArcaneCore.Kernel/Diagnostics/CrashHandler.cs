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
    private volatile bool _logFirstChance;
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

    /// <summary>Re-evaluate whether the first-chance hook is needed (an <see cref="IFaultObserver"/> was added or removed).</summary>
    internal void RefreshFirstChanceHook()
    {
        lock (InstallGate)
        {
            if (!_disposed)
            {
                HookFirstChance();
            }
        }
    }

    private void HookFirstChance()
    {
        // The log of every throw is a Debug build feature; the observers (bot QA) need the hook in every build while one is registered.
        bool log = false;
#if DEBUG
        log = _options.FirstChanceExceptions;
#endif
        _logFirstChance = log;
        bool wanted = log || FaultObservers.Any;
        if (wanted && !_firstChanceHooked)
        {
            AppDomain.CurrentDomain.FirstChanceException += _onFirstChance;
            _firstChanceHooked = true;
        }
        else if (!wanted && _firstChanceHooked)
        {
            AppDomain.CurrentDomain.FirstChanceException -= _onFirstChance;
            _firstChanceHooked = false;
        }
    }

    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        Report(CrashKind.Unhandled, exception, LogLevel.Critical, terminating: true);
        bool recorded = RecordFault("unhandled", exception, terminating: true); // the runtime ends the process after this handler whatever the policy
        if (exception is not null)
        {
            FaultObservers.Notify(FaultObservation.Unhandled, exception, recorded);
        }

        Terminate(_options.OnUnhandled == UnhandledExceptionPolicy.FailFast, exception);
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        UnobservedTaskPolicy policy = _options.OnUnobservedTask;
        bool terminating = policy != UnobservedTaskPolicy.Log;
        // Critical either way in the log: an unobserved fault is an exception nobody handled, whether or not the policy
        // ends the process. The fault ledger carries whether it terminates, so the staff announcement is Critical only then.
        Report(CrashKind.UnobservedTask, e.Exception, LogLevel.Critical, terminating);
        bool recorded = RecordFault("unobserved-task", e.Exception, terminating);
        FaultObservers.Notify(FaultObservation.UnobservedTask, e.Exception, recorded);
        if (!terminating)
        {
            e.SetObserved();
            return;
        }

        Terminate(policy == UnobservedTaskPolicy.FailFast, e.Exception);
    }

    /// <summary>
    /// Count the fault in the ledger after the report is written and before the policy runs; true when it got a signature of its own (not the overflow bucket, not a failure).
    /// Any failure here, out-of-memory included, is swallowed: the crash report and the termination policy must not be skipped (and the unobserved-task hook runs on the finalizer thread).
    /// </summary>
    private static bool RecordFault(string site, Exception? exception, bool terminating)
    {
        if (exception is null)
        {
            return false;
        }

        try
        {
            FaultRecording recording = FaultLedger.Shared.Record(site, exception, terminating);
            return recording.Count > 0 && !string.Equals(recording.Id, FaultLedger.OverflowId, StringComparison.Ordinal);
        }
        catch
        {
            // Deliberately a catch-all; see the summary.
            return false;
        }
    }

    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        // The observers (bot QA) see the raw stream; they decide what is benign (their own re-entrancy guard covers throws inside them).
        if (FaultObservers.Any)
        {
            FaultObservers.Notify(FaultObservation.FirstChance, e.Exception, ledgerRecorded: false);
        }

        if (!_logFirstChance)
        {
            return;
        }

        // Every throw in the process lands here, including throws inside the sink: the thread-static
        // guard in Report makes a nested one a no-op. Expected cancellation signals, controlled
        // malformed-packet layout rejections, and internal BCL expression-interpreter probes are skipped
        // so FirstChanceExceptions remains signal-rich and avoids expensive stack-trace rendering on the world thread.
        if (_reporting || ShouldIgnoreFirstChance(e.Exception))
        {
            return;
        }

        Report(CrashKind.FirstChance, e.Exception, LogLevel.Debug, terminating: false);
    }

    /// <summary>
    /// Whether a first-chance exception should be ignored by <see cref="OnFirstChance"/> so Debug logs stay signal-rich
    /// and world-thread fuzzing/startup probes do not pay for full stack/process crash-report rendering.
    /// </summary>
    public static bool ShouldIgnoreFirstChance(Exception? exception)
        => exception is null || IsCancellationOrSocket(exception) || IsControlledProbe(exception);

    /// <summary>
    /// A cancellation, a socket failure or an I/O failure that wraps one: the normal way a shutdown, a timeout or a client that went away ends an
    /// operation. Part of <see cref="ShouldIgnoreFirstChance"/>.
    /// </summary>
    public static bool IsCancellationOrSocket(Exception? exception)
    {
        if (exception is null or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return true;
        }

        // A cancelled or timed-out HTTP request surfaces as IOException -> SocketException (e.g. 995), which is
        // the same expected cancellation signal as the bare exceptions above. Like the bare-SocketException rule this also hides genuine
        // connection resets (10054) from the first-chance log; unhandled and fatal reports are unaffected.
        return exception is IOException && InnerChainContainsCancellationOrSocket(exception);
    }

    /// <summary>
    /// An exception the code throws on purpose and catches at once (a malformed-packet layout rejection, a BCL delegate-binding probe, the
    /// managed-revision conflict, a log writer's own I/O): never a defect. Part of <see cref="ShouldIgnoreFirstChance"/>.
    /// </summary>
    public static bool IsControlledProbe(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        if (exception is ArgumentOutOfRangeException { ParamName: "packet" or "payload" })
        {
            return true;
        }

        string typeName = exception.GetType().Name;
        if (typeName is "PacketLayoutException" or "PlayerbotQaPublishException")
        {
            return true;
        }

        if (exception is NotSupportedException notSupported && IsBclDelegateBindProbe(notSupported))
        {
            return true;
        }

        if (exception is System.Reflection.TargetInvocationException { InnerException: NotSupportedException innerNotSupported }
            && IsBclDelegateBindProbe(innerNotSupported))
        {
            return true;
        }

        if (exception is InvalidOperationException { Message: "managed-revision-conflict" })
        {
            return true;
        }

        return exception is IOException or UnauthorizedAccessException
            && Thread.CurrentThread.Name?.StartsWith("ArcaneCore.Log.", StringComparison.Ordinal) == true;
    }

    private static bool InnerChainContainsCancellationOrSocket(Exception exception)
    {
        const int MaxDepth = 8;
        Exception? inner = exception.InnerException;
        for (int depth = 0; inner is not null && depth < MaxDepth; depth++, inner = inner.InnerException)
        {
            if (inner is OperationCanceledException or System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBclDelegateBindProbe(NotSupportedException exception)
    {
        string? declaringType = exception.TargetSite?.DeclaringType?.FullName;
        if (declaringType is "System.Delegate" or "System.MulticastDelegate")
        {
            return true;
        }

        return exception.Message.Contains("BindToMethodInfo", StringComparison.Ordinal)
            || exception.Message.Contains("Cannot bind to the target method", StringComparison.Ordinal);
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
            if (_options.BeepOnException)
            {
                CrashAlert.TryBeep(checkErrorStream: true);
            }

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
