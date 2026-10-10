using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// The standard deployment runs the Realm and the World from one profile directory with one appsettings.json, so both
/// daemons read the same <c>Ops:Metrics</c> section. Each must resolve its own Prometheus endpoint from it
/// (docs/ops/metrics.md): the World <c>Ops:Metrics:PrometheusPrefix</c>, the Realm <c>Ops:Metrics:RealmPrometheusPrefix</c>.
/// </summary>
public sealed class MetricsRoleTests
{
    private static MetricsHost HostFor(Dictionary<string, string?> settings, MetricsRole role, ILogger<MetricsHost>? logger = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (logger is not null)
        {
            services.AddSingleton(logger);
        }

        services.AddOpsMetrics(configuration, role == MetricsRole.Realm ? "arcanecore-realm" : "arcanecore-world", role);
        return services.BuildServiceProvider().GetRequiredService<MetricsHost>();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public void SharedConfiguration_RealmAndWorld_ResolveDistinctPrometheusPrefixes()
    {
        var shared = new Dictionary<string, string?> { ["Ops:Metrics:Enabled"] = "true" };

        string world = HostFor(shared, MetricsRole.World).ConfiguredPrometheusPrefix;
        string realm = HostFor(shared, MetricsRole.Realm).ConfiguredPrometheusPrefix;

        Assert.Equal("http://localhost:9464/metrics/", world);
        Assert.Equal("http://localhost:9465/metrics/", realm);
    }

    [Fact]
    public void SharedConfiguration_WorldPrefixOverride_DoesNotMoveTheRealm()
    {
        var shared = new Dictionary<string, string?>
        {
            ["Ops:Metrics:Enabled"] = "true",
            ["Ops:Metrics:PrometheusPrefix"] = "http://+:9500/metrics/",
        };

        Assert.Equal("http://+:9500/metrics/", HostFor(shared, MetricsRole.World).ConfiguredPrometheusPrefix);
        Assert.Equal("http://localhost:9465/metrics/", HostFor(shared, MetricsRole.Realm).ConfiguredPrometheusPrefix);
    }

    [Fact]
    public void SharedConfiguration_RealmPrefixOverride_DoesNotMoveTheWorld()
    {
        var shared = new Dictionary<string, string?>
        {
            ["Ops:Metrics:Enabled"] = "true",
            ["Ops:Metrics:RealmPrometheusPrefix"] = "http://+:9501/metrics/",
        };

        Assert.Equal("http://localhost:9464/metrics/", HostFor(shared, MetricsRole.World).ConfiguredPrometheusPrefix);
        Assert.Equal("http://+:9501/metrics/", HostFor(shared, MetricsRole.Realm).ConfiguredPrometheusPrefix);
    }

    [Fact]
    public void Validation_BadRealmPrefix_IsAnErrorNamingTheRealmKey()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:Metrics:Enabled"] = "true",
            ["Ops:Metrics:RealmPrometheusPrefix"] = "localhost:9465",
        }).Build();

        ConfigIssue issue = Assert.Single(new MetricsOptionsValidation().Check(config));
        Assert.Equal("Ops:Metrics:RealmPrometheusPrefix", issue.Key);
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
    }

    [Fact]
    public async Task SamePrefixForBothRoles_SecondFails_AndTheLogNamesTheOtherDaemonAndTheKey()
    {
        string prefix = $"http://127.0.0.1:{FreePort()}/metrics/";
        var shared = new Dictionary<string, string?>
        {
            ["Ops:Metrics:Enabled"] = "true",
            ["Ops:Metrics:PrometheusPrefix"] = prefix,
            ["Ops:Metrics:RealmPrometheusPrefix"] = prefix,
        };

        MetricsHost realm = HostFor(shared, MetricsRole.Realm);
        var log = new Capturing();
        MetricsHost world = HostFor(shared, MetricsRole.World, log);
        await realm.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(prefix, realm.PrometheusPrefix);
            await world.StartAsync(CancellationToken.None);
            Assert.Null(world.PrometheusPrefix);
            string error = Assert.Single(log.Entries, e => e.StartsWith("Error", StringComparison.Ordinal));
            Assert.Contains(prefix, error, StringComparison.Ordinal);
            Assert.Contains("could not start", error, StringComparison.Ordinal);
            Assert.Contains("Realm", error, StringComparison.Ordinal);
            Assert.Contains("Ops:Metrics:PrometheusPrefix", error, StringComparison.Ordinal);
            await world.StopAsync(CancellationToken.None);
        }
        finally
        {
            await realm.StopAsync(CancellationToken.None);
            realm.Dispose();
            world.Dispose();
        }
    }

    private sealed class Capturing : ILogger<MetricsHost>
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(logLevel + ": " + formatter(state, exception));
            }
        }
    }
}
