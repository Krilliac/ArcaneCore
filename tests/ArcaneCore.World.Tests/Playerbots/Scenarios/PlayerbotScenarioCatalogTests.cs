using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// What <c>.playerbot scenario list|run</c> knows (<see cref="PlayerbotScenarioCatalog"/>): the built-ins, every public scenario this
/// assembly ships (the battlegrounds review found <c>wsg</c> unreachable from the command), and the scenarios registered in DI.
/// </summary>
public sealed class PlayerbotScenarioCatalogTests
{
    private static IServiceProvider NoServices() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public void Wsg_IsRunnableByName_WithoutAnyRegistration()
        => Assert.IsType<WarsongGulchScenario>(PlayerbotScenarioCatalog.Find(NoServices(), "wsg"));

    [Fact]
    public void EveryPublicScenarioOfTheWorldAssembly_IsListed_OnceByName()
    {
        Type[] shipped = [.. typeof(IPlayerbotScenario).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IPlayerbotScenario).IsAssignableFrom(t))];
        IReadOnlyList<IPlayerbotScenario> all = PlayerbotScenarioCatalog.All(NoServices());

        Assert.Contains(typeof(WarsongGulchScenario), shipped);
        Assert.All(shipped, type => Assert.Contains(all, scenario => scenario.GetType() == type));
        Assert.Equal(all.Count, all.Select(s => s.Name.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(PlayerbotScenarioCatalog.Builtins.Select(s => s.Name), all.Take(PlayerbotScenarioCatalog.Builtins.Count).Select(s => s.Name));
    }

    [Fact]
    public void ARegisteredScenario_WithAShippedName_DoesNotReplaceTheShippedOne()
    {
        IServiceProvider services = new ServiceCollection()
            .AddSingleton<IPlayerbotScenario>(new DelegateScenario("wsg", _ => Task.CompletedTask))
            .AddSingleton<IPlayerbotScenario>(new DelegateScenario("extra", _ => Task.CompletedTask))
            .BuildServiceProvider();

        Assert.IsType<WarsongGulchScenario>(PlayerbotScenarioCatalog.Find(services, "wsg"));
        Assert.IsType<DelegateScenario>(PlayerbotScenarioCatalog.Find(services, "extra"));
    }
}
