using ArcaneCore.Kernel.Ops.ServerConsole;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// <see cref="ConsoleInputSource.Default"/> takes the supervisor token through <see cref="SupervisorProtocol.CaptureProcessToken"/>: the variable is
/// removed from the environment (processes this one starts must not inherit it) and every later reader still gets the token from the captured copy.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class ConsoleInputSourceTests
{
    private static readonly string Variable = SupervisorProtocol.TokenEnvironmentVariable;

    [Fact]
    public void Default_TakesTheSupervisorToken_RemovesTheEnvironmentVariable_AndLaterReadersStillSeeIt()
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Variable, "secret-run-token");

            ConsoleInputSource first = ConsoleInputSource.Default();
            ConsoleInputSource second = ConsoleInputSource.Default();

            Assert.Equal("secret-run-token", first.SupervisorToken);
            Assert.Equal("secret-run-token", second.SupervisorToken);
            Assert.Null(Environment.GetEnvironmentVariable(Variable));
            Assert.Equal("secret-run-token", SupervisorProtocol.ProcessEnvironment(Variable));
            Assert.Equal("secret-run-token", SupervisorProtocol.CaptureProcessToken());
        }
        finally
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    [Fact]
    public void ProcessEnvironment_PassesEveryOtherVariableThrough()
    {
        const string other = "ARCANECORE_TEST_PROCESS_ENVIRONMENT_PASSTHROUGH";
        try
        {
            Environment.SetEnvironmentVariable(other, "value");

            Assert.Equal("value", SupervisorProtocol.ProcessEnvironment(other));
        }
        finally
        {
            Environment.SetEnvironmentVariable(other, null);
        }
    }

    [Fact]
    public void Default_WithoutAToken_HasNoToken()
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Variable, null);

            ConsoleInputSource source = ConsoleInputSource.Default();

            Assert.Null(source.SupervisorToken);
            Assert.Null(Environment.GetEnvironmentVariable(Variable));
        }
        finally
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }
}
