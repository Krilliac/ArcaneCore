using System.Runtime.CompilerServices;
using ArcaneCore.MockClient.Protocol;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// The mock client's per-operation deadline in this suite. The tool's 5 s (<see cref="ProtocolIO.DefaultOperationTimeout"/>) is a
/// wall-clock limit on a real server answering a real SQLite or process hop; under full-gate load it failed healthy scenarios with
/// "World packet read exceeded the 5-second deadline" (gate 1c7134022: TransferCrashTests login, LoginRedirectTests character create).
/// Measured cause (gate 1c7134022 log): the login that tripped it reached a freshly started worker (its gateway link registered only 4 s before)
/// and sat about 3 s in the first login's lazy load of the player base data (StatsFeature.EnsureLoadedAsync), then entered the world 120 ms after the
/// 5 s read had expired. The cost is real and cold-only, so the bound here is a hang bound rather than a speed assertion.
/// Here the deadline is only a hang bound, set once before any test runs, as in ArcaneCore.MockClient.Tests; each scenario keeps its
/// own overall deadline.
/// </summary>
internal static class TestDeadlines
{
    public static readonly TimeSpan Operation = TimeSpan.FromSeconds(30);

#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize() => ProtocolIO.OperationTimeout = Operation;
#pragma warning restore CA2255
}
