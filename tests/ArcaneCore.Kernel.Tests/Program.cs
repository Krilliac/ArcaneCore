using System.Runtime.CompilerServices;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.Kernel.Tests;

/// <summary>
/// Entry point of the test assembly (the test SDK's generated one is switched off). Under the test
/// runner it is never called. Started as <c>dotnet ArcaneCore.Kernel.Tests.dll --diagnostics-probe
/// &lt;scenario&gt;</c> by <see cref="Diagnostics.CrashProcessTests"/>, it runs one scenario that ends the
/// process in the way under test (FailFast, Exit 70, exit 78) or survives and prints a marker.
/// <para>
/// Trying the crash dialog by hand on Windows: from an interactive console (no redirection),
/// <c>dotnet ArcaneCore.Kernel.Tests.dll --diagnostics-probe unhandled-dialog-live</c> crashes with <c>Diagnostics:CrashDialog</c> on and
/// the real message box; the process exits 70 once it is closed. The automated test runs the same scenario redirected, where no box opens.
/// <c>--diagnostics-probe fault-dialog-live</c> does the same for the new-fault box (<c>Diagnostics:FaultDialog</c>): it records one caught
/// fault, waits until the box with its Copy and OK buttons is closed, and prints the survived marker.
/// </para>
/// </summary>
public static class Program
{
    public const string ProbeSwitch = "--diagnostics-probe";

    public const string SurvivedMarker = "PROBE SURVIVED";

    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == ProbeSwitch)
        {
            return DiagnosticsProbe.Run(args[1], args[2..]);
        }

        return 0;
    }
}

/// <summary>The child-process scenarios. Each one is one documented path of docs/ops/invariants.md.</summary>
public static class DiagnosticsProbe
{
    public static int Run(string scenario, string[] rest)
    {
        switch (scenario)
        {
            case "invariant-failfast":
                Invariant.Configure(new DiagnosticsOptions { OnInvariant = InvariantPolicy.FailFast }, logger: null);
                Invariant.Check(1 + 1 == 3, "probe-invariant-message");
                return Survive();

            case "unhandled-failfast":
                return Unhandled(UnhandledExceptionPolicy.FailFast);

            case "unhandled-exit":
                return Unhandled(UnhandledExceptionPolicy.Exit);

            case "unobserved-log":
                return Unobserved(UnobservedTaskPolicy.Log);

            case "unhandled-level":
                return Unhandled(UnhandledExceptionPolicy.Exit, LevelEchoSink.Instance);

            case "unobserved-level":
                return Unobserved(UnobservedTaskPolicy.Log, LevelEchoSink.Instance);

            case "unobserved-exit":
                return Unobserved(UnobservedTaskPolicy.Exit);

            case "unobserved-failfast":
                return Unobserved(UnobservedTaskPolicy.FailFast);

            case "unhandled-dialog":
                // A recording dialog in a forced interactive Windows session: the report, then the dialog, then exit 70.
                return UnhandledWithDialog(UnhandledExceptionPolicy.Exit, EchoDialog.Instance, rest);

            case "unhandled-dialog-failfast":
                return UnhandledWithDialog(UnhandledExceptionPolicy.FailFast, EchoDialog.Instance, rest);

            case "unhandled-dialog-throws":
                // The dialog itself fails: the failure is recorded and the exit policy still runs.
                FaultLedger.Shared.FaultNoticed += static notice =>
                {
                    Console.Error.WriteLine($"FAULT-NOTICED site={notice.Site} type={notice.ExceptionType}");
                    Console.Error.Flush();
                };
                return UnhandledWithDialog(UnhandledExceptionPolicy.Exit, ThrowingDialog.Instance, rest);

            case "unhandled-dialog-live":
                // The real message box and the real environment probe: a box only in an interactive Windows console.
                if (rest.Length > 0)
                {
                    CrashDialog.ReportDirectory = rest[0];
                }

                CrashHandler.Install(Options(o => { o.OnUnhandled = UnhandledExceptionPolicy.Exit; o.CrashDialog = true; }), StandardErrorCrashSink.Instance, [new ProbeContext()]);
                return ThrowOnThread(withInner: true);

            case "fault-dialog-live":
                // The real presenter and the real environment probe: one caught fault, one box, and a wait until it is closed.
                return FaultDialogLive();

            case "dump":
                // dump <failfast|exit|unobserved-failfast|unobserved-exit> <Off|Mini|Heap|Full> <directory> [maxFiles]: the real in-process crash dump.
                return DumpScenario(rest);

            case "hosting-bad-config":
                // UseArcaneDiagnostics must exit 78 before anything else runs.
                Host.CreateApplicationBuilder(rest).UseArcaneDiagnostics();
                return Survive();

            default:
                Console.Error.WriteLine($"unknown probe scenario '{scenario}'");
                return 64;
        }
    }

    /// <summary>Probe options: the in-process crash dump is Off unless a scenario asks for it, so the other scenarios never write a dump of the test process.</summary>
    private static DiagnosticsOptions Options(Action<DiagnosticsOptions> configure)
    {
        var options = new DiagnosticsOptions();
        options.CrashDump.Mode = CrashDumpMode.Off;
        configure(options);
        return options;
    }

    private static int DumpScenario(string[] rest)
    {
        string how = rest[0];
        var options = Options(o =>
        {
            o.CrashDump.Mode = Enum.Parse<CrashDumpMode>(rest[1]);
            o.CrashDump.Directory = rest[2];
            if (rest.Length > 3)
            {
                o.CrashDump.MaxFiles = int.Parse(rest[3], System.Globalization.CultureInfo.InvariantCulture);
            }

            if (how == "invariant")
            {
                o.OnInvariant = InvariantPolicy.FailFast;
            }

            o.OnUnhandled = how == "exit" ? UnhandledExceptionPolicy.Exit : UnhandledExceptionPolicy.FailFast;
            o.OnUnobservedTask = how switch
            {
                "unobserved-exit" => UnobservedTaskPolicy.Exit,
                "unobserved-failfast" => UnobservedTaskPolicy.FailFast,
                _ => UnobservedTaskPolicy.Log,
            };
        });
        CrashHandler.Install(options, StandardErrorCrashSink.Instance, [new ProbeContext()]);
        if (how == "invariant")
        {
            Invariant.Configure(options, logger: null);
            Invariant.Check(false, "probe-invariant");
            return Survive(); // FailFast never returns
        }

        if (how.StartsWith("unobserved", StringComparison.Ordinal))
        {
            DropFaultedTask();
            for (int i = 0; i < 100; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }

            return Survive();
        }

        return ThrowOnThread();
    }

    private static int Unhandled(UnhandledExceptionPolicy policy, ICrashSink? sink = null)
    {
        CrashHandler.Install(Options(o => o.OnUnhandled = policy), sink ?? StandardErrorCrashSink.Instance, [new ProbeContext()]);
        return ThrowOnThread();
    }

    private static int UnhandledWithDialog(UnhandledExceptionPolicy policy, ICrashDialogPresenter presenter, string[] rest)
    {
        CrashDialog.ReportDirectory = rest.Length > 0 ? rest[0] : null;
        CrashDialog.Presenter = presenter;
        CrashDialog.EnvironmentProbe = static () => new CrashDialogEnvironment(true, true, false, false, false, false);
        CrashHandler.Install(Options(o => { o.OnUnhandled = policy; o.CrashDialog = true; }), StandardErrorCrashSink.Instance, [new ProbeContext()]);
        return ThrowOnThread(withInner: true);
    }

    private static int FaultDialogLive()
    {
        FaultLedger.Shared.Reset();
        FaultLedger.Shared.Configure(new DiagnosticsOptions { FaultDialog = true });
        using var closed = new ManualResetEventSlim(false);
        ICrashDialogPresenter real = CrashDialog.Presenter;
        CrashDialog.Presenter = new ClosingSignalDialog(real, closed);
        Action<FaultNotice> subscribed = notice =>
        {
            if (!CrashDialog.TryShowFault(enabled: true, notice, onFailure: ex =>
                {
                    Console.Error.WriteLine($"FAULT-DIALOG failed: {ex.GetType().Name}: {ex.Message}");
                    Console.Error.Flush();
                }))
            {
                closed.Set();
            }
        };
        FaultLedger.Shared.FaultNoticed += subscribed;
        try
        {
            string? whyNot = CrashDialog.EnvironmentProbe().WhyNot();
            if (whyNot is not null)
            {
                Console.Error.WriteLine($"FAULT-DIALOG none opens in this session: {whyNot}");
                Console.Error.Flush();
                return Survive();
            }

            try
            {
                throw new InvalidOperationException("probe-fault-message", new FormatException("probe-inner-message"));
            }
            catch (InvalidOperationException ex)
            {
                FaultLedger.Shared.Record("probe.fault-dialog-live", ex);
            }

            if (!closed.Wait(TimeSpan.FromMinutes(10)))
            {
                Console.Error.WriteLine("FAULT-DIALOG was not closed within 10 minutes");
                Console.Error.Flush();
            }

            return Survive();
        }
        finally
        {
            FaultLedger.Shared.FaultNoticed -= subscribed;
            CrashDialog.Presenter = real;
        }
    }

    private static int ThrowOnThread(bool withInner = false)
    {
        var thread = new Thread(() => throw (withInner
            ? new InvalidOperationException("probe-unhandled-message", new FormatException("probe-inner-message"))
            : new InvalidOperationException("probe-unhandled-message"))) { Name = "probe-thrower" };
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));
        Thread.Sleep(2000); // the runtime raises UnhandledException on the throwing thread before it dies
        return Survive();
    }

    private static int Unobserved(UnobservedTaskPolicy policy, ICrashSink? sink = null)
    {
        CrashHandler.Install(Options(o => o.OnUnobservedTask = policy), sink ?? StandardErrorCrashSink.Instance, [new ProbeContext()]);
        DropFaultedTask();
        for (int i = 0; i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(50);
        }

        return Survive();
    }

    /// <summary>A faulted task nobody awaits, created in its own frame so no reference outlives the method.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropFaultedTask()
    {
        Task faulted = Task.Run(static () => throw new InvalidOperationException("probe-unobserved-message"));
        SpinWait.SpinUntil(() => faulted.IsCompleted, TimeSpan.FromSeconds(10));
    }

    private static int Survive()
    {
        Console.Out.WriteLine(Program.SurvivedMarker);
        Console.Out.Flush();
        return 0;
    }

    /// <summary>A synchronous sink that states the level and exception type the hook logged at, so a test can assert on them.</summary>
    private sealed class LevelEchoSink : ICrashSink
    {
        public static LevelEchoSink Instance { get; } = new();

        public bool IsSynchronous => true;

        public void Write(Microsoft.Extensions.Logging.LogLevel level, string report, Exception? exception)
        {
            Console.Error.WriteLine($"CRASH-SINK level={level} exception={exception?.GetType().FullName}");
            Console.Error.Flush();
        }
    }

    /// <summary>A dialog that writes what it would show to standard error.</summary>
    private sealed class EchoDialog : ICrashDialogPresenter
    {
        public static EchoDialog Instance { get; } = new();

        public void Show(string title, string text)
        {
            Console.Error.WriteLine($"CRASH-DIALOG title={title}");
            Console.Error.WriteLine(text);
            Console.Error.WriteLine("CRASH-DIALOG end");
            Console.Error.Flush();
        }
    }

    /// <summary>A dialog that fails the way a P/Invoke can.</summary>
    private sealed class ThrowingDialog : ICrashDialogPresenter
    {
        public static ThrowingDialog Instance { get; } = new();

        public void Show(string title, string text) => throw new InvalidOperationException("probe-dialog-failure");
    }

    /// <summary>Hands the box to the real presenter and signals when it has been closed (or has failed), so the probe can wait for the fault dialog's background thread.</summary>
    private sealed class ClosingSignalDialog(ICrashDialogPresenter inner, ManualResetEventSlim closed) : ICrashDialogPresenter
    {
        public void Show(string title, string text)
        {
            try
            {
                inner.Show(title, text);
            }
            finally
            {
                closed.Set();
            }
        }
    }

    private sealed class ProbeContext : ICrashContextProvider
    {
        public string Name => "probe";

        public void Describe(StringBuilder report) => report.Append("  probe context line: 4711\n");
    }
}
