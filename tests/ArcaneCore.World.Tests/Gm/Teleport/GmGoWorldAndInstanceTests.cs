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
/// Goname from the world, a victim lost while loading, GM moves, and goname into dungeons. Split from the former <c>GmGoBattlegroundAndInstanceTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="GmGoBattlegroundAndInstanceTestBase"/>.
/// </summary>
public sealed class GmGoWorldAndInstanceTests : GmGoBattlegroundAndInstanceTestBase
{
    [Fact]
    public async Task Namego_AtoB_BGoneWhileLoading()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-namego-atob-bgone", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnbgally", "Scnbghorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMBGGM", "Gmbggm", AccountSecurity.Administrator);
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMBGVIC", "Gmbgvic", AccountSecurity.Player);
            ClientTap anchor = await ClientTap.EnterAsync(world, "GMBGANC", "Gmbganc", AccountSecurity.Player);
            try
            {
                Battleground b = await PrepareMoveAsync(context, gm, victim, anchor, a);

                // Stand the victim away from the team start (where the GM, standing next to the anchor, will be) so that the origin can
                // be told from B's coordinates.
                (float originX, float originY) = await context.ReadAsync(() =>
                {
                    Player p = context.World.FindOnlinePlayer(victim.Name)!;
                    ScenarioContext.Expect(context.Services.GetRequiredService<TeleportFeature>().Teleports.TeleportTo(p, Wsg, p.X + 60f, p.Y, p.Z, p.Orientation), "the victim could not be moved inside A");
                    return (p.X + 60f, p.Y);
                });
                await context.WaitUntilAsync("the victim stands at its origin in A", () => context.World.FindOnlinePlayer(victim.Name) is { IsInWorld: true } p
                    && !context.Services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(p) && Math.Abs(p.X - originX) < 1f);
                MoveView before = await ViewAsync(context, victim.Name, a, b);
                using AnomalyCapture anomalies = AnomalyLedger.Shared.Capture(n => n.Subject.Contains(victim.Name, StringComparison.Ordinal));

                await NamegoHeldAsync(context, gm, victim);

                // B dies: its last participant goes, the GM leaves its map, the empty match is deleted.
                await anchor.DisposeAsync();
                await gm.SayAsync(".recall");
                await context.WaitUntilAsync("B is deleted", () => Battlegrounds(context).Manager.GetBattleground(b.InstanceId, BattlegroundType.WarsongGulch) is null, TimeSpan.FromMinutes(2));

                await victim.ReleaseWorldportAckAsync();
                await context.WaitUntilAsync("the victim is back in A's map instance", () => context.World.FindOnlinePlayer(victim.Name) is { IsInWorld: true, Map: { } map } p
                    && map.MapId == Wsg && map.InstanceId == a.InstanceId);
                MoveView v = await ViewAsync(context, victim.Name, a, b);
                ScenarioContext.Expect(v.Online, "the victim was kicked");
                ScenarioContext.Expect(v.BoundToA && v.ParticipantOfA, "the victim is not bound to A as a participant");
                ScenarioContext.ExpectEqual(before.PlayersOfA, v.PlayersOfA, "players of A");
                ScenarioContext.ExpectEqual(before.SlotOfA, v.SlotOfA, "A's queue slot");
                ScenarioContext.ExpectEqual(a.InstanceId, v.InviteOfA, "A's invitation");
                ScenarioContext.Expect(!v.Deserter, "the victim got Deserter");
                ScenarioContext.Expect(!v.HasRecord, "the GM move record is still there");
                ScenarioContext.Expect(!anomalies.Any("teleport/return-failed"), "the victim could not return: " + anomalies.Describe());
                (float x, float y) = await context.ReadAsync(() => (context.World.FindOnlinePlayer(victim.Name)!.X, context.World.FindOnlinePlayer(victim.Name)!.Y));
                ScenarioContext.Expect(Math.Abs(x - originX) < 1f && Math.Abs(y - originY) < 1f,
                    $"the victim is at ({x}, {y}), not back at its origin ({originX}, {originY}): it landed at the coordinates of the match that is gone");
            }
            finally
            {
                await anchor.DisposeAsync();
            }
        });
    }

    [Fact]
    public async Task Goname_FromTheWorld_BindsAndStoresTheEntryPointOnlyAtArrival()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-goname-arrival", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnarally", "Scnarhorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMARGM", "Gmargm", AccountSecurity.Administrator);
            await using ClientTap anchor = await ClientTap.EnterAsync(world, "GMARANC", "Gmaranc", AccountSecurity.Player);
            Battleground b = await CreateMatchWithAnchorAsync(context, anchor, a);
            var slotsOfB = await context.ReadAsync(() => Slots(b));
            (uint mapId, float x, float y) = await context.ReadAsync(() =>
            {
                Player p = context.World.FindOnlinePlayer(gm.Name)!;
                return (p.MapId, p.X, p.Y);
            });

            gm.HoldWorldportAcks = true;
            await gm.SayAsync(".goname " + anchor.Name);
            await context.WaitUntilAsync("the GM's client holds the loading screen", () => gm.HoldingNewWorld);
            MoveView held = await ViewAsync(context, gm.Name, a, b);
            ScenarioContext.Expect(!held.BoundToB && !held.BoundToA, "the GM is bound before it arrived");
            ScenarioContext.Expect(held.EntryPoint is null, "the entry point was stored before the arrival");
            ScenarioContext.Expect(held.HasRecord, "no move record while the teleport is in flight");

            await gm.ReleaseWorldportAckAsync();
            await context.WaitUntilAsync("the GM arrives in B and is bound to it", () => context.World.FindOnlinePlayer(gm.Name) is { IsInWorld: true, Map: { } map } p
                && map.MapId == Wsg && map.InstanceId == b.InstanceId && Battlegrounds(context).BattlegroundOf(p.Guid) == b);
            MoveView after = await ViewAsync(context, gm.Name, a, b);
            ScenarioContext.Expect(!after.ParticipantOfB, "the GM became a participant of B");
            ScenarioContext.Expect(!after.HasRecord, "the record survived the arrival");
            BattlegroundEntryPoint entry = after.EntryPoint ?? throw new ScenarioAssertionException("no entry point after the arrival");
            ScenarioContext.ExpectEqual(mapId, entry.MapId, "entry point map");
            ScenarioContext.Expect(Math.Abs(entry.X - x) < 0.01f && Math.Abs(entry.Y - y) < 0.01f, $"entry point ({entry.X}, {entry.Y}) is not the position before the command ({x}, {y})");
            ScenarioContext.ExpectEqual(slotsOfB, await context.ReadAsync(() => Slots(b)), "B's invitations and free slots");
        });
    }

    [Fact]
    public async Task Goname_WhileBeingTeleported_IsRefusedAndMakesNoRecord()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-goname-twice", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnrfally", "Scnrfhorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMRFGM", "Gmrfgm", AccountSecurity.Administrator);
            await using ClientTap anchor = await ClientTap.EnterAsync(world, "GMRFANC", "Gmrfanc", AccountSecurity.Player);
            Battleground b = await CreateMatchWithAnchorAsync(context, anchor, a);

            gm.HoldWorldportAcks = true;
            await gm.SayAsync(".goname " + anchor.Name);
            await context.WaitUntilAsync("the GM's client holds the loading screen", () => gm.HoldingNewWorld);

            long mark = gm.Mark();
            // A client in its loading screen cannot chat, so the second command is run on the world thread as the chat handler would.
            await context.ReadAsync(() =>
            {
                Player gmPlayer = context.World.FindOnlinePlayer(gm.Name)!;
                var session = (WorldSession)gmPlayer.Session;
                CommandTable commands = session.Services.GetRequiredService<CommandTableSource>().Current;
                commands.Execute(new CommandContext(session, gmPlayer, commands), "goname " + anchor.Name);
                return true;
            });
            await context.WaitUntilAsync("the second command is answered", () => gm.ChatSince(mark).Length > 0);
            ScenarioContext.Expect(gm.ChatSince(mark).Any(t => t.EndsWith(" is already being teleported.", StringComparison.Ordinal)), "no 'already being teleported' reply: " + string.Join(" | ", gm.ChatSince(mark)));
            MoveView v = await ViewAsync(context, gm.Name, a, b);
            ScenarioContext.Expect(v.HasRecord, "the first move's record is gone");
            ScenarioContext.Expect(!v.BoundToA && !v.BoundToB, "the refused command changed the binding");

            await gm.ReleaseWorldportAckAsync();
            await context.WaitUntilAsync("the GM arrives in B and is bound to it", () => context.World.FindOnlinePlayer(gm.Name) is { IsInWorld: true, Map: { } map } p
                && map.MapId == Wsg && map.InstanceId == b.InstanceId && Battlegrounds(context).BattlegroundOf(p.Guid) == b);
            ScenarioContext.Expect(!(await ViewAsync(context, gm.Name, a, b)).HasRecord, "the record survived the arrival");
        });
    }

    [Fact]
    public async Task Namego_AtoB_VictimDisconnectsWhileLoading()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-namego-atob-quit", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnqtally", "Scnqthorde", TimeSpan.FromMinutes(3));
            await using ClientTap gm = await ClientTap.EnterAsync(world, "GMQTGM", "Gmqtgm", AccountSecurity.Administrator);
            ClientTap victim = await ClientTap.EnterAsync(world, "GMQTVIC", "Gmqtvic", AccountSecurity.Player);
            await using ClientTap anchor = await ClientTap.EnterAsync(world, "GMQTANC", "Gmqtanc", AccountSecurity.Player);
            try
            {
                Battleground b = await PrepareMoveAsync(context, gm, victim, anchor, a);
                ObjectGuid guid = await context.ReadAsync(() => GuidOf(context, victim.Name));
                int playersBefore = await context.ReadAsync(() => a.PlayerCount);

                await NamegoHeldAsync(context, gm, victim);
                await victim.DisposeAsync();

                await context.WaitUntilAsync("the victim is gone and its move record with it", () => context.World.FindOnlinePlayer(victim.Name) is null
                    && !Battlegrounds(context).HasGmMove(guid));
                ScenarioContext.ExpectEqual(playersBefore - 1, await context.ReadAsync(() => a.PlayerCount), "players of A after the victim quit");
                ScenarioContext.Expect(!await context.ReadAsync(() => Battlegrounds(context).Manager.StateOf(guid).InBattleground), "the victim is still bound after it quit");
            }
            finally
            {
                await victim.DisposeAsync();
            }
        });
    }

    [Fact]
    public async Task GmMove_WithoutATeleport_IsSweptNextTick()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        await RunAsync(world, "gm-move-swept", async context =>
        {
            (_, _, Battleground a) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, "Scnswally", "Scnswhorde", TimeSpan.FromMinutes(3));
            await using ClientTap victim = await ClientTap.EnterAsync(world, "GMSWVIC", "Gmswvic", AccountSecurity.Player);
            ObjectGuid guid = await context.ReadAsync(() =>
            {
                BattlegroundFeature feature = Battlegrounds(context);
                Player vic = context.World.FindOnlinePlayer(victim.Name)!;
                Player anchor = context.World.FindOnlinePlayer("Scnswally")!;
                ScenarioContext.ExpectEqual(GmBattlegroundGo.Bound, feature.BeginGmMove(vic, anchor, leaveOtherMatch: true), "begin outcome");
                ScenarioContext.Expect(feature.CanEnterMap(vic, Wsg), "the record does not admit the teleport");
                return vic.Guid;
            });

            await context.IdleAsync(TimeSpan.FromSeconds(2));

            ScenarioContext.Expect(!await context.ReadAsync(() => Battlegrounds(context).HasGmMove(guid)), "the record outlived its (missing) teleport");
            ScenarioContext.Expect(!await context.ReadAsync(() => Battlegrounds(context).CanEnterMap(context.World.FindOnlinePlayer(victim.Name)!, Wsg)), "the swept record still admits the teleport");
            ScenarioContext.Expect(await context.ReadAsync(() => Battlegrounds(context).BattlegroundOf(guid) is null), "the sweep changed the binding");
        });
    }

    [Fact]
    public async Task Namego_InsideTheTargetsMap_KeepsTheTargetsCombat()
    {
        // vmangos HandleNamegoCommand passes TELE_TO_NOT_LEAVE_COMBAT (TeleportCommands.cpp:1187); a near teleport otherwise calls CombatStop.
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMNCGM", "Gmncgm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("GMNCVIC", "Gmncvic");
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Gmncvic")!;
            p.Map!.Combat.SetInCombatState(p, 60_000);
        });
        Assert.True(await host.PlayerStateAsync("Gmncvic", p => p.Combat.IsInCombat), "the scenario needs the target in combat");

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".namego Gmncvic");
        await victim.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);

        Assert.True(await host.PlayerStateAsync("Gmncvic", p => p.Combat.IsInCombat), ".namego ended the target's combat");
    }

    // ------------------------------------------------------------------ dungeon instances

    [Fact]
    public async Task Goname_ToAPlayerInADungeon_LandsInThatInstance_NotInANewOne()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient inside = await host.EnterWorldAsync("GMINSIDE", "Gminside");
        await using WorldTestClient gm = await host.EnterWorldAsync("GMINSGM", "Gminsgm", AccountSecurity.Administrator);
        await EnterThroughTriggerAsync(host, inside, "Gminside");
        uint instance = await host.PlayerStateAsync("Gminside", p => p.Map!.InstanceId);
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".goname Gminside");

        Assert.StartsWith("Appearing at ", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
        byte[] newWorld = await gm.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        await gm.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await gm.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Gminsgm")?.Map?.MapId == Deadmines, "the GM enters the dungeon");
        Assert.Equal(instance, await host.PlayerStateAsync("Gminsgm", p => p.Map!.InstanceId));
    }

    [Fact]
    public async Task Goname_ToAPlayerInADungeon_FromAnotherGroup_IsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient inside = await host.EnterWorldAsync("GMINSIDEB", "Gminsideb");
        await using WorldTestClient gm = await host.EnterWorldAsync("GMINSGMB", "Gminsgmb", AccountSecurity.Administrator);
        await using WorldTestClient mate = await host.EnterWorldAsync("GMINSMATE", "Gminsmate");
        await EnterThroughTriggerAsync(host, inside, "Gminsideb");

        await gm.SendAsync(WorldOpcode.CmsgGroupInvite, [.. Encoding.UTF8.GetBytes("Gminsmate"), 0]);
        await mate.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await mate.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        SocialFeature social = host.WorldServices.GetRequiredService<SocialFeature>();
        ObjectGuid gmGuid = await host.PlayerStateAsync("Gminsgmb", p => p.Guid);
        await host.WaitForWorldAsync(() => social.Context.Groups.GetGroup(gmGuid)?.MemberCount == 2, "the GM's group forms");
        await gm.CollectAsync(Quiet);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".goname Gminsideb");

        Assert.Equal("You cannot go to the player's instance because you are in a party now.", (await gm.ReadChatAsync()).Text);
        await Task.Delay(Quiet);
        Assert.NotEqual(Deadmines, await host.PlayerStateAsync("Gminsgmb", p => p.MapId));
    }
}
