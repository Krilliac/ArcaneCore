using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The daemon's transform display source reads the creature data (mangoszero HandleAuraTransform, SpellAuraShapeshift.cpp:548-562).</summary>
public sealed class TransformDisplayFeatureTests
{
    [Fact]
    public async Task TheFeature_ResolvesATransformSpellsCreatureToItsDisplay_AndAnUnknownOneToNull()
    {
        await using var host = WorldTestHost.Start();
        var sheep = new CreatureTemplate { Entry = 7, Name = "Sheep", DisplayIds = [856], DisplayProbabilities = [100] };
        var content = new CreatureContent([sheep], [], [], [], []);
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(content));

        TransformDisplayFeature feature = host.WorldServices.GetRequiredService<TransformDisplayFeature>();

        Assert.Equal(856u, feature.FindDisplay(7));
        Assert.Null(feature.FindDisplay(99999));
        Assert.Same(feature, host.WorldServices.GetServices<ArcaneCore.World.Features.IWorldFeature>().OfType<TransformDisplayFeature>().Single());
        feature.ReportNoModel(1234); // logs; nothing to assert but that it does not throw
    }

    [Fact]
    public async Task TheFeature_RegistersItselfAsTheWorldsTransformDisplaySource()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SHEEP", "Sheepish");

        ArcaneCore.Game.Entities.Player player = await host.PlayerAsync("Sheepish");
        ITransformDisplaySource? source = await host.OnWorldAsync(() => TransformDisplays.For(player.Map));

        Assert.Same(host.WorldServices.GetRequiredService<TransformDisplayFeature>(), source);
    }
}
