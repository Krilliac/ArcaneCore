using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The World suite shares one process-wide <see cref="FaultLedger.Shared"/> and records more distinct fault signatures than its
/// default cap (512). Past the cap a new signature is counted into the overflow bucket (type "(various)"), so a test that reads its own
/// entry back by id or site fails depending on how many other tests ran before it. The cap is lifted for the whole test assembly
/// before any test runs, so no test reads an overflowed ledger whatever the order or parallelism.
/// </summary>
internal static class FaultLedgerTestBounds
{
#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize() => FaultLedger.Shared.Configure(new DiagnosticsOptions { FaultMaxSignatures = 1_000_000 });
#pragma warning restore CA2255
}
