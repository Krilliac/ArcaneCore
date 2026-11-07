using System.Runtime.CompilerServices;
using ArcaneCore.MockClient.Protocol;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// The mock client's per-operation deadline in this suite. The tool's 5 s (<see cref="ProtocolIO.DefaultOperationTimeout"/>) is a
/// wall-clock limit on a real server answering a real SQLite or process hop. Gate 1c7134022 failed LoginRedirectTests in this assembly with
/// "World packet search for 0x003A exceeded the 5-second deadline": SMSG_CHAR_CREATE did not arrive in 5 s, before any login.
/// That timeout has NO KNOWN CAUSE: the gate's log holds no server output for the failing connection, so the stall may be load or may be a defect.
/// The 30 s here is an unproven mitigation, not a fix, and it would hide a stall of 5 to 30 s. So LoginRedirectTests' fixture reports its server log and
/// state when that read fails, and in the test's output when it succeeds after more than 5 s: the next occurrence can be diagnosed.
/// The value is set once before any test runs, as in ArcaneCore.MockClient.Tests; each scenario keeps its own overall deadline.
/// </summary>
internal static class TestDeadlines
{
    public static readonly TimeSpan Operation = TimeSpan.FromSeconds(30);

#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize() => ProtocolIO.OperationTimeout = Operation;
#pragma warning restore CA2255
}
