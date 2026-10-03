using ArcaneCore.Game.Spells;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>GO7a in the world daemon: the spell focus feature is discovered and installs the cast check on the shared spell system.</summary>
public sealed class SpellFocusWorldTests
{
    [Fact]
    public async Task TheFeatureIsDiscovered_AndRegistersTheCastCheckOnTheSpellSystem()
    {
        Assert.Contains(typeof(SpellFocusFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
        await using WorldTestHost host = WorldTestHost.Start();

        int checks = await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<SpellFeature>().System.CastChecks.OfType<SpellFocusCastCheck>().Count());

        Assert.Equal(1, checks);
    }
}
