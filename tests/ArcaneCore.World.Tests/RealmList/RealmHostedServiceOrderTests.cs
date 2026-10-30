using ArcaneCore.Data;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.World.Tests.RealmList;

/// <summary>
/// The generic host starts hosted services in registration order and stops them in reverse. The realm-list row must say Starting before the
/// world host starts (so the row is Offline while the world loads), and the clean-stop write must not come before the listener has stopped
/// accepting connections and the world has torn down (docs/ops/realm-liveness.md). The order is read from the real service collection
/// (<see cref="WorldServiceCollectionExtensions.AddWorldDaemon"/>), so a reordered registration there fails here.
/// </summary>
public sealed class RealmHostedServiceOrderTests
{
    [Fact]
    public async Task RealmLiveness_IsRegisteredBeforeTheWorldHost_AndTheListener_SoItStartsFirstAndStopsLast()
    {
        using var directory = new TempDirectory("arcanecore-realmorder-", sqlite: true);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={Path.Combine(directory.Path, "characters.db")}",
            }).Build();
        var collection = new ServiceCollection().AddSingleton(configuration).AddLogging().AddCharacterDatabase(configuration).AddWorldDaemon(configuration);

        // The descriptors, in registration order: one hosted-service entry each for the liveness writer, the world host and the listener.
        List<ServiceDescriptor> descriptors = collection.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        Assert.Single(collection, d => d.ServiceType == typeof(RealmLivenessService));

        await using ServiceProvider provider = collection.BuildServiceProvider();
        IHostedService[] hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(descriptors.Count, hosted.Length); // GetServices keeps registration order: hosted[i] is descriptors[i]

        int liveness = IndexOf<RealmLivenessService>(hosted);
        int worldHost = IndexOf<WorldHost>(hosted);
        int listener = IndexOf<WorldServer>(hosted);

        Assert.Same(provider.GetRequiredService<RealmLivenessService>(), hosted[liveness]); // the hosted entry is the same instance the status feature drives
        Assert.True(liveness < worldHost, $"RealmLivenessService ({liveness}) must start before WorldHost ({worldHost}) so the row says Starting while the world loads");
        Assert.True(worldHost < listener, $"WorldHost ({worldHost}) must start before WorldServer ({listener}) and so stop after it");

        // Reverse order is the stop order: listener, then world teardown (its BeforeWorldStop hook stops liveness and writes Stopped), then the liveness
        // hosted entry itself, whose StopAsync is then a no-op.
        IHostedService[] stopOrder = hosted.Reverse().ToArray();
        Assert.True(Array.IndexOf(stopOrder, hosted[listener]) < Array.IndexOf(stopOrder, hosted[worldHost]));
        Assert.True(Array.IndexOf(stopOrder, hosted[worldHost]) < Array.IndexOf(stopOrder, hosted[liveness]));

        // The status feature is not a hosted service: it is attached by WorldHost, which is what makes its before-stop hook run inside WorldHost.StopAsync.
        Assert.Contains(typeof(RealmStatusFeature), WorldFeatures.FeatureTypes);
    }

    private static int IndexOf<T>(IHostedService[] hosted)
    {
        int index = Array.FindIndex(hosted, h => h is T);
        Assert.True(index >= 0, typeof(T).Name + " is registered as a hosted service");
        Assert.Single(hosted, h => h is T);
        return index;
    }
}
