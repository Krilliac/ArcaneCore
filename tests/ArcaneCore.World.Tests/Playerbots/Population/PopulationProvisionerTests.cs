using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.World.Creatures;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Population;

public sealed class PopulationProvisionerTests
{
    [Fact]
    public async Task UnsafeProvisionIsRefusedBeforeBoost_AndRelocateChecksTheDestination()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0,
            configureServices: services =>
            {
                services.AddSingleton(new FactionTemplateCatalog([
                    new FactionTemplateRecord(1, 1, 72, 3, 2, 12),
                    new FactionTemplateRecord(14, 14, 0, 8, 0, 1),
                ]));
                services.AddSingleton<ICreatureDataStore>(new HostileStore());
            });
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Safetyone", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
        var unsafeAt = new WorldSafeLoc(1, 0, -8913.23f, -133f, 81f, 0f, "unsafe");
        var safeAt = new WorldSafeLoc(9901, 0, -8200f, -700f, 90f, 0f, "safe");

        await world.Host.World.InvokeAsync(() =>
        {
            var provisioner = new PopulationProvisioner(world.Host.WorldServices);
            PopulationPlacementFacts facts = FactsFor(world, unsafeAt, safeAt);
            Assert.Equal("placement-unsafe", provisioner.Provision(id, 38, unsafeAt, facts));
            Assert.Equal((byte)1, world.Bots.FindSession(id)!.Player!.Level);
            Assert.Equal("placement-unsafe", provisioner.Relocate(id, unsafeAt, facts));
            Assert.Null(provisioner.Relocate(id, safeAt, facts));
            return true;
        });
    }

    [Fact]
    public async Task TeleportTo_MovesTheBotOnSuccess_AndAreaLevelAboveTheBotRefusesWithoutMoving()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0);
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Areaone", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
        var low = new WorldSafeLoc(9902, 0, -8200f, -700f, 90f, 0f, "low area");
        var high = new WorldSafeLoc(9903, 0, -8100f, -650f, 90f, 0f, "high area");

        await world.Host.World.InvokeAsync(() =>
        {
            var provisioner = new PopulationProvisioner(world.Host.WorldServices)
            {
                AreaLevelSource = at => at.Id == high.Id ? 30 : 4, // level one tolerates a starting area of 5 at most
            };
            PopulationPlacementFacts facts = FactsFor(world, high, low);
            Player bot = world.Bots.FindSession(id)!.Player!;
            Assert.Equal("placement-unsafe", provisioner.Relocate(id, high, facts));
            Assert.NotEqual(high.X, bot.X);
            Assert.Null(provisioner.Relocate(id, low, facts));
            return true;
        });
        // A same-map teleport lands on a following world tick.
        await Task.Delay(300);
        await world.Host.World.InvokeAsync(() =>
        {
            Player bot = world.Bots.FindSession(id)!.Player!;
            Assert.Equal(low.X, bot.X, 1);
            Assert.Equal(low.Y, bot.Y, 1);
            return true;
        });
    }

    [Fact]
    public async Task WithoutAFactionCatalog_ANearbyHostileIsUnsafe_AndFarAwayOnesAreNot()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0,
            configureServices: services => services.AddSingleton<ICreatureDataStore>(new HostileStore()));
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Nofaction", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
        var near = new WorldSafeLoc(1, 0, -8913.23f, -133f, 81f, 0f, "near");
        var far = new WorldSafeLoc(9901, 0, -8200f, -700f, 90f, 0f, "far");

        await world.Host.World.InvokeAsync(() =>
        {
            var provisioner = new PopulationProvisioner(world.Host.WorldServices) { AreaLevelSource = _ => 0 };
            PopulationPlacementFacts facts = FactsFor(world, near, far);
            Assert.Equal("placement-unsafe", provisioner.Relocate(id, near, facts));
            Assert.Null(provisioner.Relocate(id, far, facts));
            return true;
        });
    }

    /// <summary>The placement facts for the host's current creature content and area table (no terrain tiles), built inline for a direct call.</summary>
    internal static PopulationPlacementFacts FactsFor(PopulationTestHost world, params WorldSafeLoc[] sites)
        => PopulationPlacementFacts.Build(world.Host.WorldServices.GetService<CreatureWorldFeature>()?.Content ?? CreatureContent.Empty,
            WorldMaps.Of(world.Host.World).Terrain.Areas, null, sites, CancellationToken.None);

    private sealed class HostileStore : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = 990001, Name = "test hostile", Faction = 14, MinLevel = 50, MaxLevel = 50,
                MinLevelHealth = 300, MaxLevelHealth = 300, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 990001, Entry = 990001, MapId = 0, X = -8880f, Y = -133f, Z = 81f }],
            [], [], []));
    }
}
