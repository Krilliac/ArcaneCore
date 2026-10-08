using ArcaneCore.Game.Instances.Scripts;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class RaidRegistrationTests
{
    [Theory]
    [InlineData(409u)]
    [InlineData(249u)]
    public void RaidHasAnInstanceScript(uint map) => Assert.True(InstanceScriptRegistry.Default.HasScript(map));
}
