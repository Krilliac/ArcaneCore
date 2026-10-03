using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Stats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Stats;

/// <summary>The world daemon wires the feral stat listener into the stance feature, in either attach order.</summary>
public sealed class FormStatFeatureTests
{
    private static ServiceProvider Services(params KeyValuePair<string, string?>[] settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        services.AddSingleton<StanceFeature>();
        services.AddSingleton<FormStatFeature>();
        return services.BuildServiceProvider();
    }

    private static ArcaneCore.Game.Maps.WorldRuntime NewWorld(ServiceProvider sp)
    {
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new ArcaneCore.Game.Maps.WorldRuntime(new ArcaneCore.Game.Maps.WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<ArcaneCore.Game.Maps.WorldRuntime>.Instance);
    }

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(FormStatFeature), WorldFeatures.FeatureTypes);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attach_WorksInEitherOrderWithTheStanceFeature_AndDefaultsToResettingFistAttackTime(bool statsFirst)
    {
        await using ServiceProvider sp = Services();
        using var world = NewWorld(sp);
        var stance = sp.GetRequiredService<StanceFeature>();
        var stats = sp.GetRequiredService<FormStatFeature>();

        if (statsFirst)
        {
            stats.Attach(world);
            stance.Attach(world);
        }
        else
        {
            stance.Attach(world);
            stats.Attach(world);
        }

        Assert.NotNull(stance.Service);
        Assert.NotNull(stats.Listener);
        Assert.True(stats.Listener!.ResetFistAttackTime);
    }

    [Fact]
    public async Task TheVmangosLiteralOption_TurnsTheFistReset_Off()
    {
        await using ServiceProvider sp = Services(new KeyValuePair<string, string?>(FormStatFeature.ResetFistKey, "false"));
        using var world = NewWorld(sp);
        var stats = sp.GetRequiredService<FormStatFeature>();

        sp.GetRequiredService<StanceFeature>().Attach(world);
        stats.Attach(world);

        Assert.False(stats.Listener!.ResetFistAttackTime);
    }
}
