using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Clustering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Tier 3b groundwork (docs/design/hot-reload-tiers.md, section 5.3): in worker mode character fencing is mandatory. The world host
/// validates <see cref="CharacterFencingOptions"/> before anything else at start, so a worker configured with fencing off refuses to
/// start; Combined mode keeps its opt-in fencing.
/// </summary>
public sealed class WorkerModeFencingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void WorkerMode_WithFencingOff_RefusesToValidate(string? fencing)
    {
        CharacterFencingOptions options = Resolve(worker: "true", fencing);

        Assert.True(options.RequiredByWorkerMode);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Cluster:Worker:Enabled requires Cluster:Fencing:Enabled", error.Message);
    }

    [Fact]
    public void WorkerMode_WithFencingOn_Validates()
    {
        CharacterFencingOptions options = Resolve(worker: "true", fencing: "true");

        Assert.True(options.RequiredByWorkerMode);
        Assert.True(options.Enabled);
        options.Validate();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("false", null)]
    [InlineData("false", "false")]
    [InlineData(null, "true")]
    public void CombinedMode_KeepsFencingOptIn(string? worker, string? fencing)
    {
        CharacterFencingOptions options = Resolve(worker, fencing);

        Assert.False(options.RequiredByWorkerMode);
        options.Validate();
    }

    [Fact]
    public void TheRequirement_CannotBeSwitchedOffFromConfiguration()
    {
        CharacterFencingOptions options = Resolve(worker: "true", fencing: null, ("Cluster:Fencing:RequiredByWorkerMode", "false"));

        Assert.True(options.RequiredByWorkerMode);
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task TheWorldHost_RefusesToStartAWorkerWithFencingOff_BeforeTouchingAnything()
    {
        CharacterFencingOptions options = Resolve(worker: "true", fencing: null);
        var host = new WorldHost(
            world: null!, saveQueue: null!, directory: null!, scopes: null!,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorldHost>.Instance,
            fencingOptions: Options.Create(options));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));
        Assert.Contains("Cluster:Worker:Enabled requires Cluster:Fencing:Enabled", error.Message);
    }

    private static CharacterFencingOptions Resolve(string? worker, string? fencing, params (string Key, string? Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Cluster:Worker:Enabled"] = worker,
            ["Cluster:Fencing:Enabled"] = fencing,
        };
        foreach ((string key, string? value) in extra)
            values[key] = value;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<CharacterFencingOptions>(configuration.GetSection(CharacterFencingOptions.SectionName));
        services.AddGatewayWorker(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<CharacterFencingOptions>>().Value;
    }
}
