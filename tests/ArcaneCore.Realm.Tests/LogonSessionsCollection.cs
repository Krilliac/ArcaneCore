using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// <see cref="LogonHandshakeTests"/>, <see cref="AutoPatchTests"/> and <see cref="LogonReconnectTests"/> each close an
/// unauthenticated <c>LogonSession</c> through its session cap and watch the process-wide <see cref="ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared"/>
/// for the resulting <c>logon/handshake-timeout</c> notice. xUnit runs test classes in parallel by default, so without this
/// collection one test's unauthenticated session closing mid-test could be heard by another test's capture (or vice versa).
/// Parallelization disabled keeps each capture seeing only its own test's notices.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LogonSessionsCollection
{
    public const string Name = "LogonSessions";
}
