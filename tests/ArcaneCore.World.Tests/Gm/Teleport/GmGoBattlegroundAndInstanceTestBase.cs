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
/// <c>.goname</c> and <c>.namego</c> into a battleground or a dungeon instance (vmangos HandleGonameCommand
/// TeleportCommands.cpp:1242-1303, HandleNamegoCommand :1163-1168). The reported bug: <c>.appear &lt;player&gt;</c> with that player in Warsong Gulch
/// answered "Target map or coordinates is invalid" because the teleport gate admits a battleground map only to a player bound to
/// its match, and the GM was bound to none. The battleground tests run a real Warsong Gulch match between two scenario bots
/// (<see cref="WarsongGulchTestContent"/>) and drive the GM through a socket client; the dungeon tests use Deadmines.
/// </summary>
public abstract class GmGoBattlegroundAndInstanceTestBase
{
    private protected const uint Wsg = 489;
    private protected const uint Deadmines = 36;
    private protected static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private protected sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private protected static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
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
    private protected sealed class ClientTap : IAsyncDisposable
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

    private protected static BattlegroundFeature Battlegrounds(ScenarioContext context) => context.Services.GetRequiredService<BattlegroundFeature>();

    /// <summary>The invitation counts and free slots of both teams: what a queue over-invites from when a visitor skews them.</summary>
    private protected static (uint AllianceInvited, uint HordeInvited, uint AllianceFree, uint HordeFree) Slots(Battleground match)
        => (match.InvitedCount(Team.Alliance), match.InvitedCount(Team.Horde), match.FreeSlotsForTeam(Team.Alliance), match.FreeSlotsForTeam(Team.Horde));

    /// <summary>A second running Warsong Gulch match (world thread). It lives only while somebody is in it or invited.</summary>
    private protected static Battleground CreateSecondMatch(ScenarioContext context, Battleground like)
    {
        BattlegroundManager manager = Battlegrounds(context).Manager;
        Battleground second = manager.CreateNewBattleground(BattlegroundType.WarsongGulch, like.Bracket)
            ?? throw new ScenarioAssertionException("could not create a second match");
        manager.StartBattleground(second);
        return second;
    }

    private protected static ObjectGuid GuidOf(ScenarioContext context, string name)
        => context.World.FindOnlinePlayer(name)?.Guid ?? throw new ScenarioAssertionException($"{name} is not online");

    /// <summary>
    /// World thread: binds the tap's player to <paramref name="match"/> the way a queue leaves it (a participant when
    /// <paramref name="participant"/>, holding a queue slot and the invitation to the match when <paramref name="invited"/>) and teleports it
    /// to the match's start.
    /// </summary>
    private protected static void BindAndTeleport(ScenarioContext context, ClientTap tap, Battleground match, bool participant, bool invited)
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

    private protected static Task WaitInMatchAsync(ScenarioContext context, ClientTap tap, Battleground match)
        => context.WaitUntilAsync(tap.Name + " is in the match's map instance", () =>
            context.World.FindOnlinePlayer(tap.Name) is { IsInWorld: true, Map: { } map } && map.MapId == Wsg && map.InstanceId == match.InstanceId);

    /// <summary>Puts the tap's player into <paramref name="match"/> (<see cref="BindAndTeleport"/>) and waits until it stands in the match's map instance.</summary>
    private protected static async Task PutInMatchAsync(ScenarioContext context, ClientTap tap, Battleground match, bool participant, bool invited)
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
    private protected static async Task<Battleground> CreateMatchWithAnchorAsync(ScenarioContext context, ClientTap anchor, Battleground like)
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
    private protected sealed record MoveView(
        bool InMapOfB, bool BoundToA, bool BoundToB, bool ParticipantOfA, bool ParticipantOfB, int PlayersOfA,
        int SlotOfA, uint InviteOfA, bool Deserter, bool HasRecord, bool Online, BattlegroundEntryPoint? EntryPoint);

    private protected static Task<MoveView> ViewAsync(ScenarioContext context, string name, Battleground a, Battleground b)
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
    private protected static async Task<Battleground> PrepareMoveAsync(ScenarioContext context, ClientTap gm, ClientTap victim, ClientTap anchor, Battleground a)
    {
        Battleground b = await CreateMatchWithAnchorAsync(context, anchor, a);
        await AppearAtAsync(context, gm, anchor.Name, b);
        await PutInMatchAsync(context, victim, a, participant: true, invited: true);
        return b;
    }

    /// <summary>The GM types <c>.namego</c> for the victim, whose client holds its loading screen; returns once the client holds the SMSG_NEW_WORLD.</summary>
    private protected static async Task NamegoHeldAsync(ScenarioContext context, ClientTap gm, ClientTap victim)
    {
        victim.HoldWorldportAcks = true;
        long mark = gm.Mark();
        await gm.SayAsync(".namego " + victim.Name);
        await context.WaitUntilAsync("the victim's client holds the loading screen and the GM is told it is summoning",
            () => victim.HoldingNewWorld && gm.ChatSince(mark).Any(t => t.StartsWith("You are summoning ", StringComparison.Ordinal)));
    }

    /// <summary>What a finished A to B move looks like: in B's instance, bound to B only, out of A with slot and invitation released, no Deserter.</summary>
    private protected static async Task ExpectMovedToBAsync(ScenarioContext context, ClientTap victim, Battleground a, Battleground b, int playersOfABefore, BattlegroundEntryPoint? entryBefore, string when)
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
    /// The GM types <c>.goname</c> and waits until it stands in the match's map instance, or until the server answered the reported bug's
    /// "Target map or coordinates is invalid", which fails the scenario at once with that text.
    /// </summary>
    private protected static async Task AppearAtAsync(ScenarioContext context, ClientTap gm, string targetName, Battleground match)
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

    // Stand in the trigger box at level 20 and walk through it: the dungeon transfer, then the ack.
    private protected static async Task EnterThroughTriggerAsync(WorldTestHost host, WorldTestClient client, string name)
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
