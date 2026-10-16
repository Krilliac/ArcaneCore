using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Game.Teleport;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The brain's own way to its bind point (<c>PlayerbotBrain.TryTeleportToBindPoint</c>): an urgent escape teleports directly (a ten second
/// cast is no escape), with or without a ready stone; any other reason uses the bot's hearthstone when it is ready, through the real item
/// use and cast, and nothing when it is not.
/// </summary>
public sealed class TravelBindHearthTests
{
    private static async Task<(MageTravelRig Rig, WorldSession Session, Player Player, PlayerbotBrain Brain)> StartAsync(string name, bool stone)
    {
        MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync(name);
        Player player = session.Player!;
        await rig.OnWorld(() =>
        {
            WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
            if (stone) MageTravelRig.Give(player, MageTravelFixture.Hearthstone);
            player.Home = new HomeBind(0, 12, MageTravelFixture.Bind.X, MageTravelFixture.Bind.Y, MageTravelFixture.Bind.Z);
            return 0;
        });
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };
        return (rig, session, player, new PlayerbotBrain(session, options));
    }

    private static TeleportService Teleports(MageTravelRig rig) => rig.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

    /// <summary>Thinks of the brain (a fresh budget each) until the bot stands at its bind point (and <paramref name="also"/> holds) or the time is up.</summary>
    private static async Task<bool> ThinkToBindAsync(MageTravelRig rig, WorldSession session, Player player, PlayerbotBrain brain, Func<bool>? also = null,
        int maxMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (DateTime.UtcNow < deadline)
        {
            bool there = await rig.OnWorld(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(100);
                return player.IsInWorld && MageTravelRig.DistanceTo(player, MageTravelFixture.Bind) < 1f && (also is null || also());
            });
            if (there) return true;
            await Task.Delay(50);
        }

        return false;
    }

    [Fact]
    public async Task AReadyHearthstone_ReplacesTheBindPointTeleport_AndTheBrainDrivesItToTheBindPoint()
    {
        (MageTravelRig rig, WorldSession session, Player player, PlayerbotBrain brain) = await StartAsync("Bindhearth", stone: true);
        await using (rig)
        {
            try
            {
                await rig.OnWorld(() =>
                {
                    Assert.True(brain.TeleportToBindPointForQuest(player, "test: the ender is out of reach"));
                    Assert.True(PlayerbotHearthstone.IsActive(player), "no hearthstone leg was begun");
                    Assert.False(Teleports(rig).IsBeingTeleported(player), "the bot was teleported directly");
                    Assert.True(brain.BindTeleported);
                    return 0;
                });

                Assert.True(await ThinkToBindAsync(rig, session, player, brain, () => PlayerbotHearthstone.LegOf(player)?.Stage == TravelCastStage.Done),
                    "the hearthstone leg did not bring the bot to its bind point and end there");
                await rig.OnWorld(() =>
                {
                    Assert.Equal(TravelCastStage.Done, PlayerbotHearthstone.LegOf(player)!.Stage);
                    Assert.True(rig.Spells.System.GetActiveCooldowns(player).Any(c => c.SpellId == MageTravelFixture.HearthSpell),
                        "the hearthstone's own cast did not run (no cooldown)");
                    return 0;
                });
            }
            finally
            {
                brain.Stop();
            }
        }
    }

    [Fact]
    public async Task WithoutAReadyHearthstone_ANonSafetyReason_SendsTheBotNowhere()
    {
        (MageTravelRig rig, _, Player player, PlayerbotBrain brain) = await StartAsync("Bindnostone", stone: false);
        await using (rig)
        {
            try
            {
                await rig.OnWorld(() =>
                {
                    float before = MageTravelRig.DistanceTo(player, MageTravelFixture.Bind);
                    Assert.False(brain.TeleportToBindPointForQuest(player, "test: the ender is out of reach"));
                    Assert.False(PlayerbotHearthstone.IsActive(player));
                    Assert.False(Teleports(rig).IsBeingTeleported(player));
                    Assert.False(brain.BindTeleported);
                    Assert.Equal(before, MageTravelRig.DistanceTo(player, MageTravelFixture.Bind));
                    return 0;
                });
            }
            finally
            {
                brain.Stop();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUrgentEscape_FromNullIsland_TeleportsDirectly_EvenWithAReadyHearthstone(bool stone)
    {
        (MageTravelRig rig, WorldSession session, Player player, PlayerbotBrain brain) = await StartAsync(stone ? "Islandhearth" : "Islandtele", stone);
        await using (rig)
        {
            try
            {
                await rig.OnWorld(() =>
                {
                    player.Relocate(0.5f, 0.5f, MageTravelFixture.Z, 0f, rig.Host.World.NowMs); // null island: within a yard of (0, 0) on map 0
                    return 0;
                });
                (bool Hearth, bool Teleport) seen = default;
                for (int i = 0; i < 40 && seen == default; i++)
                {
                    seen = await rig.OnWorld(() =>
                    {
                        session.ManagedBudget = new ManagedActionBudget(4);
                        brain.Update(100);
                        return (PlayerbotHearthstone.IsActive(player), Teleports(rig).IsBeingTeleported(player));
                    });
                    await Task.Delay(50);
                }

                Assert.Equal((false, true), seen); // the direct teleport, never the stone's cast, ready or not
                Assert.True(await ThinkToBindAsync(rig, session, player, brain), "the bot did not reach its bind point");
            }
            finally
            {
                brain.Stop();
            }
        }
    }
}
