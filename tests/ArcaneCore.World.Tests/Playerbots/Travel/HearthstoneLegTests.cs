using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>The hearthstone leg (<see cref="PlayerbotHearthstone"/>) through the real CMSG_USE_ITEM, the item's on-use spell and its teleport.</summary>
public sealed class HearthstoneLegTests
{
    private static readonly string[] HearthCodes = ["no-hearthstone", "on-cooldown", "use-refused", "cast-interrupted", "not-arrived", "services-unavailable"];

    private static async Task<(MageTravelRig Rig, WorldSession Session, Player Player)> StartAsync(string name)
    {
        MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync(name);
        Player player = session.Player!;
        await rig.OnWorld(() =>
        {
            MageTravelRig.Give(player, MageTravelFixture.Hearthstone);
            player.Home = new HomeBind(0, 12, MageTravelFixture.Bind.X, MageTravelFixture.Bind.Y, MageTravelFixture.Bind.Z);
            return 0;
        });
        return (rig, session, player);
    }

    [Fact]
    public async Task TheHearthstoneLeg_CastsTheItemsSpell_AndArrivesAtTheBindPoint_WithoutAnAnomaly()
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync("Hearthone");
        await using (rig)
        {
            using AnomalyCapture cap = BotAnomalyCapture.For(player);
            PlayerbotOptions options = MageTravelRig.Options();
            await rig.OnWorld(() =>
            {
                Assert.True(PlayerbotHearthstone.IsReady(session, player, options));
                Assert.Equal(new TravelPlace(0, MageTravelFixture.Bind), PlayerbotHearthstone.BindPlace(player));
                Assert.True(PlayerbotHearthstone.TryBegin(session, player, options, out string? why), why);
                Assert.True(PlayerbotHearthstone.IsActive(player));

                // The first think uses the stone: the server's own cast of the item's spell, the bot standing where it was.
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.Equal(TravelLegStep.Traveling, PlayerbotHearthstone.Drive(session, player, options, 500, out string? failure));
                Assert.Null(failure);
                Assert.True(PlayerbotTravelCast.IsCasting(rig.Spells.System, player, MageTravelFixture.HearthSpell), "the use did not start the hearthstone cast");
                Assert.True(MageTravelRig.DistanceTo(player, MageTravelFixture.Bind) > 100f);
                return 0;
            });

            TravelLegStep step = await rig.ThinkUntilAsync(session, () => PlayerbotHearthstone.Drive(session, player, options, 500, out _));
            Assert.Equal(TravelLegStep.Arrived, step);
            await rig.OnWorld(() =>
            {
                Assert.True(MageTravelRig.DistanceTo(player, MageTravelFixture.Bind) < 1f, $"the bot stands at {player.X},{player.Y},{player.Z}");
                Assert.False(PlayerbotHearthstone.IsActive(player));
                Assert.Null(PlayerbotHearthstone.LastFailure(player));
                Assert.Equal(TravelLegStep.Idle, PlayerbotHearthstone.Drive(session, player, options, 500, out _)); // answered once
                Assert.Equal(1u, player.Inventory.GetItemCount(MageTravelFixture.Hearthstone)); // the stone is not used up
                return 0;
            });
            Assert.Empty(MageTravelRig.Codes(cap, PlayerbotHearthstone.Area));
        }
    }

    [Fact]
    public async Task AHearthstoneOnCooldown_IsNotReady_AndTheLegIsRefused_WithoutAnAnomaly()
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync("Hearthtwo");
        await using (rig)
        {
            PlayerbotOptions options = MageTravelRig.Options();
            await rig.OnWorld(() => PlayerbotHearthstone.TryBegin(session, player, options, out _));
            Assert.Equal(TravelLegStep.Arrived, await rig.ThinkUntilAsync(session, () => PlayerbotHearthstone.Drive(session, player, options, 500, out _)));

            using AnomalyCapture cap = BotAnomalyCapture.For(player);
            await rig.OnWorld(() =>
            {
                // The hour's cooldown the cast started on the server.
                Assert.Contains(rig.Spells.System.GetActiveCooldowns(player), c => c.SpellId == MageTravelFixture.HearthSpell && c.CooldownMs > 0);
                Assert.False(PlayerbotHearthstone.IsReady(session, player, options));
                Assert.False(PlayerbotHearthstone.TryBegin(session, player, options, out string? why));
                Assert.Contains("cooldown", why);
                Assert.False(PlayerbotHearthstone.IsActive(player));
                return 0;
            });
            Assert.Empty(MageTravelRig.Codes(cap, PlayerbotHearthstone.Area));
        }
    }

    [Fact]
    public async Task WithTheHearthstoneSwitchOff_TheLegIsRefused_AndALegNotYetCastIsDropped()
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync("Hearththree");
        await using (rig)
        {
            using AnomalyCapture cap = BotAnomalyCapture.For(player);
            await rig.OnWorld(() =>
            {
                PlayerbotOptions off = MageTravelRig.Options(t => t.Hearthstone = false);
                Assert.False(PlayerbotHearthstone.IsReady(session, player, off));
                Assert.False(PlayerbotHearthstone.TryBegin(session, player, off, out string? why));
                Assert.Contains("off", why);
                Assert.False(PlayerbotHearthstone.IsActive(player));

                // Begun with the switch on, switched off before the first think: dropped at the safe point, nothing cast, nothing reported.
                PlayerbotOptions options = MageTravelRig.Options();
                Assert.True(PlayerbotHearthstone.TryBegin(session, player, options, out _));
                options.Travel.Hearthstone = false;
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.Equal(TravelLegStep.Idle, PlayerbotHearthstone.Drive(session, player, options, 500, out _));
                Assert.False(PlayerbotHearthstone.IsActive(player));
                Assert.False(PlayerbotTravelCast.IsCasting(rig.Spells.System, player, MageTravelFixture.HearthSpell));
                return 0;
            });
            Assert.Empty(MageTravelRig.Codes(cap, PlayerbotHearthstone.Area));
        }
    }

    [Fact]
    public async Task AStoneLostAfterTheLegBegan_FailsTheLegOnce_WithNoHearthstone()
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync("Hearthfour");
        await using (rig)
        {
            using AnomalyCapture cap = BotAnomalyCapture.For(player);
            await rig.OnWorld(() =>
            {
                PlayerbotOptions options = MageTravelRig.Options();
                Assert.True(PlayerbotHearthstone.TryBegin(session, player, options, out _));
                player.Inventory.DestroyItemCount(MageTravelFixture.Hearthstone, 1);
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.Equal(TravelLegStep.Failed, PlayerbotHearthstone.Drive(session, player, options, 500, out string? failure));
                Assert.Contains("no longer in the bags", failure);
                Assert.Equal(TravelLegStep.Idle, PlayerbotHearthstone.Drive(session, player, options, 500, out _));
                return 0;
            });
            Assert.Equal(["no-hearthstone"], MageTravelRig.Codes(cap, PlayerbotHearthstone.Area));
            foreach (string code in HearthCodes.Where(c => c != "no-hearthstone"))
                Assert.False(cap.Any(PlayerbotAnomalies.Key(PlayerbotHearthstone.Area, code)), cap.Describe());
        }
    }

    [Fact]
    public async Task ADelayedFirstDrivePastStartBound_WithZeroAttempts_AnchorsBeganMsAndStartsTheCast_InsteadOfFailingWithZeroAttempts()
    {
        (MageTravelRig rig, WorldSession session, Player player) = await StartAsync("Hearthdelay");
        await using (rig)
        {
            using AnomalyCapture cap = BotAnomalyCapture.For(player);
            PlayerbotOptions options = MageTravelRig.Options();
            await rig.OnWorld(() =>
            {
                // A competing travel leg is cancelled when TryBegin starts the hearthstone leg.
                rig.Teach(player, MageTravelFixture.TeleportSpell);
                MageTravelRig.Give(player, MageTravelFixture.RuneOfTeleportation);
                Assert.True(PlayerbotMageService.TryBeginTeleport(session, player, MageTravelFixture.TeleportSpell, options, out _));
                Assert.True(PlayerbotMageService.IsActive(player));

                Assert.True(PlayerbotHearthstone.TryBegin(session, player, options, out string? why), why);
                Assert.False(PlayerbotMageService.IsActive(player));
                TravelCastLeg leg = PlayerbotHearthstone.LegOf(player)!;
                Assert.Equal(0, leg.Attempts);

                // 35,000 ms elapse before Drive is first reached (e.g. resting or competing leg before the first cast attempt).
                leg.BeganMs = unchecked(session.World.NowMs - 35_000u);
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.Equal(TravelLegStep.Traveling, PlayerbotHearthstone.Drive(session, player, options, 500, out string? failure));
                Assert.Null(failure);
                Assert.True(PlayerbotTravelCast.IsCasting(rig.Spells.System, player, MageTravelFixture.HearthSpell),
                    "the first Drive after a pre-Drive delay did not start the hearthstone cast");
                return 0;
            });
            Assert.Empty(MageTravelRig.Codes(cap, PlayerbotHearthstone.Area));
        }
    }
}
