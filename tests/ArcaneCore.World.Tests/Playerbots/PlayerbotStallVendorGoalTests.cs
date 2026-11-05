using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Travel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA #1487 (stall: goal Vendor target 3098 map 1 near -550,-4250): level 60 bots with no home stood in the Valley of Trials, whose
/// creatures are level 1 to 5, holding a Vendor errand that named no vendor (target 0) and stalling again and again on it. Giving up
/// Vendor/0 set entry 0 aside, which does nothing, left the goal as Vendor, and the stall escape only moved bots that were too low for the
/// area, never ones that were too high. The rig stands the bot on map 0 (the world test host has no map 1); only the shape matters: a high
/// level bot, no home, an atlas that lists nothing near it for its level and no creature stronger than it, and a bind point that has some.
/// </summary>
public sealed class PlayerbotStallVendorGoalTests
{
    private const uint LowMob = 960201, HighMob = 960202;

    private static readonly Vector3 FarBind = new(MageTravelFixture.X + 1500f, MageTravelFixture.Y, MageTravelFixture.Z);

    private static async Task<(MageTravelRig Rig, Player Player, PlayerbotBrain Brain)> StartAsync(string name, bool highLevelCreaturesHere, bool withHome)
    {
        MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync(name);
        Player player = session.Player!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } });
        await rig.OnWorld(() =>
        {
            WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
            MageTravelRig.Give(player, MageTravelFixture.Hearthstone);
            player.Level = 60;
            player.Home = new HomeBind(0, 12, FarBind.X, FarBind.Y, FarBind.Z);
            // Level 3 creatures 300 yards from the bot (out of sight, inside the three rings the stranded checks look at), and a level 60
            // creature at the bind point; with highLevelCreaturesHere, one beside the bot as well (the zone then fits its level).
            var templates = new List<CreatureTemplate>
            {
                new() { Entry = LowMob, Name = "valley mob", MinLevel = 3, MaxLevel = 3, Faction = 14, DisplayIds = [49] },
                new() { Entry = HighMob, Name = "outland mob", MinLevel = 60, MaxLevel = 60, Faction = 14, DisplayIds = [49] },
            };
            var spawns = new List<CreatureSpawn>
            {
                new() { Guid = 960211, Entry = LowMob, MapId = player.MapId, X = player.X + 300f, Y = player.Y, Z = player.Z },
                new() { Guid = 960212, Entry = HighMob, MapId = player.MapId, X = FarBind.X, Y = FarBind.Y, Z = FarBind.Z },
            };
            if (highLevelCreaturesHere)
                spawns.Add(new() { Guid = 960213, Entry = HighMob, MapId = player.MapId, X = player.X - 300f, Y = player.Y, Z = player.Z });
            rig.Host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(templates, spawns, [], [], []));
            if (withHome) brain.Home = new PlayerbotHomeArea(player.MapId, new Vector3(player.X, player.Y, player.Z), 750f, "StallHome");
            return 0;
        });
        return (rig, player, brain);
    }

    /// <summary>
    /// A Vendor errand that names no vendor (target 0) is given up: the goal is no longer Vendor, and the bot, too strong for everything the
    /// area holds and with no home, leaves it by its hearthstone to its bind point (where creatures of its level are).
    /// </summary>
    [Fact]
    public async Task GivenUpVendorErrandOfAnOverLevelledBotWithNoHome_IsNotStillVendor_AndTheBotHearthsOut()
    {
        (MageTravelRig rig, Player player, PlayerbotBrain brain) = await StartAsync("Stallvendor", highLevelCreaturesHere: false, withHome: false);
        await using (rig)
        {
            try
            {
                await rig.OnWorld(() =>
                {
                    brain.HoldGoalForTests(PlayerbotGoalKind.Vendor, 0, 0);
                    brain.GiveUpStalledGoal(player);
                    Assert.NotEqual(PlayerbotGoalKind.Vendor, brain.Goal);
                    Assert.Equal(0u, brain.TargetEntry);
                    Assert.True(PlayerbotHearthstone.IsActive(player), "the over-levelled bot with no home did not begin its hearthstone leg out of the zone");
                    Assert.True(brain.BindTeleported);
                    return 0;
                });
            }
            finally { brain.Stop(); }
        }
    }

    /// <summary>Control: where creatures of the bot's level stand within reach, the zone fits it and the give-up sends it nowhere.</summary>
    [Fact]
    public async Task GivenUpVendorErrand_WhereCreaturesOfTheBotsLevelAreNear_LeavesTheBotInTheZone()
    {
        (MageTravelRig rig, Player player, PlayerbotBrain brain) = await StartAsync("Stallfits", highLevelCreaturesHere: true, withHome: false);
        await using (rig)
        {
            try
            {
                await rig.OnWorld(() =>
                {
                    brain.HoldGoalForTests(PlayerbotGoalKind.Vendor, 0, 0);
                    brain.GiveUpStalledGoal(player);
                    Assert.False(PlayerbotHearthstone.IsActive(player));
                    Assert.False(brain.BindTeleported);
                    return 0;
                });
            }
            finally { brain.Stop(); }
        }
    }

    /// <summary>Control: a bot with a home is the home's to keep (it walks or teleports home by its own rules), not sent to its bind point.</summary>
    [Fact]
    public async Task GivenUpVendorErrand_OfAHomedBot_DoesNotHearthToTheBindPoint()
    {
        (MageTravelRig rig, Player player, PlayerbotBrain brain) = await StartAsync("Stallhomed", highLevelCreaturesHere: false, withHome: true);
        await using (rig)
        {
            try
            {
                await rig.OnWorld(() =>
                {
                    brain.HoldGoalForTests(PlayerbotGoalKind.Vendor, 0, 0);
                    brain.GiveUpStalledGoal(player);
                    Assert.False(PlayerbotHearthstone.IsActive(player));
                    Assert.False(brain.BindTeleported);
                    return 0;
                });
            }
            finally { brain.Stop(); }
        }
    }
}
