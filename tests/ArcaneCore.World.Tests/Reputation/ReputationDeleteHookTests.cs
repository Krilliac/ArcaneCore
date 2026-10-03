using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Reputation;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>Reputation takes part in character deletion through the discovered <see cref="ICharacterDeleteHook"/> seam.</summary>
public sealed class ReputationDeleteHookTests
{
    [Fact]
    public void ReputationDeleteHook_IsDiscovered()
        => Assert.Contains(typeof(ReputationCharacterDeleteHook),
            WorldFeatures.FeatureTypes.Where(typeof(ICharacterDeleteHook).IsAssignableFrom));
}
