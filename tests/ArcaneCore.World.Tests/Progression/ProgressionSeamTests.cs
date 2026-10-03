using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

/// <summary>Registration of the quest progression seams (docs/integration/quest-progression.md).</summary>
public sealed class ProgressionSeamTests
{
    [Fact]
    public void ProgressionFeature_IsDiscovered_AndHooksCharacterLoading()
    {
        Assert.Contains(typeof(ProgressionFeature), WorldFeatures.FeatureTypes);
        var services = new ServiceCollection().AddWorldFeatures();
        Assert.Contains(services, d => d.ServiceType == typeof(ICharacterHooks)
            && d.ImplementationFactory is not null);
        Assert.True(typeof(ICharacterHooks).IsAssignableFrom(typeof(ProgressionFeature)));
    }

    [Fact]
    public void QuestFeature_IsAnAreaTriggerListenerReachedThroughTheWorldFeatures()
    {
        // Listeners are found among the discovered features, so WorldFeatures.cs stays untouched.
        Assert.True(typeof(IAreaTriggerListener).IsAssignableFrom(typeof(QuestNpcFeature)));
        var services = new ServiceCollection().AddWorldFeatures();
        Assert.Contains(services, d => d.ServiceType == typeof(QuestNpcFeature) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IWorldFeature) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(typeof(QuestNpcFeature), WorldFeatures.FeatureTypes);
    }
}
