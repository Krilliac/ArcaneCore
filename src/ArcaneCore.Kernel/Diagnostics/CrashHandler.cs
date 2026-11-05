using System.Diagnostics;
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
/// <c>Diagnostics:OnUnhandled</c> / <c>Diagnostics:OnUnobservedTask</c> (docs/ops/invariants.md). On a terminating path the opt-in
/// <see cref="CrashDialog"/> (<c>Diagnostics:CrashDialog</c>) runs between the fault record and the policy; it never changes the policy.
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

    [ThreadStatic]
    private static bool t_dumping;

    private readonly UnhandledExceptionEventHandler _onUnhandled;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _onUnobserved;
    private readonly EventHandler<FirstChanceExceptionEventArgs> _onFirstChance;
    private volatile DiagnosticsOptions _options;
    private volatile ICrashSink _sink;
    private volatile IReadOnlyList<ICrashContextProvider> _providers;
    private volatile bool _firstChanceHooked;
    private volatile bool _logFirstChance;
    private readonly FirstChanceThrottle _firstChance = new();
    private volatile CrashDumpService _dumpService = new();
    private readonly object _dumpGate = new();
    private string? _dumpDoneNote;
    private bool _dumpDone;
    private volatile Action<int>? _dumpAnnouncer;
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

    /// <summary>The in-process crash dump writer (<c>Diagnostics:CrashDump</c>); tests replace it so no real dump is written.</summary>
    public CrashDumpService DumpService
    {
        get => _dumpService;
        set => _dumpService = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Told, with the most seconds the dump may take, just before the in-process dump starts. The heartbeat writer sets it to publish the
    /// <c>dumping</c> state, so a supervisor that kills a process for a stale heartbeat waits for the dump instead of truncating it.
    /// </summary>
    public Action<int>? DumpAnnouncer
    {
        get => _dumpAnnouncer;
        set => _dumpAnnouncer = value;
    }

    /// <summary>Whether the first-chance hook is attached (Debug Kernel build with the option on, or any build while an <see cref="IFaultObserver"/> is registered).</summary>
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
        lock (InstallGate)
        {
            HookFirstChance();
        }
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
        DiagnosticsOptions options = _options;
        bool failFast = options.OnUnhandled == UnhandledExceptionPolicy.FailFast;
        string? report = Report(CrashKind.Unhandled, exception, LogLevel.Critical, terminating: true, CrashDumpService.DescribePlan(options.CrashDump, failFast));
        string? faultId = RecordFault("unhandled", exception, terminating: true); // the runtime ends the process after this handler whatever the policy
        NotifyObservers(FaultObservation.Unhandled, exception, faultId);
        string? dumpNote = WriteCrashDump(failFast);
        ShowDialog(options, CrashKind.Unhandled, exception, failFast, "Diagnostics:OnUnhandled = " + options.OnUnhandled, "unhandled", faultId, report, dumpNote);
        Terminate(failFast, exception);
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticsOptions options = _options;
        UnobservedTaskPolicy policy = options.OnUnobservedTask;
        bool terminating = policy != UnobservedTaskPolicy.Log;
        // Critical either way in the log: an unobserved fault is an exception nobody handled, whether or not the policy
        // ends the process. The fault ledger carries whether it terminates, so the staff announcement is Critical only then.
        bool failFastPolicy = policy == UnobservedTaskPolicy.FailFast;
        string? report = Report(CrashKind.UnobservedTask, e.Exception, LogLevel.Critical, terminating, terminating ? CrashDumpService.DescribePlan(options.CrashDump, failFastPolicy) : null);
        string? faultId = RecordFault("unobserved-task", e.Exception, terminating);
        NotifyObservers(FaultObservation.UnobservedTask, e.Exception, faultId);
        if (!terminating)
        {
            e.SetObserved();
            return;
        }

        string? dumpNote = WriteCrashDump(failFastPolicy);
        ShowDialog(options, CrashKind.UnobservedTask, e.Exception, failFastPolicy, "Diagnostics:OnUnobservedTask = " + policy, "unobserved-task", faultId, report, dumpNote);
        Terminate(failFastPolicy, e.Exception);
    }

    /// <summary>
    /// Show the opt-in crash dialog for a terminating crash this process is about to end with, after the report and the fault record:
    /// <see cref="CrashDialog.TryShowFatal"/> returns at once when <c>Diagnostics:CrashDialog</c> is off or the session is not interactive, and
    /// never throws. Used by the hooks here and by the tick watchdog's fatal path. The re-entrancy guard of the report covers it.
    /// </summary>
    public void ShowFatalDialog(CrashKind kind, Exception? exception, bool failFast, string decidedBy, string? report)
        => ShowDialog(_options, kind, exception, failFast, decidedBy, faultSite: null, faultId: null, report, WriteCrashDump(failFast));

    /// <summary>
    /// Write the in-process crash dump (<c>Diagnostics:CrashDump</c>) of a process that is about to end, after the crash report and before the
    /// exit policy runs; used by the hooks here and by the tick watchdog's fatal path. <paramref name="failFast"/> is whether the process ends by
    /// <see cref="Environment.FailFast(string)"/> (the runtime's own dump may then cover it) or by <see cref="Environment.Exit(int)"/>. Returns the
    /// note for the crash dialog (null when nothing is configured). Never throws; bounded by <see cref="CrashDumpService.MaxWaitSeconds"/> (three times <c>Diagnostics:CrashDump:TimeoutSeconds</c>) for the whole Full, Heap, Mini chain, not per attempt.
    /// </summary>
    public string? WriteCrashDump(bool failFast)
    {
        if (t_dumping)
        {
            return null; // this thread is already dumping (a hook that threw inside the dump)
        }

        t_dumping = true;
        try
        {
            // One dump per process: a second crashing thread waits here for the first to finish (and shares its note) instead of racing on
            // to FailFast, which would end the process in the middle of the first thread's dump.
            lock (_dumpGate)
            {
                if (_dumpDone)
                {
                    return _dumpDoneNote;
                }

                DiagnosticsOptions options = _options;
                ICrashSink sink = _sink;
                string daemon = CrashJournalPaths.ProcessName();
                string directory = CrashDumpService.ResolveDirectory(options.CrashDump, CrashDialog.LogFilePath);
                CrashDumpOutcome? outcome = _dumpService.Run(options.CrashDump, failFast, daemon, directory, DateTime.UtcNow, (level, text) => SayDump(sink, level, text), _dumpAnnouncer);
                if (outcome is not null)
                {
                    _dumpDone = true;
                    _dumpDoneNote = outcome.Note;
                }

                return outcome?.Note;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"crash dump failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            t_dumping = false;
        }
    }

    private static void SayDump(ICrashSink sink, LogLevel level, string text)
    {
        try
        {
            sink.Write(level, text, null);
            if (!sink.IsSynchronous)
            {
                Console.Error.WriteLine(text);
                Console.Error.Flush();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine(text);
            Console.Error.WriteLine($"crash sink {sink.GetType().Name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ShowDialog(DiagnosticsOptions options, CrashKind kind, Exception? exception, bool failFast, string decidedBy, string? faultSite, string? faultId, string? report, string? dumpNote)
    {
        if (!options.CrashDialog || _reporting)
        {
            return;
        }

        _reporting = true;
        try
        {
            CrashDialog.TryShowFatal(enabled: true, kind, exception, failFast, decidedBy, faultSite, faultId, report, dumpNote);
        }
        finally
        {
            _reporting = false;
        }
    }

    /// <summary>Count the fault in the ledger after the report is written and before the policy runs, and return its signature id (null when nothing was recorded). Any failure here, out-of-memory included, is swallowed: the crash report and the termination policy must not be skipped (and the unobserved-task hook runs on the finalizer thread).</summary>
    private static string? RecordFault(string site, Exception? exception, bool terminating)
    {
        if (exception is null)
        {
            return null;
        }

        try
        {
            return FaultLedger.Shared.Record(site, exception, terminating).Id;
        }
        catch
        {
            // Deliberately a catch-all; see the summary.
            return null;
        }
    }

    /// <summary>Hand a hook's exception to the registered <see cref="IFaultObserver"/>s; <paramref name="faultId"/> is the ledger signature (null when nothing was recorded, the overflow bucket does not count as recorded).</summary>
    private static void NotifyObservers(FaultObservation kind, Exception? exception, string? faultId)
    {
        if (exception is not null)
        {
            FaultObservers.Notify(kind, exception, faultId is not null && !string.Equals(faultId, FaultLedger.OverflowId, StringComparison.Ordinal));
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

        _ = ReportFirstChance(e.Exception);
    }

    /// <summary>
    /// The first-chance report's body (public so tests drive it in any build; the hook itself is attached only in a Debug Kernel). Every throw in the
    /// process lands here, including throws inside the sink: the thread-static guard makes a nested one a no-op. Expected cancellation signals,
    /// controlled malformed-packet layout rejections and internal BCL probes are skipped (<see cref="ShouldIgnoreFirstChance"/>). Of the rest,
    /// <see cref="FirstChanceThrottle"/> keeps one report per exception object (rethrows are not reported again) and per type and origin per window,
    /// and the report carries the throwing thread's stack, so an exception whose own stack is only the rethrow frame still names where it came from.
    /// Returns whether a report was written.
    /// </summary>
    public bool ReportFirstChance(Exception? exception)
    {
        if (_reporting || exception is null || ShouldIgnoreFirstChance(exception))
        {
            return false;
        }

        string throwSite;
        _reporting = true;
        try
        {
            if (!_firstChance.TryMarkNew(exception))
            {
                return false;
            }

            // Skip this frame; the hook's own frames are skipped by FirstChanceSite as well. No file info for the signature (cheap); the report reads it.
            string origin = FirstChanceSite.Origin(new StackTrace(1, fNeedFileInfo: false));
            FirstChanceDecision decision = _firstChance.Decide(exception.GetType().FullName + " @ " + origin);
            if (!decision.Report)
            {
                return false;
            }

            throwSite = FirstChanceSite.Render(origin, new StackTrace(1, fNeedFileInfo: true), decision);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"first-chance report failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _reporting = false;
        }

        // The report already holds the exception and its stack; the sink gets no exception object, so the logger does not append the same stack again.
        return Report(CrashKind.FirstChance, exception, LogLevel.Debug, terminating: false, dumpPlan: null, throwSite, attachException: false) is not null;
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
        if (exception is IOException && InnerChainContainsCancellationOrSocket(exception))
        {
            return true;
        }

        // Task.Wait / WaitAll at shutdown wrap cancelled tasks in an AggregateException: the same expected cancellation signal.
        return exception is AggregateException aggregate && IsCancellationOnly(aggregate);
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

    private static bool IsCancellationOnly(AggregateException aggregate)
    {
        System.Collections.ObjectModel.ReadOnlyCollection<Exception> inner = aggregate.Flatten().InnerExceptions;
        if (inner.Count == 0)
        {
            return false;
        }

        foreach (Exception e in inner)
        {
            if (e is not OperationCanceledException)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>Write the report through the sink; returns its text (null when nested on this thread or rendering failed).</summary>
    private string? Report(CrashKind kind, Exception? exception, LogLevel level, bool terminating, string? dumpPlan, string? throwSite = null, bool attachException = true)
    {
        if (_reporting)
        {
            return null;
        }

        _reporting = true;
        try
        {
            if (_options.BeepOnException)
            {
                CrashAlert.TryBeep(checkErrorStream: true);
            }

            string report = CrashReport.Render(kind, exception, _providers, DateTime.UtcNow, dumpPlan, throwSite);
            ICrashSink sink = _sink;
            try
            {
                sink.Write(level, report, attachException ? exception : null);
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

            return report;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"crash report failed: {ex}");
            return null;
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
            // (SIGABRT 134 on Linux, 0x80131623 on Windows); the runtime's own dump follows when DOTNET_DbgEnableMiniDump is set
            // (the in-process dump of Diagnostics:CrashDump has been written, or skipped for that reason, before this point).
            Environment.FailFast(exception is null ? "ArcaneCore: unhandled exception" : "ArcaneCore: " + exception.GetType().FullName + ": " + exception.Message, exception);
        }

        Environment.Exit(ExitCodes.UnhandledException);
    }
}
