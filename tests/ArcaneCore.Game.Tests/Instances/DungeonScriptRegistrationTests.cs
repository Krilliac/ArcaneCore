using ArcaneCore.Game.Instances.Scripts;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class DungeonScriptRegistrationTests
{
    [Theory]
    [InlineData(90u)]  // Gnomeregan
    [InlineData(189u)] // Scarlet Monastery
    [InlineData(129u)] // Razorfen Downs
    [InlineData(47u)]  // Razorfen Kraul
    [InlineData(70u)]  // Uldaman
    [InlineData(209u)] // Zul'Farrak
    public void ScriptDev2DungeonHasInstanceData(uint mapId)
        => Assert.True(InstanceScriptRegistry.Default.HasScript(mapId));
}
