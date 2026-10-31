using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Teleport;

/// <summary>
/// The entry point after a match, and namego from one battleground to another through the loading screen. Split from the former <c>GmGoBattlegroundAndInstanceTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="GmGoBattlegroundAndInstanceTestBase"/>.
/// </summary>
public sealed class GmGoBattlegroundNamegoLoadingTests : GmGoBattlegroundAndInstanceTestBase
{
    [Fact]
    public async Task LeavingAMatch_ForgetsTheEntryPoint_WhenTheBindPointWasUsed_AndKeepsItWhenNothingWasAccepted()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "bg-entry-point-stale", async context =>
        {
            (ScenarioBot ally, _, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scnepally", "Scnephorde", TimeSpan.FromMinutes(3));
            await context.ReadAsync(() =>
            {
                BattlegroundFeature feature = Battlegrounds(context);
                Player p = ally.RequirePlayer();
                MatchRuntime runtime = feature.FindMatch(match.InstanceId)!;

                // An entry point the player may not use (a battleground map it is no longer bound to): the return to it is refused and the
                // bind point is the fallback, so the entry point it could not use must not stay behind.
                feature.StoreEntryPoint(p.Guid, p);
                feature.Manager.UnbindPlayer(p.Guid);
                ScenarioContext.Expect(feature.EntryPointOf(p.Guid) is not null, "the scenario needs an entry point");
                runtime.TeleportToEntryPoint(p.Guid);
                ScenarioContext.Expect(context.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleportedFar(p), "the bind point teleport was not started");
                ScenarioContext.Expect(feature.EntryPointOf(p.Guid) is null, "the entry point survived a leave that went to the bind point");

                // Nothing is accepted (the player is mid-teleport): the entry point is kept for the leave that follows.
                feature.StoreEntryPoint(p.Guid, p);
                runtime.TeleportToEntryPoint(p.Guid);
                ScenarioContext.Expect(feature.EntryPointOf(p.Guid) is not null, "a refused return forgot the entry point");
                return true;
            });
        });
    }

    // ------------------------------------------------------------------ A to B: the pending GM move

    [Fact]
    public async Task Namego_AtoB_EndToEnd()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-namego-atob", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnabally", "Scnabhorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMABGM", "Gmabgm", AccountSecurity.Administrator);
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMABVIC", "Gmabvic", AccountSecurity.Player);
            await using ClientTap anchor = await ClientTap.EnterAsync(world, "GMABANC", "Gmabanc", AccountSecurity.Player);
            Battleground b = await PrepareMoveAsync(context, gm, victim, anchor, a);
            MoveView before = await ViewAsync(context, victim.Name, a, b);
            var slotsOfB = await context.ReadAsync(() => Slots(b));
            ScenarioContext.Expect(before.BoundToA && before.ParticipantOfA && before.InviteOfA == a.InstanceId && before.SlotOfA >= 0, "the scenario needs the victim to be an invitee-participant of A");

            long mark = victim.Mark();
            await gm.SayAsync(".namego " + victim.Name);
            await ExpectMovedToBAsync(context, victim, a, b, before.PlayersOfA, before.EntryPoint, "after the move");
            ScenarioContext.ExpectEqual(slotsOfB, await context.ReadAsync(() => Slots(b)), "B's invitations and free slots");
            ScenarioContext.Expect(victim.Saw(mark, WorldOpcode.SmsgTransferPending) && victim.Saw(mark, WorldOpcode.SmsgNewWorld), "the victim's client was not sent through a loading screen");
        });
    }

    [Fact]
    public async Task Namego_AtoB_LoadingScreenKeepsA()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-namego-atob-loading", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnlaally", "Scnlahorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMLAGM", "Gmlagm", AccountSecurity.Administrator);
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMLAVIC", "Gmlavic", AccountSecurity.Player);
            await using ClientTap anchor = await ClientTap.EnterAsync(world, "GMLAANC", "Gmlaanc", AccountSecurity.Player);
            Battleground b = await PrepareMoveAsync(context, gm, victim, anchor, a);
            MoveView before = await ViewAsync(context, victim.Name, a, b);

            await NamegoHeldAsync(context, gm, victim);

            async Task ExpectStillInAAsync(string when)
            {
                MoveView v = await ViewAsync(context, victim.Name, a, b);
                ScenarioContext.Expect(v.Online, when + ": the victim was kicked");
                ScenarioContext.Expect(v.BoundToA && !v.BoundToB, when + ": the victim is not bound to A only");
                ScenarioContext.Expect(v.ParticipantOfA, when + ": the victim left A during the loading screen");
                ScenarioContext.ExpectEqual(before.PlayersOfA, v.PlayersOfA, when + ": players of A");
                ScenarioContext.ExpectEqual(before.SlotOfA, v.SlotOfA, when + ": A's queue slot");
                ScenarioContext.ExpectEqual(a.InstanceId, v.InviteOfA, when + ": A's invitation");
                ScenarioContext.Expect(v.HasRecord, when + ": the GM move record is gone while the teleport is in flight");
            }

            await ExpectStillInAAsync("loading");
            await context.IdleAsync(TimeSpan.FromSeconds(5));
            await ExpectStillInAAsync("loading, 5 s later");

            await victim.ReleaseWorldportAckAsync();
            await ExpectMovedToBAsync(context, victim, a, b, before.PlayersOfA, before.EntryPoint, "after the loading screen");
        });
    }

    [Fact]
    public async Task Namego_AtoB_ARemovesTheVictimWhileLoading()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-namego-atob-aends", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnaeally", "Scnaehorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMAEGM", "Gmaegm", AccountSecurity.Administrator);
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMAEVIC", "Gmaevic", AccountSecurity.Player);
            await using ClientTap anchor = await ClientTap.EnterAsync(world, "GMAEANC", "Gmaeanc", AccountSecurity.Player);
            Battleground b = await PrepareMoveAsync(context, gm, victim, anchor, a);
            using AnomalyCapture anomalies = AnomalyLedger.Shared.Capture(n => n.Subject.Contains(victim.Name, StringComparison.Ordinal));

            await NamegoHeldAsync(context, gm, victim);

            // A ends while the victim is in its loading screen and removes every player after the auto-remove time: the victim too.
            await context.ReadAsync(() =>
            {
                a.EndBattleground(null);
                return true;
            });
            await context.IdleAsync(TimeSpan.FromMilliseconds(BattlegroundConstants.TimeToAutoRemoveMs + 2000));

            MoveView v = await ViewAsync(context, victim.Name, a, b);
            ScenarioContext.ExpectEqual(0, v.PlayersOfA, "players of A after it removed everybody");
            ScenarioContext.Expect(!v.ParticipantOfA, "the victim is still a participant of A");
            ScenarioContext.ExpectEqual(-1, v.SlotOfA, "A's queue slot of the victim");
            ScenarioContext.Expect(v.Online, "the victim was kicked while loading");
            ScenarioContext.Expect(!await context.ReadAsync(() => Battlegrounds(context).Manager.StateOf(GuidOf(context, victim.Name)).InBattleground), "the victim is still bound after A removed it");
            ScenarioContext.Expect(v.HasRecord, "the GM move record was dropped while the teleport is still in flight");

            await victim.ReleaseWorldportAckAsync();
            await ExpectMovedToBAsync(context, victim, a, b, playersOfABefore: 1 /* the victim, before it left; A is empty after */, v.EntryPoint, "after A removed the victim");
            ScenarioContext.Expect(!anomalies.Any("teleport/map-entry-failed") && !anomalies.Any("teleport/return-failed"), "the arrival failed: " + anomalies.Describe());
        });
    }
}
