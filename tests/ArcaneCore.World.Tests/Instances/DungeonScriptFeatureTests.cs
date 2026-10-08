using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Teleport;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

public sealed class DungeonScriptFeatureTests
{
    [Fact]
    public void DungeonAdaptersAreDiscoveredAsAreaTriggerListeners()
    {
        Assert.Contains(typeof(DungeonScriptFeature), WorldFeatures.FeatureTypes);
        Assert.True(typeof(IAreaTriggerListener).IsAssignableFrom(typeof(DungeonScriptFeature)));
    }
}
