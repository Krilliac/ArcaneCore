using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Locomotion;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>The daemon's mount display source reads the creature data (vmangos HandleAuraMounted, SpellAuras.cpp:2257-2268).</summary>
public sealed class MountEndToEndTests
{
    [Fact]
    public async Task TheFeature_ResolvesAMountSpellsCreatureToItsDisplay_AndRefusesAnUnknownOne()
    {
        await using var host = WorldTestHost.Start();
        var horse = new CreatureTemplate { Entry = 2402, Name = "Brown Horse", DisplayIds = [14337], DisplayProbabilities = [100] };
        var content = new CreatureContent([horse], [], [], [], []);
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(content));

        MountDisplayFeature feature = host.WorldServices.GetRequiredService<MountDisplayFeature>();

        Assert.Equal(14337u, feature.FindMountDisplay(2402));
        Assert.Null(feature.FindMountDisplay(99999));
        Assert.Same(feature, host.WorldServices.GetServices<ArcaneCore.World.Features.IWorldFeature>().OfType<MountDisplayFeature>().Single());
    }

    [Fact]
    public async Task TheFeature_RegistersItselfAsTheWorldsMountDisplaySource()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("RIDER", "Rider");

        ArcaneCore.Game.Entities.Player player = await host.PlayerAsync("Rider");
        Assert.NotNull(await host.OnWorldAsync(() => LocomotionEnvironment.MountDisplaysFor(player.Map)));
    }
}
