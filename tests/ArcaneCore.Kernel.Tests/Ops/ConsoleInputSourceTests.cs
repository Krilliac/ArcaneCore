using ArcaneCore.Kernel.Ops.ServerConsole;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// <see cref="ConsoleInputSource.Default"/> reads the supervisor token and leaves the environment variable in place: the World's cross-realm
/// lifecycle channel reads it from the live environment later, so clearing it would disable that channel under supervision.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class ConsoleInputSourceTests
{
    private static readonly string Variable = SupervisorProtocol.TokenEnvironmentVariable;

    [Fact]
    public void Default_ReadsTheSupervisorToken_AndLeavesTheEnvironmentVariableForLaterReaders()
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, "secret-run-token");

            ConsoleInputSource first = ConsoleInputSource.Default();
            ConsoleInputSource second = ConsoleInputSource.Default();

            Assert.Equal("secret-run-token", first.SupervisorToken);
            Assert.Equal("secret-run-token", second.SupervisorToken);
            Assert.Equal("secret-run-token", Environment.GetEnvironmentVariable(Variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Fact]
    public void Default_WithoutAToken_HasNoToken()
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, null);

            ConsoleInputSource source = ConsoleInputSource.Default();

            Assert.Null(source.SupervisorToken);
            Assert.Null(Environment.GetEnvironmentVariable(Variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }
}
