using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotMovementControlTests
{
    [Fact]
    public async Task ActualFlagAndSpeedOrders_AreAcknowledgedWithinTheSharedBudget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                MovementControl.Request(player, MovementChangeType.WaterWalk, true);
                Assert.True(player.Locomotion.Pending.HasPending);
                session.ManagedBudget = new ManagedActionBudget(0);
                Assert.True(PlayerbotMovementControl.Update(session, player));
                Assert.True(player.Locomotion.Pending.HasPending);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotMovementControl.Update(session, player));
                Assert.False(player.Locomotion.Pending.HasPending);
                Assert.True(player.Movement.HasFlag(MovementFlags.WaterWalking));
                UnitSpeed.SetRate(player, MoveType.Run, 0.5f);
                Assert.True(player.Locomotion.Pending.HasPending);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotMovementControl.Update(session, player));
                Assert.False(player.Locomotion.Pending.HasPending);
                Assert.Equal(Unit.BaseRunSpeed * 0.5f, UnitSpeed.Get(player, MoveType.Run), 3);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ATurnRateChange_IsAcknowledgedByTheBot_ThroughThePendingLedger_AndTheBotKeepsWorking()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                int wrongBefore = player.Locomotion.WrongAckCount;
                UnitSpeed.SetTurnRate(player, 0.5f);
                float turn = player.TurnRate;
                Assert.Equal(Unit.BaseTurnRate * 0.5f, turn, 3);
                Assert.True(player.Locomotion.Pending.HasPendingOfType(MovementChangeType.TurnRate));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotMovementControl.Update(session, player));
                Assert.False(player.Locomotion.Pending.HasPending);
                Assert.Equal(wrongBefore, player.Locomotion.WrongAckCount);
                Assert.Equal(turn, player.TurnRate);
                // A later order of another kind is still acknowledged: the turn rate entry did not wedge the ledger.
                UnitSpeed.SetRate(player, MoveType.Run, 0.5f);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotMovementControl.Update(session, player));
                Assert.False(player.Locomotion.Pending.HasPending);
                Assert.Equal(wrongBefore, player.Locomotion.WrongAckCount);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task SelectivePacketDrain_PreservesOtherFamiliesAndMovementStopUsesNormalHandler()
    {
        await using WorldTestHost host = BotFidelity.Start(PlayerbotClientFidelity.Full);
        WorldSession session = await EnterAsync(host);
        try
        {
            session.DrainManagedPackets();
            // Use complete wire bodies so the opt-in wire oracle can observe this drain test too.
            session.Send(WorldOpcode.SmsgLootResponse, new byte[14] { 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0 });
            session.Send(WorldOpcode.SmsgNotification, [3, 0]);
            Assert.Single(session.DrainManagedPackets(WorldOpcode.SmsgLootResponse));
            Assert.Equal(WorldOpcode.SmsgNotification, Assert.Single(session.DrainManagedPackets()).Opcode);
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                MovementInfo moving = player.Movement; moving.Flags |= MovementFlags.Forward;
                var packet = new PacketWriter(); moving.Write(packet);
                session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, packet.ToArray());
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                Assert.True(PlayerbotMovementControl.Stop(session, player));
                Assert.Equal(MovementFlags.None, player.Movement.Flags & MovementFlags.MaskMoving);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    internal static async Task<WorldSession> EnterAsync(WorldTestHost host, byte race = 1)
    {
        Account owner = await host.Accounts.CreateAsync(new Account
        { Username = "BOTCONTROL", Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes,
            host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter(); create.WriteCString("Controlone"); create.WriteByte(race); create.WriteByte(1);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        var login = new PacketWriter(); login.WriteUInt64((ulong)(await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is not null, "control player login");
        return session;
    }
}
