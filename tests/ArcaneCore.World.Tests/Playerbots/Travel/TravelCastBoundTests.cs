using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The cast legs (<see cref="PlayerbotHearthstone"/>, a mage's own Teleport in <see cref="PlayerbotMageService"/>) are bounded before the
/// server took the cast (<see cref="PlayerbotTravelCast.StartBoundMs"/> in world time, <see cref="PlayerbotTravelCast.MaxSendAttempts"/> thinks,
/// whatever put the request off: a bot that would not stand, an action budget spent), failing once with an anomaly; and a fight during the
/// cast cancels it and drops the leg (no report) so the brain fights.
/// </summary>
public sealed class TravelCastBoundTests
{
    private static async Task<(MageTravelRig Rig, WorldSession Session, Player Player)> StartAsync(string name)
    {
        MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync(name);
        Player player = session.Player!;
        await rig.OnWorld(() =>
        {
            MageTravelRig.Give(player, MageTravelFixture.Hearthstone);
            MageTravelRig.Give(player, MageTravelFixture.RuneOfTeleportation);
            rig.Teach(player, MageTravelFixture.TeleportSpell);
            player.Home = new HomeBind(0, 12, MageTravelFixture.Bind.X, MageTravelFixture.Bind.Y, MageTravelFixture.Bind.Z);
            return 0;
        });
        return (rig, session, player);
    }

    /// <summary>The next think of the leg under test, with an action budget of <paramref name="budget"/>.</summary>
    private static TravelLegStep Think(WorldSession session, Player player, PlayerbotOptions options, bool hearth, int budget, out string? failure)
    {
        session.ManagedBudget = new ManagedActionBudget(budget);
        return hearth ? PlayerbotHearthstone.Drive(session, player, options, 500, out failure) : PlayerbotMageService.Drive(session, player, options, 500, out failure);
    }

    private static bool Begin(WorldSession session, Player player, PlayerbotOptions options, bool hearth)
        => hearth ? PlayerbotHearthstone.TryBegin(session, player, options, out _)
            : PlayerbotMageService.TryBeginTeleport(session, player, MageTravelFixture.TeleportSpell, options, out _);

    private static TravelCastLeg Leg(Player player, bool hearth) => hearth ? PlayerbotHearthstone.LegOf(player)! : PlayerbotMageService.LegOf(player)!.Cast!;

    private static string Area(bool hearth) => hearth ? PlayerbotHearthstone.Area : PlayerbotMageService.Area;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABotThatCannotBeStoodUp_FailsTheLegAfterTheBoundedThinks_ReportedOnce(bool hearth)
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync(hearth ? "Boundstand" : "Boundtele");
        await using (rig)
        {
            using AnomalyCapture cap = await BotAnomalyCapture.OnWorldThreadAsync(rig.Host, player);
            PlayerbotOptions options = MageTravelRig.Options();
            await rig.OnWorld(() =>
            {
                Assert.True(Begin(session, player, options, hearth));
                // Sitting, with no action left to stand up with: every think puts the cast off.
                player.StandState = StandState.Sit;
                TravelLegStep step = TravelLegStep.Traveling;
                string? failure = null;
                int thinks = 0;
                while (step == TravelLegStep.Traveling && thinks < 100)
                {
                    thinks++;
                    step = Think(session, player, options, hearth, 0, out failure);
                }

                Assert.Equal(TravelLegStep.Failed, step);
                Assert.Equal(PlayerbotTravelCast.MaxSendAttempts + 1, thinks);
                Assert.Contains("could not be stood still", failure);
                Assert.Equal(TravelLegStep.Idle, Think(session, player, options, hearth, 0, out _));
                return 0;
            });
            Assert.Equal([hearth ? "use-refused" : "teleport-cast-refused"], MageTravelRig.Codes(cap, Area(hearth)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALegWhoseCastWasNotTakenWithinTheStartBound_Fails_ReportedOnce(bool hearth)
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync(hearth ? "Boundtime" : "Boundtelet");
        await using (rig)
        {
            using AnomalyCapture cap = await BotAnomalyCapture.OnWorldThreadAsync(rig.Host, player);
            PlayerbotOptions options = MageTravelRig.Options();
            await rig.OnWorld(() =>
            {
                Assert.True(Begin(session, player, options, hearth));
                player.StandState = StandState.Sit;
                Assert.Equal(TravelLegStep.Traveling, Think(session, player, options, hearth, 0, out _));
                // Begun longer ago than the start bound, the cast still not taken: the next think gives the leg up.
                TravelCastLeg leg = Leg(player, hearth);
                leg.BeganMs = unchecked(rig.Host.World.NowMs - PlayerbotTravelCast.StartBoundMs - 1_000);
                Assert.Equal(TravelLegStep.Failed, Think(session, player, options, hearth, 0, out string? failure));
                Assert.Contains($"not taken within {PlayerbotTravelCast.StartBoundMs} ms", failure);
                return 0;
            });
            Assert.Equal([hearth ? "use-refused" : "teleport-cast-refused"], MageTravelRig.Codes(cap, Area(hearth)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttackedDuringTheCast_TheCastIsCancelled_AndTheLegDropped_WithoutAReport(bool hearth)
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync(hearth ? "Hitcast" : "Hittele");
        await using (rig)
        {
            using AnomalyCapture cap = await BotAnomalyCapture.OnWorldThreadAsync(rig.Host, player);
            PlayerbotOptions options = MageTravelRig.Options();
            uint spell = hearth ? MageTravelFixture.HearthSpell : MageTravelFixture.TeleportSpell;
            await rig.OnWorld(() =>
            {
                Assert.True(Begin(session, player, options, hearth));
                Assert.Equal(TravelLegStep.Traveling, Think(session, player, options, hearth, 4, out _));
                Assert.True(PlayerbotTravelCast.IsCasting(rig.Spells.System, player, spell), "the cast did not start");

                player.Map!.Combat.SetInCombatState(player, 60_000);
                Assert.Equal(TravelLegStep.Idle, Think(session, player, options, hearth, 4, out string? failure));
                Assert.Null(failure);
                Assert.False(hearth ? PlayerbotHearthstone.IsActive(player) : PlayerbotMageService.IsActive(player), "the leg outlived the fight's start");
                Assert.False(PlayerbotTravelCast.IsCasting(rig.Spells.System, player, spell), "the cast went on in the fight");
                return 0;
            });

            // Well past the cast time: the bot is still where it was attacked.
            await Task.Delay(MageTravelFixture.CastMs * 4);
            await rig.OnWorld(() =>
            {
                Assert.True(MageTravelRig.DistanceTo(player, hearth ? MageTravelFixture.Bind : MageTravelFixture.Stormwind) > 100f, "the cast teleported the bot anyway");
                return 0;
            });
            Assert.Empty(MageTravelRig.Codes(cap, Area(hearth)));
        }
    }
}
