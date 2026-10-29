using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The mage fee driver beside the trade slot registry (<see cref="PlayerbotTradeOwners"/>): the registry must be live in the mage rig, otherwise every mage test would run
/// the null-registry path (each driver the sole owner) and never the contention rules.
/// </summary>
public sealed class MageTradeOwnersTests(ITestOutputHelper output)
{
    private const uint Fee = 100;

    /// <summary>The mage rig's managed sessions resolve the playerbot feature's registry. Broken: <c>PlayerbotTradeOwners.Of(session)</c> would be null.</summary>
    [Fact]
    public async Task TheMageRigSession_ResolvesTheFeaturesTradeOwners()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Ownerprobe");
        Assert.NotNull(PlayerbotTradeOwners.Of(session));
    }

    /// <summary>
    /// A whole portal journey in which the trade desk agrees a deal for the requester at the very moment its fee has been paid: the fee trade completed (the requester's money
    /// fell by the fee) and the fee leg has not yet looked at it when the requester agrees a sale with a third bot. The fee leg must still recognise its paid fee (an agreed
    /// desk deal does not own the slot, so it does not hide the payment) and the requester goes through the portal, paying once; the desk's deal is untouched (still Agreed).
    /// Broken: the paid fee is lost behind the deal (the leg waits for the desk and fails after the pay wait, or pays again) and the journey never arrives.
    /// </summary>
    [Fact]
    public async Task APortalTrip_WhoseFeeIsPaidWhileADeskDealGetsAgreed_RecordsTheFee_AndArrives()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync(new MageTravelFixture { Vendor = true });
        WorldSession mageSession = await rig.EnterAsync("Feemage");
        WorldSession requesterSession = await rig.EnterAsync("Feerider");
        WorldSession buyerSession = await rig.EnterAsync("Feebuyer");
        Player mage = mageSession.Player!, requester = requesterSession.Player!, buyer = buyerSession.Player!;
        ManagedPlayerbotFeature feature = requesterSession.Services.GetRequiredService<ManagedPlayerbotFeature>();
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Risk = { Enabled = false } };
        options.Travel.MageServiceFeeCopper = Fee;
        var requesterBrain = new PlayerbotBrain(requesterSession, options);
        var travel = new PlayerbotTravel(options);
        var botId = Guid.NewGuid();
        try
        {
            await rig.OnWorld(() =>
            {
                feature.BotOptions.Enabled = true;
                WorldCollision.Of(rig.Host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
                rig.Teach(mage, MageTravelFixture.PortalSpell);
                MageTravelRig.Give(mage, MageTravelFixture.RuneOfPortals, 2);
                MageTravelRig.Give(requester, MageTravelFixture.DealGoods);
                mage.Money = 1_000;
                requester.Money = 500;
                requester.Relocate(mage.X - 25f, mage.Y, mage.Z, 0f, rig.Host.World.NowMs);
                buyer.Relocate(requester.X, requester.Y + 2f, requester.Z, 0f, rig.Host.World.NowMs);
                return 0;
            });

            for (int i = 0; i < 40 && await rig.OnWorld(() => PlayerbotPortalBoard.Offers(requesterSession, requester, options).Count) == 0; i++)
            {
                await rig.OnWorld(() =>
                {
                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMageService.DriveMage(mageSession, mage, options, 100);
                    return 0;
                });
                await Task.Delay(50);
            }

            await rig.OnWorld(() =>
            {
                Assert.True(travel.TryBegin(requesterSession, requester, 0, MageTravelFixture.Stormwind, out string? why), why);
                return 0;
            });

            // Both brains think. At the first step that finds the fee paid (money down by the fee) while the fee leg is still Paying, the desk agrees a sale for the requester.
            bool agreed = false;
            JourneyState? state = JourneyState.Active;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (state == JourneyState.Active && DateTime.UtcNow < deadline)
            {
                state = await rig.OnWorld(() =>
                {
                    PlayerbotTradeDesk desk = feature.TradeDesk;
                    if (!agreed && requester.Money == 500 - Fee && PlayerbotMageService.LegOf(requester) is { Stage: MageLegStage.Paying })
                    {
                        PlayerbotDealQuote quote = desk.Quote(botId, requester, buyer, PlayerbotDealRole.Seller, MageTravelFixture.DealGoods, 1);
                        Assert.True(quote.Ok, quote.Refusal.ToString());
                        Assert.Equal(PlayerbotDealHeardKind.Agreed, desk.Hear(botId, buyer.Guid, "deal").Kind);
                        agreed = true;
                    }

                    mageSession.ManagedBudget = new ManagedActionBudget(4);
                    PlayerbotMageService.DriveMage(mageSession, mage, options, 100);
                    requesterSession.ManagedBudget = new ManagedActionBudget(4);
                    requesterBrain.Update(100);
                    return PlayerbotTravel.JourneyOf(requester)?.State;
                });
                await Task.Delay(50);
            }

            string trace = await rig.OnWorld(() =>
                $"requester at {requester.X:F1},{requester.Y:F1} money {requester.Money}; journey {PlayerbotTravel.JourneyOf(requester)?.State} "
                + $"{PlayerbotTravel.JourneyOf(requester)?.Failure} spent {PlayerbotTravel.JourneyOf(requester)?.SpentCopper}; "
                + $"mage leg {PlayerbotMageService.LegOf(requester)?.Stage} {PlayerbotMageService.LegOf(requester)?.Failure}; deal agreed {agreed}");
            output.WriteLine(trace);
            Assert.True(agreed, "the fee was never seen paid before the fee leg recorded it: " + trace);
            Assert.Equal(JourneyState.Arrived, state);
            await rig.OnWorld(() =>
            {
                Assert.True(MageTravelRig.DistanceTo(requester, MageTravelFixture.Stormwind) <= PlayerbotTravel.ArriveYards, trace);
                Assert.Equal(500u - Fee, requester.Money); // one fee
                Assert.Equal(Fee, PlayerbotTravel.JourneyOf(requester)!.SpentCopper);
                Assert.Equal(PlayerbotDealState.Agreed, feature.TradeDesk.StateOf(botId, buyer.Guid));
                return 0;
            });
        }
        finally
        {
            requesterBrain.Stop();
        }
    }
}
