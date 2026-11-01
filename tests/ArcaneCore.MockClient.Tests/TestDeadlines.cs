using System.Runtime.CompilerServices;
using ArcaneCore.MockClient.Protocol;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// The mock client's per-operation deadline in this suite. The tool's 5 s (<see cref="ProtocolIO.DefaultOperationTimeout"/>) is a
/// wall-clock limit on the in-process server: under full-suite load a real SQLite character create answered later than that, and
/// scenarios failed with "World packet read exceeded the 5-second deadline" (QuestRewardAdversarial, EconomyAuctionSilentRefusal).
/// Here the deadline is only a hang bound, set once before any test runs; every scenario keeps its own overall deadline, and the
/// tests of the deadline itself pass the default explicitly.
/// <para>
/// A scenario deadline is armed after <see cref="ArcaneCore.MockClient.Hosting.SyntheticArcaneServer.StartAsync(System.Threading.CancellationToken)"/>
/// returns, never passed into it: startup keeps the fixture's own 60 s hang bound. Passing a 20-40 s scenario token into startup
/// spent the scenario's budget on fixture construction (three SQLite schemas, content seed, a real world host) whenever the host
/// was loaded, and the test failed with a bare cancellation in startup itself or in whichever login step was in flight when the
/// budget ran out (QuestGreetingWire, InactiveQueueWire, ItemFeedbackProbe, EndToEnd, ReplayTranscript, QuestRewardAdversarial).
/// </para>
/// </summary>
internal static class TestDeadlines
{
    public static readonly TimeSpan Operation = TimeSpan.FromSeconds(30);

#pragma warning disable CA2255 // A test assembly's one-time setup, before xUnit runs any test; it is not a library initializer.
    [ModuleInitializer]
    internal static void Initialize()
    {
        ProtocolIO.OperationTimeout = Operation;
        ArcaneCore.MockClient.Hosting.MockWorldPacks.EnsureRegistered(); // before any test reads the world's startup code set
    }
#pragma warning restore CA2255
}
