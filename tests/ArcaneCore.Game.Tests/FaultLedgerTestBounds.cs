using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// Tests here read their own entry back from the process-wide <see cref="FaultLedger.Shared"/>. Past the default signature cap a
/// new signature lands in the overflow bucket, so the cap is lifted for the whole test assembly before any test runs and no test
/// depends on how many other tests recorded faults first (same bound as the World test assembly).
/// </summary>
internal static class FaultLedgerTestBounds
{
#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize() => FaultLedger.Shared.Configure(new DiagnosticsOptions { FaultMaxSignatures = 1_000_000 });
#pragma warning restore CA2255
}
