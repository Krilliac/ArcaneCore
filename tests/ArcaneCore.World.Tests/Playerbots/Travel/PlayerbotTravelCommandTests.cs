using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary><c>.playerbot travel $name #map #x #y #z</c>: a running bot begins a journey through the travel service and the reply lists its legs.</summary>
public sealed class PlayerbotTravelCommandTests
{
    /// <summary>The travel seam as production registers it (<c>WorldServiceCollectionExtensions</c>), over the host's own bot options.</summary>
    private static Task<FleetWorld> StartAsync() => FleetWorld.StartAsync(configureServices: services =>
        services.AddSingleton<IPlayerbotTravel>(sp => new PlayerbotTravel(sp.GetRequiredService<IOptions<PlayerbotOptions>>().Value)));

    private static string Place(float x, float y, float z) => string.Create(CultureInfo.InvariantCulture, $"{x} {y} {z}");

    [Fact]
    public async Task Travel_BeginsAJourneyForTheBot_AndPrintsItsLegs()
    {
        await using FleetWorld world = await StartAsync();
        await world.AddBotAsync("Travelcmd", 0);
        float x = FleetWorld.BaseX + 300f, y = FleetWorld.BaseY, z = FleetWorld.BaseZ;
        Assert.NotNull(world.Host.WorldServices.GetService<IPlayerbotTravel>());

        string[] lines = await world.Console.RunAsync($".playerbot travel Travelcmd 0 {Place(x, y, z)}");

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Playerbot travel: Travelcmd to map 0 (-7700,1000,100): 1 leg(s), about ", lines[0]);
        Assert.StartsWith("1. Walk to map 0 (-7700,1000,100)", lines[1]);
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = world.Host.World.FindOnlinePlayer("Travelcmd")!;
            TravelJourney journey = PlayerbotTravel.JourneyOf(bot)!;
            Assert.Equal(new TravelPlace(0, new System.Numerics.Vector3(x, y, z)), journey.Goal);
            Assert.Equal([TravelMeans.Walk], journey.Planned.Legs.Select(l => l.Means));
            return 0;
        });
    }

    [Fact]
    public async Task Travel_Refusals_SayWhy_AndBadArgumentsShowTheSyntax()
    {
        await using FleetWorld world = await StartAsync();
        await world.AddBotAsync("Travelref", 0);

        string[] map = await world.Console.RunAsync($".playerbot travel Travelref 530 {Place(0f, 0f, 0f)}");
        string[] unknown = await world.Console.RunAsync($".playerbot travel Nobodybot 0 {Place(0f, 0f, 0f)}");
        string[] syntax = await world.Console.RunAsync(".playerbot travel Travelref 0 1");

        Assert.Equal(["Playerbot travel refused: Travelref: travel: bots may not walk map 530"], map);
        Assert.Equal(["Playerbot not found."], unknown);
        Assert.Contains(syntax, l => l.Contains(".playerbot travel $id|$name #map #x #y #z", StringComparison.Ordinal));
        await world.Host.OnWorldAsync(() =>
        {
            Assert.Null(PlayerbotTravel.JourneyOf(world.Host.World.FindOnlinePlayer("Travelref")!));
            return 0;
        });
    }
}
