using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// The supervisor token is taken out of the environment at the start of every daemon (<see cref="SupervisorProtocol.CaptureProcessToken"/>) and the readers take it from the
/// captured copy: these tests go through the production paths (the production <see cref="HeartbeatWriter"/> constructor, the host's configuration builder, the daemon entry
/// points) instead of injecting an environment, so a reader reverted to the live environment, or a daemon that stops capturing, fails here.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class SupervisorTokenWiringTests
{
    private static readonly string Token = SupervisorProtocol.TokenEnvironmentVariable;

    private static string RepoFile(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "ArcaneCore.Kernel", "ArcaneCore.Kernel.csproj")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.True(dir is not null, "the repository root was not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir!, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void TheProductionHeartbeatWriter_AfterTheTokenWasCaptured_BeatsWithTheToken_ThoughTheVariableIsGone()
    {
        string? previousToken = Environment.GetEnvironmentVariable(Token);
        string? previousProtocol = Environment.GetEnvironmentVariable(SupervisorProtocol.EnvironmentVariable);
        TextWriter previousOut = Console.Out;
        var stdout = new StringWriter();
        try
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Token, "wired-run-token");
            Environment.SetEnvironmentVariable(SupervisorProtocol.EnvironmentVariable, "1");
            SupervisorProtocol.CaptureProcessToken(); // what Main does first
            Assert.Null(Environment.GetEnvironmentVariable(Token));
            Console.SetOut(stdout);

            var writer = new HeartbeatWriter(new WatchdogOptions(), [], new CounterRegistry(), NullLogger<HeartbeatWriter>.Instance); // the constructor the daemons use
            writer.Start();
            writer.Check(1_000_000);

            string[] lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(2, lines.Length);
            Assert.True(SupervisorProtocol.TryParseHeartbeat(lines[0], out SupervisorHeartbeatState ready, out string readyToken), lines[0]);
            Assert.True(SupervisorProtocol.TryParseHeartbeat(lines[1], out SupervisorHeartbeatState alive, out string aliveToken), lines[1]);
            Assert.Equal(SupervisorHeartbeatState.Ready, ready);
            Assert.Equal(SupervisorHeartbeatState.Alive, alive);
            Assert.Equal("wired-run-token", readyToken);
            Assert.Equal("wired-run-token", aliveToken);
        }
        finally
        {
            Console.SetOut(previousOut);
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Token, previousToken);
            Environment.SetEnvironmentVariable(SupervisorProtocol.EnvironmentVariable, previousProtocol);
        }
    }

    [Fact]
    public void TheHostConfigurationBuilder_NeverSeesTheToken_OnceItWasCaptured_ButDoesWithoutTheCapture()
    {
        string? previous = Environment.GetEnvironmentVariable(Token);
        try
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Token, "config-run-token");

            // Control: the builder copies the environment, so without the capture the secret is in IConfiguration (and so in every config dump and binder).
            Assert.Equal("config-run-token", Host.CreateApplicationBuilder().Configuration[Token]);

            SupervisorProtocol.CaptureProcessToken();

            Assert.Null(Host.CreateApplicationBuilder().Configuration[Token]);
            Assert.Equal("config-run-token", SupervisorProtocol.ProcessEnvironment(Token));
        }
        finally
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Token, previous);
        }
    }

    [Fact]
    public void ANativeGrandchild_StartedAfterTheCapture_DoesNotInheritTheToken()
    {
        string? previous = Environment.GetEnvironmentVariable(Token);
        try
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Token, "grandchild-token");
            SupervisorProtocol.CaptureProcessToken();

            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            Assert.False(info.Environment.ContainsKey(Token), "a process started now would inherit the supervisor token");
        }
        finally
        {
            SupervisorProtocol.ResetCapturedToken();
            Environment.SetEnvironmentVariable(Token, previous);
        }
    }

    // Each daemon captures the token as its first statement, before the command line, the configuration builder or any child process exists.
    [Theory]
    [InlineData("src/ArcaneCore.World/Program.cs", "DaemonCommandLine.Parse(")]
    [InlineData("src/ArcaneCore.Realm/Program.cs", "DaemonCommandLine.Parse(")]
    [InlineData("src/ArcaneCore.Gateway/Program.cs", "GatewayCli.Prepare(")]
    public void EveryDaemonEntryPoint_CapturesTheToken_BeforeItBuildsAnything(string program, string firstUse)
    {
        string text = File.ReadAllText(RepoFile(program));

        int capture = text.IndexOf("SupervisorProtocol.CaptureProcessToken();", StringComparison.Ordinal);
        int build = text.IndexOf(firstUse, StringComparison.Ordinal);

        Assert.True(capture >= 0, program + " never captures the supervisor token");
        Assert.True(build > capture, program + " reads its command line or builds its host before it captures the supervisor token");
    }

    // The readers must keep taking the token from the captured copy: a reader on the live environment would see null once the variable is removed, so every beat and
    // stopping line of a supervised child would be token-less and rejected.
    [Theory]
    [InlineData("src/ArcaneCore.Kernel/Ops/Watchdog/Heartbeat/HeartbeatWriter.cs")]
    [InlineData("src/ArcaneCore.World/Ops/Lifecycle/SupervisorProtocolChannel.cs")]
    public void TheTokenReaders_UseTheCapturedCopy_NotTheLiveEnvironment(string file)
    {
        string text = File.ReadAllText(RepoFile(file));

        Assert.Contains("SupervisorProtocol.ProcessEnvironment", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", text, StringComparison.Ordinal);
    }
}
