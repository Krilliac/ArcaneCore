using ArcaneCore.Kernel.Ops.ServerConsole;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary><see cref="ConsoleInputSource.Default"/> takes the supervisor token from the environment once and removes it from the process environment.</summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class ConsoleInputSourceTests
{
    private static readonly string Variable = SupervisorProtocol.TokenEnvironmentVariable;

    [Fact]
    public void Default_CapturesTheSupervisorToken_AndClearsTheEnvironmentVariable()
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            ConsoleInputSource.ResetCapturedTokenForTests();
            Environment.SetEnvironmentVariable(Variable, "secret-run-token");

            ConsoleInputSource source = ConsoleInputSource.Default();

            Assert.Equal("secret-run-token", source.SupervisorToken);
            Assert.Null(Environment.GetEnvironmentVariable(Variable));
        }
        finally
        {
            ConsoleInputSource.ResetCapturedTokenForTests();
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Fact]
    public void Default_CalledAgain_ReturnsTheCachedToken_EvenThoughTheVariableIsGone()
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            ConsoleInputSource.ResetCapturedTokenForTests();
            Environment.SetEnvironmentVariable(Variable, "first-token");
            ConsoleInputSource first = ConsoleInputSource.Default();

            ConsoleInputSource second = ConsoleInputSource.Default(); // the variable was cleared by the first call

            Assert.Equal("first-token", first.SupervisorToken);
            Assert.Equal("first-token", second.SupervisorToken);
            Assert.Null(Environment.GetEnvironmentVariable(Variable));
        }
        finally
        {
            ConsoleInputSource.ResetCapturedTokenForTests();
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Fact]
    public void Default_WithoutAToken_HasNoToken_AndLeavesTheVariableUnset()
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            ConsoleInputSource.ResetCapturedTokenForTests();
            Environment.SetEnvironmentVariable(Variable, null);

            ConsoleInputSource source = ConsoleInputSource.Default();

            Assert.Null(source.SupervisorToken);
            Assert.Null(Environment.GetEnvironmentVariable(Variable));
        }
        finally
        {
            ConsoleInputSource.ResetCapturedTokenForTests();
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }
}
