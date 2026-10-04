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

            case "unobserved-exit":
                return Unobserved(UnobservedTaskPolicy.Exit);

            case "unobserved-failfast":
                return Unobserved(UnobservedTaskPolicy.FailFast);

            case "hosting-bad-config":
                // UseArcaneDiagnostics must exit 78 before anything else runs.
                Host.CreateApplicationBuilder(rest).UseArcaneDiagnostics();
                return Survive();

            default:
                Console.Error.WriteLine($"unknown probe scenario '{scenario}'");
                return 64;
        }
    }

    private static int Unhandled(UnhandledExceptionPolicy policy)
    {
        CrashHandler.Install(new DiagnosticsOptions { OnUnhandled = policy }, StandardErrorCrashSink.Instance, [new ProbeContext()]);
        var thread = new Thread(static () => throw new InvalidOperationException("probe-unhandled-message")) { Name = "probe-thrower" };
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20));
        Thread.Sleep(2000); // the runtime raises UnhandledException on the throwing thread before it dies
        return Survive();
    }

    private static int Unobserved(UnobservedTaskPolicy policy)
    {
        CrashHandler.Install(new DiagnosticsOptions { OnUnobservedTask = policy }, StandardErrorCrashSink.Instance, [new ProbeContext()]);
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

    private sealed class ProbeContext : ICrashContextProvider
    {
        public string Name => "probe";

        public void Describe(StringBuilder report) => report.Append("  probe context line: 4711\n");
    }
}
