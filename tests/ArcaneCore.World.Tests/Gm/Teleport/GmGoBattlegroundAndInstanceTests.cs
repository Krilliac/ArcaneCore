using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
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
        private int _heldNewWorld;
        private int _disposed;

        private ClientTap(WorldTestClient client, string name)
        {
            _client = client;
            Name = name;
            _reader = Task.Run(ReadLoopAsync);
        }

        public string Name { get; }

        /// <summary>
        /// While true the client records SMSG_NEW_WORLD but does not send MSG_MOVE_WORLDPORT_ACK: the far teleport stays in its loading screen
        /// until <see cref="ReleaseWorldportAckAsync"/>.
        /// </summary>
        public volatile bool HoldWorldportAcks;

        /// <summary>Whether an SMSG_NEW_WORLD is waiting for <see cref="ReleaseWorldportAckAsync"/>.</summary>
        public bool HoldingNewWorld => Volatile.Read(ref _heldNewWorld) != 0;

        /// <summary>Stop holding and acknowledge the held SMSG_NEW_WORLD (the loading screen ends).</summary>
        public async Task ReleaseWorldportAckAsync()
        {
            HoldWorldportAcks = false;
            if (Interlocked.Exchange(ref _heldNewWorld, 0) != 0)
            {
                await SendRawAsync(WorldOpcode.MsgMoveWorldportAck, []);
            }
        }

        /// <summary>Whether <paramref name="opcode"/> was received since <paramref name="since"/>.</summary>
        public bool Saw(long since, WorldOpcode opcode)
        {
            lock (_received)
            {
                return _received.Any(p => p.Sequence >= since && p.Opcode == opcode);
            }
        }

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
                    if (HoldWorldportAcks)
                    {
                        Volatile.Write(ref _heldNewWorld, 1);
                    }
                    else
                    {
                        await SendRawAsync(WorldOpcode.MsgMoveWorldportAck, []);
                    }
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
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

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
    /// World thread: binds the tap's player to <paramref name="match"/> the way a queue leaves it (a participant when
    /// <paramref name="participant"/>, holding a queue slot and the invitation to the match when <paramref name="invited"/>) and teleports it
    /// to the match's start.
    /// </summary>
    private static void BindAndTeleport(ScenarioContext context, ClientTap tap, Battleground match, bool participant, bool invited)
    {
        BattlegroundFeature feature = Battlegrounds(context);
        Player p = context.World.FindOnlinePlayer(tap.Name)!;
        feature.Manager.BindPlayer(p.Guid, match, p.Team);
        if (participant)
        {
            ScenarioContext.Expect(match.AddPlayer(p.Guid, p.Team), tap.Name + " could not be added to the match");
        }

        if (invited)
        {
            BattlegroundPlayerState state = feature.Manager.StateOf(p.Guid);
            state.AddSlot(match.Template.QueueType);
            state.SetInvite(match.Template.QueueType, match.InstanceId);
        }

        BattlegroundStartLocation start = p.Team == Team.Alliance ? match.Template.AllianceStart : match.Template.HordeStart;
        TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
        if (!teleports.TeleportTo(p, Wsg, start.X, start.Y, start.Z, start.Orientation))
        {
            throw new ScenarioAssertionException(
                $"{tap.Name} could not be teleported into the match: stage={teleports.StageOf(p)} map={p.Map?.MapId} bound={feature.BattlegroundOf(p.Guid)?.InstanceId} "
                + $"matchAlive={feature.Manager.GetBattleground(match.InstanceId, match.Type) is not null} allowed={feature.CanEnterMap(p, Wsg)} start=({start.X},{start.Y},{start.Z},{start.Orientation})");
        }
    }

    private static Task WaitInMatchAsync(ScenarioContext context, ClientTap tap, Battleground match)
        => context.WaitUntilAsync(tap.Name + " is in the match's map instance", () =>
            context.World.FindOnlinePlayer(tap.Name) is { IsInWorld: true, Map: { } map } && map.MapId == Wsg && map.InstanceId == match.InstanceId);

    /// <summary>Puts the tap's player into <paramref name="match"/> (<see cref="BindAndTeleport"/>) and waits until it stands in the match's map instance.</summary>
    private static async Task PutInMatchAsync(ScenarioContext context, ClientTap tap, Battleground match, bool participant, bool invited)
    {
        await context.ReadAsync(() =>
        {
            BindAndTeleport(context, tap, match, participant, invited);
            return true;
        });
        await WaitInMatchAsync(context, tap, match);
    }

    /// <summary>
    /// Creates the second match and puts <paramref name="anchor"/> into it as a participant in the same world-thread step (an empty match is deleted
    /// by its first update tick), and waits until the anchor stands in the match's map instance.
    /// </summary>
    private static async Task<Battleground> CreateMatchWithAnchorAsync(ScenarioContext context, ClientTap anchor, Battleground like)
    {
        Battleground b = await context.ReadAsync(() =>
        {
            Battleground created = CreateSecondMatch(context, like);
            BindAndTeleport(context, anchor, created, participant: true, invited: false);
            return created;
        });
        await WaitInMatchAsync(context, anchor, b);
        return b;
    }

    /// <summary>Everything about a GM move that the tests compare, read on the world thread.</summary>
    private sealed record MoveView(
        bool InMapOfB, bool BoundToA, bool BoundToB, bool ParticipantOfA, bool ParticipantOfB, int PlayersOfA,
        int SlotOfA, uint InviteOfA, bool Deserter, bool HasRecord, bool Online, BattlegroundEntryPoint? EntryPoint);

    private static Task<MoveView> ViewAsync(ScenarioContext context, string name, Battleground a, Battleground b)
        => context.ReadAsync(() =>
        {
            BattlegroundFeature feature = Battlegrounds(context);
            Player? p = context.World.FindOnlinePlayer(name);
            if (p is null)
            {
                return new MoveView(false, false, false, false, false, a.PlayerCount, -1, 0, false, false, false, null);
            }

            BattlegroundPlayerState state = feature.Manager.StateOf(p.Guid);
            Battleground? bound = feature.BattlegroundOf(p.Guid);
            return new MoveView(
                p.Map is { } map && map.MapId == Wsg && map.InstanceId == b.InstanceId,
                bound == a,
                bound == b,
                a.PlayerTeam(p.Guid) is not null,
                b.PlayerTeam(p.Guid) is not null,
                a.PlayerCount,
                state.SlotOf(a.Template.QueueType),
                state.InvitedInstance(a.Template.QueueType),
                context.Services.GetRequiredService<SpellFeature>().System.HasAura(p, BattlegroundConstants.SpellDeserter),
                feature.HasGmMove(p.Guid),
                true,
                feature.EntryPointOf(p.Guid));
        });

    /// <summary>
    /// Creates the second match B (kept alive by <paramref name="anchor"/> as a participant), sends the GM into B with <c>.goname</c>, and puts
    /// the victim into A as a participant holding A's queue slot and invitation. Returns B.
    /// </summary>
    private static async Task<Battleground> PrepareMoveAsync(ScenarioContext context, ClientTap gm, ClientTap victim, ClientTap anchor, Battleground a)
    {
        Battleground b = await CreateMatchWithAnchorAsync(context, anchor, a);
        await AppearAtAsync(context, gm, anchor.Name, b);
        await PutInMatchAsync(context, victim, a, participant: true, invited: true);
        return b;
    }

    /// <summary>The GM types <c>.namego</c> for the victim, whose client holds its loading screen; returns once the client holds the SMSG_NEW_WORLD.</summary>
    private static async Task NamegoHeldAsync(ScenarioContext context, ClientTap gm, ClientTap victim)
    {
        victim.HoldWorldportAcks = true;
        long mark = gm.Mark();
        await gm.SayAsync(".namego " + victim.Name);
        await context.WaitUntilAsync("the victim's client holds the loading screen and the GM is told it is summoning",
            () => victim.HoldingNewWorld && gm.ChatSince(mark).Any(t => t.StartsWith("You are summoning ", StringComparison.Ordinal)));
    }

    /// <summary>What a finished A to B move looks like: in B's instance, bound to B only, out of A with slot and invitation released, no Deserter.</summary>
    private static async Task ExpectMovedToBAsync(ScenarioContext context, ClientTap victim, Battleground a, Battleground b, int playersOfABefore, BattlegroundEntryPoint? entryBefore, string when)
    {
        await context.WaitUntilAsync("the victim arrives in B and is bound to it", () => context.World.FindOnlinePlayer(victim.Name) is { IsInWorld: true, Map: { } map } p
            && map.MapId == Wsg && map.InstanceId == b.InstanceId && Battlegrounds(context).BattlegroundOf(p.Guid) == b);
        MoveView v = await ViewAsync(context, victim.Name, a, b);
        ScenarioContext.Expect(v.InMapOfB && v.BoundToB && !v.BoundToA, when + ": the victim is not in B and bound to B");
        ScenarioContext.Expect(!v.ParticipantOfB, when + ": the victim became a participant of B");
        ScenarioContext.Expect(!v.ParticipantOfA, when + ": the victim is still a participant of A");
        ScenarioContext.ExpectEqual(playersOfABefore - 1, v.PlayersOfA, when + ": players of A");
        ScenarioContext.ExpectEqual(-1, v.SlotOfA, when + ": A's queue slot of the victim");
        ScenarioContext.ExpectEqual(0u, v.InviteOfA, when + ": A's invitation of the victim");
        ScenarioContext.Expect(!v.Deserter, when + ": the victim got Deserter from a GM move");
        ScenarioContext.Expect(!v.HasRecord, when + ": the GM move record is still there");
        ScenarioContext.ExpectEqual(entryBefore, v.EntryPoint, when + ": entry point");
    }

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
            await ExpectMovedToBAsync(context, victim, a, b, 0 + 1, v.EntryPoint, "after A removed the victim");
            ScenarioContext.Expect(!anomalies.Any("teleport/map-entry-failed") && !anomalies.Any("teleport/return-failed"), "the arrival failed: " + anomalies.Describe());
        });
    }

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
