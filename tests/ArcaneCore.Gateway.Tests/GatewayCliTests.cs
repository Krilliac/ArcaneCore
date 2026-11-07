using ArcaneCore.Kernel.Ops;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// The gateway's command line, run in process through <see cref="GatewayCli"/> (what <c>Program.cs</c> calls): <c>--check-config</c> and
/// <c>check-config</c> validate <c>Gateway:*</c> with <see cref="GatewayOptions.Validate"/> and the link secret rule and exit without starting anything.
/// </summary>
public sealed class GatewayCliTests
{
    private const string GoodSecret = "--Gateway:LinkSecret=0123456789abcdef0123";

    private static (int? Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int? exit = GatewayCli.Prepare(args, stdout, stderr, out _);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("--check-config")]
    [InlineData("check-config")]
    public void CheckConfig_WithABadPort_ExitsInvalidConfiguration_AndNamesTheKey(string verb)
    {
        (int? exit, string stdout, string stderr) = Run(verb, "--Gateway:Port=99999", GoodSecret);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.NotEqual(0, exit);
        Assert.Contains("Gateway:Port", stdout, StringComparison.Ordinal);
        Assert.Contains("invalid", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--check-config")]
    [InlineData("check-config")]
    public void CheckConfig_WithValidValues_ExitsZero(string verb)
    {
        (int? exit, string stdout, _) = Run(verb, "--Gateway:Port=8086", GoodSecret);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Gateway:Port", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckConfig_WithTheDefaultEmptyLinkSecret_Fails_LikeStart()
    {
        (int? exit, string stdout, _) = Run("--check-config");

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("Gateway:LinkSecret", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--Diagnostics:CrashDump:TimeoutSeconds=2000000000", "Diagnostics:CrashDump:TimeoutSeconds")]
    [InlineData("--Diagnostics:CrashDump:TimeoutSeconds=0", "Diagnostics:CrashDump:TimeoutSeconds")]
    [InlineData("--Diagnostics:CrashDump:Mode=Everything", "Diagnostics:CrashDump:Mode")]
    public void CheckConfig_WithABadCrashDumpValue_ExitsInvalidConfiguration_AndNamesTheKey(string argument, string key)
    {
        (int? exit, string stdout, _) = Run("--check-config", GoodSecret, argument);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains(key, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckConfig_ReportsEveryProblemAtOnce()
    {
        (int? exit, string stdout, _) = Run("--check-config", "--Gateway:Port=99999", "--Gateway:WorkerPort=0", "--Gateway:MaxConnections=-1", GoodSecret);

        Assert.Equal(ExitCodes.InvalidConfiguration, exit);
        Assert.Contains("Gateway:Port", stdout, StringComparison.Ordinal);
        Assert.Contains("Gateway:WorkerPort", stdout, StringComparison.Ordinal);
        Assert.Contains("Gateway:MaxConnections", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistypedGatewayOption_IsAUsageError_NotSilentlyDropped()
    {
        (int? exit, _, string stderr) = Run("--Gateway:Prot=8086");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("Gateway:Port", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorldOnlyOption_IsRefused_AsBelongingToAnotherDaemon()
    {
        (int? exit, _, string stderr) = Run("--World:Port=8085");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("belongs to the", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteConfigDefaults_WritesTheGatewayKeys_ToStandardOutput()
    {
        (int? exit, string stdout, _) = Run("write-config", "--defaults", "-");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("ArcaneCore gateway daemon configuration (gateway.conf.dist)", stdout, StringComparison.Ordinal);
        Assert.Contains("Gateway.Port", stdout, StringComparison.Ordinal);
        // The generated header's syntax example mentions World.Port in a comment; a World key line must not appear.
        Assert.DoesNotContain(stdout.Split('\n'), line => line.StartsWith("World.", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_ExitsWithUsage_AndNamesTheGateway()
    {
        (int? exit, string stdout, _) = Run("--help");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("ArcaneCore.Gateway", stdout, StringComparison.Ordinal);
        Assert.Contains("gateway.conf", stdout, StringComparison.Ordinal);
    }
}
