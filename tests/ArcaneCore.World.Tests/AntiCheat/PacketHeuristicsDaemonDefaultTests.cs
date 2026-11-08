using ArcaneCore.World.AntiCheat.PacketHeuristics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// The packet heuristics default as the daemon registers it (<c>AddWorldDaemon</c> binds <c>World:PacketHeuristics</c>): on with no
/// section and with an empty one, off only when the section says <c>Enabled=false</c>. The options class and the feature are both read
/// through the container, so a registration that stopped binding the section would show here.
/// </summary>
public sealed class PacketHeuristicsDaemonDefaultTests
{
    private static ServiceProvider Daemon(params (string Key, string? Value)[] values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddWorldDaemon(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WithNoSection_TheDaemonRunsTheChecks()
    {
        using ServiceProvider provider = Daemon();

        Assert.True(provider.GetRequiredService<IOptions<PacketHeuristicsOptions>>().Value.Enabled);
        Assert.True(new PacketHeuristicsFeature(provider).Options.Enabled);
    }

    [Fact]
    public void WithAnEmptySection_TheDaemonRunsTheChecks()
    {
        using ServiceProvider provider = Daemon(("World:PacketHeuristics", ""));

        Assert.True(provider.GetRequiredService<IOptions<PacketHeuristicsOptions>>().Value.Enabled);
        Assert.True(new PacketHeuristicsFeature(provider).Options.Enabled);
    }

    [Fact]
    public void WithAnUnrelatedKeyInTheSection_TheDaemonStillRunsTheChecks()
    {
        using ServiceProvider provider = Daemon(("World:PacketHeuristics:ReportIntervalSeconds", "30"));

        PacketHeuristicsOptions options = provider.GetRequiredService<IOptions<PacketHeuristicsOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(30, options.ReportIntervalSeconds);
    }

    [Fact]
    public void WithExplicitFalse_TheDaemonSwitchesTheChecksOff()
    {
        using ServiceProvider provider = Daemon(("World:PacketHeuristics:Enabled", "false"));

        Assert.False(provider.GetRequiredService<IOptions<PacketHeuristicsOptions>>().Value.Enabled);
        Assert.False(new PacketHeuristicsFeature(provider).Options.Enabled);
    }
}
