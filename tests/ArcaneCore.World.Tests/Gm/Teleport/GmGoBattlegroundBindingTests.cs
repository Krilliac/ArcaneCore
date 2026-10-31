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
/// Goname and namego into battlegrounds and the GM's battleground binding. Split from the former <c>GmGoBattlegroundAndInstanceTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="GmGoBattlegroundAndInstanceTestBase"/>.
/// </summary>
public sealed class GmGoBattlegroundBindingTests : GmGoBattlegroundAndInstanceTestBase
{
    [Fact]
    public async Task Goname_ToAPlayerInARunningWsg_LandsInThatMatch_AndSendsNoInvalidTargetReply()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-appear-wsg", async context =>
        {
            (_, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scngoally", "Scngohorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOWSG", "Gmgowsg", AccountSecurity.Administrator);
            var slotsBefore = await context.ReadAsync(() => Slots(match));
            int countBefore = await context.ReadAsync(() => match.PlayerCount);

            await AppearAtAsync(context, gm, horde.Name, match);

            // Landed on map 489 in the match's own instance and bound to that match, but not one of its players (vmangos adds only an invited player).
            (uint mapId, uint instanceId, uint boundInstance, bool inMatch) = await context.ReadAsync(() =>
            {
                var p = context.World.FindOnlinePlayer(gm.Name)!;
                Battleground? bound = Battlegrounds(context).BattlegroundOf(p.Guid);
                return (p.Map!.MapId, p.Map.InstanceId, bound?.InstanceId ?? 0u, bound?.PlayerTeam(p.Guid) is not null);
            });
            ScenarioContext.ExpectEqual(Wsg, mapId, "GM map");
            ScenarioContext.ExpectEqual(match.InstanceId, instanceId, "GM map instance");
            ScenarioContext.ExpectEqual(match.InstanceId, boundInstance, "GM battleground binding");
            ScenarioContext.Expect(!inMatch, "the GM became a player of the match");
            ScenarioContext.ExpectEqual(countBefore, await context.ReadAsync(() => match.PlayerCount), "players in the match with the GM");
            ScenarioContext.ExpectEqual(slotsBefore, await context.ReadAsync(() => Slots(match)), "invitations and free slots with the GM on the map");
        });
    }

    [Fact]
    public async Task Goname_FromAnotherBattleground_IsRefused_AndChangesNothing()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-appear-bg-from-bg", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scngrally", "Scngrhorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOREF", "Gmgoref", AccountSecurity.Administrator);

            // The GM is a real participant of a second, running match (it stays alive while it holds a player): the vmangos refusal is
            // "GetBattleGroundId() != the target's".
            uint other = await context.ReadAsync(() =>
            {
                Battleground second = CreateSecondMatch(context, match);
                var team = ally.RequirePlayer().Team;
                ObjectGuid gmGuid = GuidOf(context, gm.Name);
                Battlegrounds(context).Manager.BindPlayer(gmGuid, second, team);
                ScenarioContext.Expect(second.AddPlayer(gmGuid, team), "the GM could not be added to the second match");
                return second.InstanceId;
            });
            ScenarioContext.Expect(other != match.InstanceId, "the second match reuses the first instance id");

            long mark = gm.Mark();
            await gm.SayAsync(".goname " + horde.Name);
            await context.WaitUntilAsync("the GM is refused", () => gm.ChatSince(mark).Length > 0);
            string[] chat = gm.ChatSince(mark);
            Assert.Contains("You cannot teleport to a battleground from another battleground. Please leave the current battleground first.", chat);
            ScenarioContext.Expect(!chat.Any(t => t.StartsWith("Appearing at ", StringComparison.Ordinal)), "the GM was told it is appearing");

            // Nothing moved and the binding is the one the GM had.
            await context.IdleAsync(TimeSpan.FromSeconds(2));
            (uint mapId, uint bound) = await context.ReadAsync(() =>
            {
                var p = context.World.FindOnlinePlayer(gm.Name)!;
                return (p.MapId, Battlegrounds(context).BattlegroundOf(p.Guid)?.InstanceId ?? 0u);
            });
            ScenarioContext.Expect(mapId != Wsg, "the GM was teleported into the battleground");
            ScenarioContext.ExpectEqual(other, bound, "GM battleground binding");
        });
    }

    [Fact]
    public async Task GmBinding_IsClearedWhenTheGmLeavesTheBattlegroundMap()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-appear-bg-leave", async context =>
        {
            (_, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scnglally", "Scnglhorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOLEAVE", "Gmgoleave", AccountSecurity.Administrator);
            int before = await context.ReadAsync(() => match.PlayerCount);
            var slotsBefore = await context.ReadAsync(() => Slots(match));
            await AppearAtAsync(context, gm, horde.Name, match);
            ScenarioContext.ExpectEqual(before, await context.ReadAsync(() => match.PlayerCount), "players in the match with the GM");
            ScenarioContext.ExpectEqual(slotsBefore, await context.ReadAsync(() => Slots(match)), "invitations and free slots with the GM on the map");

            // .recall is a far teleport off the battleground map: the ordinary leave path unbinds the GM.
            await gm.SayAsync(".recall");
            await context.WaitUntilAsync("the GM leaves the battleground map and its binding", () =>
                context.World.FindOnlinePlayer(gm.Name) is { IsInWorld: true, MapId: not Wsg } p
                && Battlegrounds(context).BattlegroundOf(p.Guid) is null
                && !Battlegrounds(context).Manager.StateOf(p.Guid).InBattleground);
            ScenarioContext.ExpectEqual(before, await context.ReadAsync(() => match.PlayerCount), "players in the match after the GM left");
            ScenarioContext.ExpectEqual(slotsBefore, await context.ReadAsync(() => Slots(match)), "invitations and free slots after the GM left");
            ScenarioContext.Expect(!await context.ReadAsync(() => Battlegrounds(context).Manager.StateOf(GuidOf(context, gm.Name)).InAnyQueue),
                "the GM kept a queue slot");
        });
    }

    [Fact]
    public async Task GmBinding_IsClearedAndTheGmReturned_WhenTheMatchRemovesIt()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-appear-bg-end", async context =>
        {
            (_, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scngeally", "Scngehorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOEND", "Gmgoend", AccountSecurity.Administrator);
            (float startX, float startY) = await context.ReadAsync(() =>
            {
                var p = context.World.FindOnlinePlayer(gm.Name)!;
                return (p.X, p.Y);
            });
            await AppearAtAsync(context, gm, horde.Name, match);

            // What the end of a match does to each player (Battleground.cs: RemovePlayerAtLeave with the entry-point teleport).
            await context.ReadAsync(() =>
            {
                match.RemovePlayerAtLeave(GuidOf(context, gm.Name), teleportToEntryPoint: true, sendStatus: true);
                return true;
            });
            await context.WaitUntilAsync("the GM is back where it appeared from, unbound", () =>
                context.World.FindOnlinePlayer(gm.Name) is { IsInWorld: true, MapId: not Wsg } p
                && Math.Abs(p.X - startX) < 1f && Math.Abs(p.Y - startY) < 1f
                && Battlegrounds(context).BattlegroundOf(p.Guid) is null);
        });
    }

    [Fact]
    public async Task Namego_FromInsideAWsgMatch_BringsThePlayerIntoThatMatch()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-summon-wsg", async context =>
        {
            (_, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scngsally", "Scngshorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOSUM", "Gmgosum", AccountSecurity.Administrator);
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMGOVIC", "Gmgovic", AccountSecurity.Player);
            await AppearAtAsync(context, gm, horde.Name, match);

            long mark = gm.Mark();
            await gm.SayAsync(".namego " + victim.Name);
            await context.WaitUntilAsync("the summoned player arrives in the match", () =>
                context.World.FindOnlinePlayer(victim.Name) is { IsInWorld: true, Map: { } map } && map.MapId == Wsg && map.InstanceId == match.InstanceId
                || gm.ChatSince(mark).Any(t => t.Contains("Target map or coordinates is invalid", StringComparison.Ordinal)));
            ScenarioContext.Expect(!gm.ChatSince(mark).Any(t => t.Contains("Target map or coordinates is invalid", StringComparison.Ordinal)),
                "the summon was refused as invalid: " + string.Join(" | ", gm.ChatSince(mark)));
            ScenarioContext.ExpectEqual(match.InstanceId,
                await context.ReadAsync(() => Battlegrounds(context).BattlegroundOf(GuidOf(context, victim.Name))?.InstanceId ?? 0u), "summoned player binding");
        });
    }

    [Fact]
    public async Task GmBinding_OfAMatchDeletedBeforeTheGmArrives_IsNoBinding_ForTheQueueAndForGoname()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-appear-bg-stale", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scngtally", "Scngthorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOSTALE", "Gmgostale", AccountSecurity.Administrator);

            // Bound to a match nobody is in: its first update tick deletes it, leaving the GM bound to a match that is gone.
            async Task BindToAMatchThatIsDeletedAsync()
            {
                uint id = await context.ReadAsync(() =>
                {
                    Battleground ghost = CreateSecondMatch(context, match);
                    Battlegrounds(context).Manager.BindPlayer(GuidOf(context, gm.Name), ghost, ally.RequirePlayer().Team);
                    return ghost.InstanceId;
                });
                await context.WaitUntilAsync("the unused match is deleted", () => Battlegrounds(context).Manager.GetBattleground(id, BattlegroundType.WarsongGulch) is null);
                ScenarioContext.Expect(await context.ReadAsync(() => Battlegrounds(context).Manager.StateOf(GuidOf(context, gm.Name)).InBattleground),
                    "the stale binding is not there; the scenario needs it");
            }

            // 1. The queue: a stale binding must not turn the join into a no-op.
            await BindToAMatchThatIsDeletedAsync();
            BattlegroundJoinOutcome joined = await context.ReadAsync(() =>
            {
                var member = new BattlegroundMember(GuidOf(context, gm.Name), ally.RequirePlayer().Team, 60, true, false);
                return Battlegrounds(context).Manager.JoinQueue(new BattlegroundJoinRequest(member, BattlegroundType.WarsongGulch, 0, AsGroup: false, QueuedAtPortal: false, [])).Outcome;
            });
            ScenarioContext.ExpectEqual(BattlegroundJoinOutcome.Queued, joined, "queue join with a stale binding");
            ScenarioContext.Expect(!await context.ReadAsync(() => Battlegrounds(context).Manager.StateOf(GuidOf(context, gm.Name)).InBattleground),
                "the stale binding survived the queue join");

            // 2. .goname: a stale binding is not "another battleground".
            await BindToAMatchThatIsDeletedAsync();
            await AppearAtAsync(context, gm, horde.Name, match);
            ScenarioContext.ExpectEqual(match.InstanceId,
                await context.ReadAsync(() => Battlegrounds(context).BattlegroundOf(GuidOf(context, gm.Name))?.InstanceId ?? 0u), "GM battleground binding");
        });
    }

    [Fact]
    public async Task Namego_RefusedTeleport_ChangesNothing_AndAnAlreadyBoundPlayerIsLeftAlone()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-summon-bg-revert", async context =>
        {
            (_, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scngvally", "Scngvhorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOREV", "Gmgorev", AccountSecurity.Administrator);
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMGOREVV", "Gmgorevv", AccountSecurity.Player);
            await AppearAtAsync(context, gm, horde.Name, match);

            await context.ReadAsync(() =>
            {
                BattlegroundFeature feature = Battlegrounds(context);
                Player gmPlayer = context.World.FindOnlinePlayer(gm.Name)!;
                Player vic = context.World.FindOnlinePlayer(victim.Name)!;
                ScenarioContext.Expect(feature.EntryPointOf(vic.Guid) is null, "the victim already has an entry point");

                // The move is recorded and nothing else changes: no binding, no entry point, no participation.
                ScenarioContext.ExpectEqual(GmBattlegroundGo.Bound, feature.BeginGmMove(vic, gmPlayer, leaveOtherMatch: true), "begin outcome");
                ScenarioContext.Expect(feature.HasGmMove(vic.Guid), "no move record");
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) is null, "the move bound the victim before its arrival");
                ScenarioContext.Expect(feature.EntryPointOf(vic.Guid) is null, "the move stored an entry point before the arrival");
                ScenarioContext.Expect(match.PlayerTeam(vic.Guid) is null, "the victim joined the match");
                ScenarioContext.Expect(feature.CanEnterMap(vic, Wsg), "the record does not admit the teleport");
                ScenarioContext.ExpectEqual(GmBattlegroundGo.MoveInProgress, feature.BeginGmMove(vic, gmPlayer, leaveOtherMatch: true), "second begin outcome");

                // The teleport is refused: the record goes and nothing else changes.
                feature.CancelGmMove(vic);
                ScenarioContext.Expect(!feature.HasGmMove(vic.Guid), "the record survived the refused teleport");
                ScenarioContext.Expect(!feature.CanEnterMap(vic, Wsg), "the cancelled record still admits the teleport");
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) is null, "the cancel bound the victim");
                ScenarioContext.Expect(feature.EntryPointOf(vic.Guid) is null, "the cancel stored an entry point");

                // A player already bound to the match (loading in) is AlreadyBound and no record is made.
                feature.Manager.BindPlayer(vic.Guid, match, vic.Team);
                ScenarioContext.ExpectEqual(GmBattlegroundGo.AlreadyBound, feature.BeginGmMove(vic, gmPlayer, leaveOtherMatch: true), "begin outcome of a bound player");
                ScenarioContext.Expect(!feature.HasGmMove(vic.Guid), "a record was made for an already bound player");
                feature.CancelGmMove(vic);
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == match, "a cancel unbound a player that was bound before the call");
                feature.Manager.UnbindPlayer(vic.Guid);
                return true;
            });
        });
    }

    [Fact]
    public async Task Namego_OfAPlayerInAnotherMatch_LeavesThatMatchOnlyAtTheCommit_WithoutDeserter()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-summon-bg-other", async context =>
        {
            (_, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(
                BattlegroundType.WarsongGulch, "Scngpally", "Scngphorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMGOOTH", "Gmgooth", AccountSecurity.Administrator);
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMGOOTHV", "Gmgoothv", AccountSecurity.Player);
            await AppearAtAsync(context, gm, horde.Name, match);

            await context.ReadAsync(() =>
            {
                BattlegroundFeature feature = Battlegrounds(context);
                SpellFeature spells = context.Services.GetRequiredService<SpellFeature>();
                Player gmPlayer = context.World.FindOnlinePlayer(gm.Name)!;
                Player vic = context.World.FindOnlinePlayer(victim.Name)!;
                Battleground old = CreateSecondMatch(context, match);
                feature.Manager.BindPlayer(vic.Guid, old, vic.Team);
                ScenarioContext.Expect(old.AddPlayer(vic.Guid, vic.Team), "the victim could not be added to its own match");
                // What JoinQueue -> invite -> HandlePort leaves behind: the queue entry is gone, the slot holds the invitation to the old match.
                BattlegroundPlayerState queueState = feature.Manager.StateOf(vic.Guid);
                queueState.AddSlot(BattlegroundQueueType.WarsongGulch);
                queueState.SetInvite(BattlegroundQueueType.WarsongGulch, old.InstanceId);

                // The move is recorded, then the teleport is refused: the victim is still bound to, and a player of, its own match; no Deserter.
                ScenarioContext.ExpectEqual(GmBattlegroundGo.Bound, feature.BeginGmMove(vic, gmPlayer, leaveOtherMatch: true), "begin outcome");
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == old, "the move changed the victim's binding");
                ScenarioContext.ExpectEqual(1, old.PlayerCount, "players of the old match while the move is pending");
                feature.CancelGmMove(vic);
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == old, "the victim lost its own match when the teleport was refused");
                ScenarioContext.ExpectEqual(1, old.PlayerCount, "players of the old match after the refused move");
                ScenarioContext.Expect(!spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "the victim got Deserter from a refused GM move");
                ScenarioContext.ExpectEqual(old.InstanceId, queueState.InvitedInstance(BattlegroundQueueType.WarsongGulch), "the invitation after the refused move");

                // The move is committed: it left the old match, no Deserter, and is bound (not a player) of the new one.
                ScenarioContext.ExpectEqual(GmBattlegroundGo.Bound, feature.BeginGmMove(vic, gmPlayer, leaveOtherMatch: true), "second begin outcome");
                feature.CommitGmMove(vic);
                ScenarioContext.Expect(!feature.HasGmMove(vic.Guid), "the record survived the commit");
                ScenarioContext.ExpectEqual(0, old.PlayerCount, "players of the old match after the commit");
                ScenarioContext.ExpectEqual(-1, queueState.SlotOf(BattlegroundQueueType.WarsongGulch), "the queue slot of the match the victim was taken out of");
                ScenarioContext.ExpectEqual(0u, queueState.InvitedInstance(BattlegroundQueueType.WarsongGulch), "the invitation to the match the victim was taken out of");
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == match, "the victim is not bound to the new match");
                ScenarioContext.Expect(match.PlayerTeam(vic.Guid) is null, "the victim became a player of the new match");
                ScenarioContext.Expect(!spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "the victim got Deserter from a GM move");

                // Deserter on a leave is for an invitee only: a player a command bound to the running match was never invited.
                feature.Manager.BindPlayer(vic.Guid, match, vic.Team);
                feature.LeaveBattleground(vic, teleportToEntryPoint: false);
                ScenarioContext.Expect(!spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "a non-invitee got Deserter");

                // An invitee whose leave is GM-induced gets none either; one who leaves on its own does.
                feature.Manager.BindPlayer(vic.Guid, match, vic.Team);
                feature.Manager.StateOf(vic.Guid).AddSlot(BattlegroundQueueType.WarsongGulch);
                feature.Manager.StateOf(vic.Guid).SetInvite(BattlegroundQueueType.WarsongGulch, match.InstanceId);
                feature.LeaveBattleground(vic, teleportToEntryPoint: false, castDeserter: false);
                ScenarioContext.Expect(!spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "an invitee got Deserter from a GM-induced leave");
                feature.Manager.BindPlayer(vic.Guid, match, vic.Team);
                feature.Manager.StateOf(vic.Guid).AddSlot(BattlegroundQueueType.WarsongGulch);
                feature.Manager.StateOf(vic.Guid).SetInvite(BattlegroundQueueType.WarsongGulch, match.InstanceId);
                feature.LeaveBattleground(vic, teleportToEntryPoint: false);
                ScenarioContext.Expect(spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "an invitee who left a running match got no Deserter");
                return true;
            });
        });
    }
}
