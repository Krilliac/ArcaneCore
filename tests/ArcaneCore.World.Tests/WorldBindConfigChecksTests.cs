using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests;

public sealed class WorldBindConfigChecksTests
{
    private static List<ConfigIssue> Run(params (string Key, string? Value)[] pairs)
        => [.. new WorldBindConfigChecks().Check(new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value)).Build())];

    [Theory]
    [InlineData("localhost")]
    [InlineData("")]
    public void WorkerBindAddressThatIsNotAnIp_IsAnError_WhenWorkerModeIsOn(string value)
        => Assert.Contains(Run(("Cluster:Worker:Enabled", "true"), ("Cluster:Worker:BindAddress", value)),
            i => i.Key == "Cluster:Worker:BindAddress" && i.Severity == ConfigSeverity.Error);

    [Fact]
    public void WorkerBindAddress_IsNotChecked_WhileWorkerModeIsOff()
        => Assert.Empty(Run(("Cluster:Worker:BindAddress", "localhost")));

    [Fact]
    public void ValidOrMissingWorkerBindAddress_Passes()
    {
        Assert.Empty(Run(("Cluster:Worker:Enabled", "true"), ("Cluster:Worker:BindAddress", "127.0.0.1")));
        Assert.Empty(Run(("Cluster:Worker:Enabled", "true")));
    }
}
