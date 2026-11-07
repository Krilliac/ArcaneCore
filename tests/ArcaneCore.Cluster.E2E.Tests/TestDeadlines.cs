using System.Runtime.CompilerServices;
using ArcaneCore.MockClient.Protocol;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// The mock client's per-operation deadline in this suite. The tool's 5 s (<see cref="ProtocolIO.DefaultOperationTimeout"/>) is a
/// wall-clock limit on a real server answering a real SQLite or process hop. Gate 1c7134022 failed TransferCrashTests' login with
/// "World packet read exceeded the 5-second deadline".
/// Cause inferred from log timestamps of that gate, not profiled: the login reached a worker whose gateway link had just registered, 3.03 s passed between
/// "authenticated" and the warnings of the first login's lazy player base data load (StatsFeature.EnsureLoadedAsync; the log has no start marker for that load),
/// a further 1.5 s to entering the world is unattributed, and the player entered the world about 120 ms after the 5 s read expired.
/// If that inference holds, the cost is cold-only and a bound here should only catch a hang, so it is set once before any test runs, as in
/// ArcaneCore.MockClient.Tests; each scenario keeps its own overall deadline.
/// </summary>
internal static class TestDeadlines
{
    public static readonly TimeSpan Operation = TimeSpan.FromSeconds(30);

#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize() => ProtocolIO.OperationTimeout = Operation;
#pragma warning restore CA2255
}
