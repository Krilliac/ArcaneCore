using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Teleport;

/// <summary>
/// <c>.goname</c> and <c>.namego</c> into a battleground or a dungeon instance (vmangos HandleGonameCommand
/// TeleportCommands.cpp:1242-1303, HandleNamegoCommand :1163-1168). The live bug: <c>.appear Rokun</c> with Rokun in Warsong Gulch
/// answered "Target map or coordinates is invalid" because the teleport gate admits a battleground map only to a player bound to
/// its match, and the GM was bound to none. The battleground tests run a real Warsong Gulch match between two scenario bots
/// (<see cref="WarsongGulchTestContent"/>) and drive the GM through a socket client; the dungeon tests use Deadmines.
/// </summary>
public sealed class GmGoBattlegroundAndInstanceTests
{
    private const uint Wsg = 489;
    private const uint Deadmines = 36;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
    {
        ScenarioReport report = await world.RunAsync(new Scenario(name, body), new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMinutes(6),
        });
        Assert.True(report.Passed, report.ToString());
    }

    /// <summary>
    /// A socket client that records every packet and, like a game client, acknowledges the server's teleports (near and far), so the
    /// scenario clock can advance while the client keeps up. Owns the client.
    /// </summary>
    private sealed class ClientTap : IAsyncDisposable
    {
        private readonly WorldTestClient _client;
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly List<(long Sequence, WorldOpcode Opcode, byte[] Payload)> _received = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reader;
        private long _sequence;

        private ClientTap(WorldTestClient client, string name)
        {
            _client = client;
            Name = name;
            _reader = Task.Run(ReadLoopAsync);
        }

        public string Name { get; }

        public static async Task<ClientTap> EnterAsync(ScenarioTestWorld world, string account, string character, AccountSecurity security)
            => new(await world.EnterWorldAsync(account, character, security), character);

        public long Mark() => Interlocked.Read(ref _sequence) + 1;

        public async Task SayAsync(string text)
        {
            await _send.WaitAsync();
            try
            {
                await _client.SendChatAsync(ChatType.Say, Language.Common, text);
            }
            finally
            {
                _send.Release();
            }
        }

        /// <summary>The text of every SMSG_MESSAGECHAT received since <paramref name="since"/>.</summary>
        public string[] ChatSince(long since)
        {
            lock (_received)
            {
                return [.. _received.Where(p => p.Sequence >= since && p.Opcode == WorldOpcode.SmsgMessagechat)
                    .Select(p => ChatMessage.Parse(p.Payload).Text)];
            }
        }

        private async Task ReadLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                (WorldOpcode opcode, byte[] payload) packet;
                try
                {
                    packet = await _client.ReadAsync(TimeSpan.FromMinutes(10));
                }
                catch (Exception error) when (_stop.IsCancellationRequested || error is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                lock (_received)
                {
                    _received.Add((Interlocked.Increment(ref _sequence), packet.opcode, packet.payload));
                }

                if (packet.opcode == WorldOpcode.MsgMoveTeleportAck)
                {
                    var reader = new PacketReader(packet.payload);
                    ulong guid = reader.ReadPackedGuid();
                    uint counter = reader.ReadUInt32();
                    var reply = new PacketWriter(16);
                    reply.WriteUInt64(guid);
                    reply.WriteUInt32(counter);
                    reply.WriteUInt32(0);
                    await SendRawAsync(WorldOpcode.MsgMoveTeleportAck, reply.ToArray());
                }
                else if (packet.opcode == WorldOpcode.SmsgNewWorld)
                {
                    await SendRawAsync(WorldOpcode.MsgMoveWorldportAck, []);
                }
            }
        }

        private async Task SendRawAsync(WorldOpcode opcode, byte[] payload)
        {
            await _send.WaitAsync();
            try
            {
                await _client.SendAsync(opcode, payload);
            }
            finally
            {
                _send.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _client.DisposeAsync();
            try
            {
                await _reader;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // closed under the reader
            }

            _stop.Dispose();
            _send.Dispose();
        }
    }

    private static BattlegroundFeature Battlegrounds(ScenarioContext context) => context.Services.GetRequiredService<BattlegroundFeature>();

    /// <summary>The invitation counts and free slots of both teams: what a queue over-invites from when a visitor skews them.</summary>
    private static (uint AllianceInvited, uint HordeInvited, uint AllianceFree, uint HordeFree) Slots(Battleground match)
        => (match.InvitedCount(Team.Alliance), match.InvitedCount(Team.Horde), match.FreeSlotsForTeam(Team.Alliance), match.FreeSlotsForTeam(Team.Horde));

    /// <summary>A second running Warsong Gulch match (world thread). It lives only while somebody is in it or invited.</summary>
    private static Battleground CreateSecondMatch(ScenarioContext context, Battleground like)
    {
        BattlegroundManager manager = Battlegrounds(context).Manager;
        Battleground second = manager.CreateNewBattleground(BattlegroundType.WarsongGulch, like.Bracket)
            ?? throw new ScenarioAssertionException("could not create a second match");
        manager.StartBattleground(second);
        return second;
    }

    private static ObjectGuid GuidOf(ScenarioContext context, string name)
        => context.World.FindOnlinePlayer(name)?.Guid ?? throw new ScenarioAssertionException($"{name} is not online");

    /// <summary>
    /// The GM types <c>.goname</c> and waits until it stands in the match's map instance, or until the server answered the live bug's
    /// "Target map or coordinates is invalid", which fails the scenario at once with that text.
    /// </summary>
    private static async Task AppearAtAsync(ScenarioContext context, ClientTap gm, string targetName, Battleground match)
    {
        long mark = gm.Mark();
        await gm.SayAsync(".goname " + targetName);
        await context.WaitUntilAsync("the GM arrives in the match or is refused", () =>
            context.World.FindOnlinePlayer(gm.Name) is { IsInWorld: true, Map: { } map } && map.MapId == Wsg && map.InstanceId == match.InstanceId
            || gm.ChatSince(mark).Any(t => t.Contains("Target map or coordinates is invalid", StringComparison.Ordinal)));
        string[] chat = gm.ChatSince(mark);
        ScenarioContext.Expect(!chat.Any(t => t.Contains("Target map or coordinates is invalid", StringComparison.Ordinal)),
            "the GM was told the battleground target is invalid: " + string.Join(" | ", chat));
        ScenarioContext.Expect(chat.Any(t => t.StartsWith("Appearing at ", StringComparison.Ordinal)), "no 'Appearing at' reply: " + string.Join(" | ", chat));
    }

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
    public async Task Namego_RefusedTeleport_RevertsOnlyWhatTheBindChanged_AndAnAlreadyBoundPlayerIsLeftAlone()
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

                // The teleport is refused: the bind and the entry point it took are gone again.
                ScenarioContext.ExpectEqual(GmBattlegroundGo.Bound, feature.BindToMatchOf(vic, gmPlayer, leaveOtherMatch: true), "bind outcome");
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == match, "the victim is not bound");
                ScenarioContext.Expect(feature.EntryPointOf(vic.Guid) is not null, "no entry point was taken");
                feature.RevertGmBinding(vic);
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) is null, "the binding survived the refused teleport");
                ScenarioContext.Expect(feature.EntryPointOf(vic.Guid) is null, "the entry point survived the refused teleport");
                ScenarioContext.Expect(match.PlayerTeam(vic.Guid) is null, "the victim joined the match");

                // A player already bound to the match (loading in) is AlreadyBound; a revert after that changes nothing.
                feature.Manager.BindPlayer(vic.Guid, match, vic.Team);
                ScenarioContext.ExpectEqual(GmBattlegroundGo.AlreadyBound, feature.BindToMatchOf(vic, gmPlayer, leaveOtherMatch: true), "bind outcome of a bound player");
                feature.RevertGmBinding(vic);
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == match, "a revert unbound a player that was bound before the call");
                feature.Manager.UnbindPlayer(vic.Guid);
                return true;
            });
        });
    }

    [Fact]
    public async Task Namego_OfAPlayerInAnotherMatch_LeavesThatMatchOnlyAfterTheTeleport_WithoutDeserter()
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

                // The teleport is refused: the victim is still bound to, and a player of, its own match; no Deserter.
                ScenarioContext.ExpectEqual(GmBattlegroundGo.Bound, feature.BindToMatchOf(vic, gmPlayer, leaveOtherMatch: true), "bind outcome");
                ScenarioContext.ExpectEqual(1, old.PlayerCount, "players of the old match while the move is pending");
                feature.RevertGmBinding(vic);
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == old, "the victim lost its own match when the teleport was refused");
                ScenarioContext.ExpectEqual(1, old.PlayerCount, "players of the old match after the refused move");
                ScenarioContext.Expect(!spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "the victim got Deserter from a refused GM move");
                ScenarioContext.ExpectEqual(old.InstanceId, queueState.InvitedInstance(BattlegroundQueueType.WarsongGulch), "the invitation after the refused move");

                // The teleport is accepted: it left the old match, no Deserter, and is bound (not a player) of the new one.
                ScenarioContext.ExpectEqual(GmBattlegroundGo.Bound, feature.BindToMatchOf(vic, gmPlayer, leaveOtherMatch: true), "second bind outcome");
                feature.CommitGmBinding(vic);
                ScenarioContext.ExpectEqual(0, old.PlayerCount, "players of the old match after the accepted move");
                ScenarioContext.ExpectEqual(-1, queueState.SlotOf(BattlegroundQueueType.WarsongGulch), "the queue slot of the match the victim was taken out of");
                ScenarioContext.ExpectEqual(0u, queueState.InvitedInstance(BattlegroundQueueType.WarsongGulch), "the invitation to the match the victim was taken out of");
                ScenarioContext.Expect(feature.BattlegroundOf(vic.Guid) == match, "the victim is not bound to the new match");
                ScenarioContext.Expect(match.PlayerTeam(vic.Guid) is null, "the victim became a player of the new match");
                ScenarioContext.Expect(!spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "the victim got Deserter from a GM move");

                // Deserter on a leave is for an invitee only: a player a command bound to the running match was never invited.
                feature.Manager.BindPlayer(vic.Guid, match, vic.Team);
                feature.LeaveBattleground(vic, teleportToEntryPoint: false);
                ScenarioContext.Expect(!spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "a non-invitee got Deserter");
                feature.Manager.BindPlayer(vic.Guid, match, vic.Team);
                feature.Manager.StateOf(vic.Guid).AddSlot(BattlegroundQueueType.WarsongGulch);
                feature.Manager.StateOf(vic.Guid).SetInvite(BattlegroundQueueType.WarsongGulch, match.InstanceId);
                feature.LeaveBattleground(vic, teleportToEntryPoint: false);
                ScenarioContext.Expect(spells.System.HasAura(vic, BattlegroundConstants.SpellDeserter), "an invitee who left a running match got no Deserter");
                return true;
            });
        });
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

    // Stand in the trigger box at level 20 and walk through it: the dungeon transfer, then the ack.
    private static async Task EnterThroughTriggerAsync(WorldTestHost host, WorldTestClient client, string name)
    {
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(name)!.Level = 20);
        await host.PlaceAsync(name, -8962f, -130f, 84f);
        await client.CollectAsync(Quiet);
        byte[] trigger = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trigger, InMemoryMapDataStore.DeadminesTrigger);
        await client.SendAsync(WorldOpcode.CmsgAreatrigger, trigger);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgTransferPending)));
        byte[] newWorld = await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)?.Map?.MapId == Deadmines, $"{name} enters the dungeon");
    }
}
