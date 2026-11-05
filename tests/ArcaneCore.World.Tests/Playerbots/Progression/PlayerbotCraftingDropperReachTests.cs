using System.Numerics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Tests.Playerbots.Opportunities;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Opportunities.OpportunityWorld;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// A creature that drops a missing crafting reagent is hunted only if a walk can reach one of its spawns (GitHub #1441: a dropper on another
/// landmass was picked again and again). <see cref="PlayerbotCrafting.AllSpawnsUnreachable"/> is the pure filter;
/// <see cref="PlayerbotCrafting.FirstUsableDropper"/> is the picker's loop, with reachability injected (the real answer is
/// <c>PlayerbotNavigation.IsUnreachableOnFoot</c>).
/// </summary>
[Collection(PlayerbotCraftingCollection.Name)]
public sealed class PlayerbotCraftingDropperReachTests
{
    private const uint Far = 7001, Near = 7002;

    private static CreatureSpawn Spawn(uint guid, uint entry, float x) => new() { Guid = guid, Entry = entry, MapId = 0, X = x, Y = 0f, Z = 0f };

    private static CreatureTemplate Template(uint entry) => new()
    {
        Entry = entry, Name = "Dropper" + entry, MinLevel = 1, MaxLevel = 5, MinLevelHealth = 1, MaxLevelHealth = 1, Faction = 35,
    };

    /// <summary>Far: two spawns east of x=1000 (another landmass for the probe); Near: one spawn at x=10.</summary>
    private static CreatureContent Content() => new(
        [Template(Far), Template(Near)],
        [Spawn(1, Far, 2000f), Spawn(2, Far, 2100f), Spawn(3, Near, 10f)],
        [], [], []);

    private static async Task<(WorldTestHost Host, Bot Bot)> StartAsync()
    {
        WorldTestHost host = Start([], out _, manualClock: true);
        Bot bot = await EnterAsync(host, "DROPBOT", "Dropone", 1, new PlayerbotOptions { Enabled = true }, new PlayerbotClaims(8), () => 0);
        return (host, bot);
    }

    private static async Task<T> OnWorld<T>(WorldTestHost host, Func<T> work) => await host.OnWorldAsync(work);

    private static async Task StopAsync(WorldTestHost host, Bot bot)
    {
        bot.Brain.Stop();
        bot.Session.Kick();
        await bot.Session.ManagedClosed;
        await host.DisposeAsync();
    }

    [Fact]
    public void ADropperWithEverySpawnUnreachable_IsSkipped()
    {
        CreatureSpawn[] spawns = [Spawn(1, Far, 1000f), Spawn(2, Far, 1100f)];

        Assert.True(PlayerbotCrafting.AllSpawnsUnreachable(spawns, at => at.X >= 1000f));
    }

    [Fact]
    public void ADropperWithOneReachableSpawn_IsKept()
    {
        CreatureSpawn[] spawns = [Spawn(1, Far, 1000f), Spawn(2, Far, 10f)];

        Assert.False(PlayerbotCrafting.AllSpawnsUnreachable(spawns, at => at.X >= 1000f));
    }

    [Fact]
    public void ADropperWithNoSpawn_IsNotJudgedHere_AndTheReachabilityIsNeverAsked()
    {
        int asked = 0;

        Assert.False(PlayerbotCrafting.AllSpawnsUnreachable([], _ => { asked++; return true; }));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheSearch_StopsAtTheFirstReachableSpawn_AndAsksWithTheSpawnsPosition()
    {
        var seen = new List<Vector3>();
        CreatureSpawn[] spawns = [Spawn(1, Near, 5f), Spawn(2, Near, 6f)];

        Assert.False(PlayerbotCrafting.AllSpawnsUnreachable(spawns, at => { seen.Add(at); return false; }));

        Assert.Equal([new Vector3(5f, 0f, 0f)], seen);
    }

    [Fact]
    public async Task ThePicker_SkipsADropperWhoseEverySpawnIsInAnotherWalkingComponent_AndTakesTheNext()
    {
        (WorldTestHost host, Bot bot) = await StartAsync();
        try
        {
            uint picked = await OnWorld(host, () =>
            {
                var crafting = new PlayerbotCrafting(bot.Session, bot.Options) { ReachabilityProbe = at => at.X >= 1000f };
                return crafting.FirstUsableDropper(bot.Player, [Far, Near], Content(), 1_000);
            });

            Assert.Equal(Near, picked);
        }
        finally
        {
            await StopAsync(host, bot);
        }
    }

    [Fact]
    public async Task AnUnreachableDropper_IsNotRescannedOnTheNextThink_UntilTheRecheckWindowEnds()
    {
        (WorldTestHost host, Bot bot) = await StartAsync();
        try
        {
            await OnWorld(host, () =>
            {
                int asked = 0;
                var crafting = new PlayerbotCrafting(bot.Session, bot.Options) { ReachabilityProbe = at => { asked++; return at.X >= 1000f; } };
                CreatureContent content = Content();

                crafting.FirstUsableDropper(bot.Player, [Far, Near], content, 1_000);
                Assert.Equal(3, asked); // both Far spawns, then Near's one
                crafting.FirstUsableDropper(bot.Player, [Far, Near], content, 2_000);
                Assert.Equal(4, asked); // Far is remembered: only Near is asked again
                crafting.FirstUsableDropper(bot.Player, [Far, Near], content, 1_000 + PlayerbotCrafting.UnreachableRecheckMs + 1);
                Assert.Equal(7, asked); // the window is over: Far is walked again
                return true;
            });
        }
        finally
        {
            await StopAsync(host, bot);
        }
    }

    [Fact]
    public async Task WithNoNavigationMesh_NoDropperIsCalledUnreachable()
    {
        (WorldTestHost host, Bot bot) = await StartAsync();
        try
        {
            uint picked = await OnWorld(host, () =>
            {
                var crafting = new PlayerbotCrafting(bot.Session, bot.Options); // no probe: the real IsUnreachableOnFoot, which has no graph here
                Assert.False(PlayerbotNavigation.IsUnreachableOnFoot(bot.Player, new Vector3(2000f, 0f, 0f), out _, out _));
                return crafting.FirstUsableDropper(bot.Player, [Far, Near], Content(), 1_000);
            });

            Assert.Equal(Far, picked);
        }
        finally
        {
            await StopAsync(host, bot);
        }
    }
}
