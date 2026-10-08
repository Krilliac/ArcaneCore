using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

public sealed class DungeonEventFeatureTests
{
    [Fact]
    public async Task TheWorldDiscoversTheDungeonTriggers_AndInstallsTheAvatarSendEventEffect()
    {
        Assert.Contains(typeof(DungeonEventFeature), WorldFeatures.FeatureTypes);
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance))
            .BuildServiceProvider();
        var saves = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves,
            NullLogger<WorldRuntime>.Instance);

        new DungeonEventFeature(services).Attach(world);

        Assert.True(services.GetRequiredService<SpellFeature>().System.HasEffectHandler(SpellEffectName.SendEvent));
    }
}
