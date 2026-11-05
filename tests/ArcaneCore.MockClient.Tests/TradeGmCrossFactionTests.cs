using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// CMSG_INITIATE_TRADE across factions: refused as WrongFaction for a normal player, accepted for a player with GM mode on. These
/// run on the full synthetic server because trading needs the economy feature and the character save queue, which the lightweight
/// world test host does not provide.
/// </summary>
public sealed class TradeGmCrossFactionTests
{
    /// <summary>Turns the receiver into an Orc (Horde) while the sender stays Human (Alliance), and sets the sender's GM mode.</summary>
    private static Task PrepareAsync(Rig rig, bool senderGm) => rig.Server.World.InvokeAsync(() =>
    {
        Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
        Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
        receiver.SetByte(UpdateFields.UnitFieldBytes0, 0, (byte)Race.Orc);
        Assert.NotEqual(sender.Team, receiver.Team);
        sender.Flags = senderGm ? sender.Flags | PlayerFlags.Gm : sender.Flags & ~PlayerFlags.Gm;
        return true;
    }).WaitAsync(rig.Token);

    [Fact]
    public async Task Trade_WithTheOtherFaction_IsRefusedAsWrongFactionWithGmModeOff()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        await PrepareAsync(rig, senderGm: false);

        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);

        byte[] reply = await rig.Sender.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);
        Assert.Equal((uint)TradeStatus.WrongFaction, BinaryPrimitives.ReadUInt32LittleEndian(reply));
    }

    [Fact]
    public async Task Trade_WithTheOtherFaction_InvitesThemWithGmModeOn()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        await PrepareAsync(rig, senderGm: true);

        await rig.Sender.SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioWire.Guid(rig.ReceiverGuid), rig.Token);

        byte[] invite = await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgTradeStatus, rig.Token);
        Assert.Equal((uint)TradeStatus.BeginTrade, BinaryPrimitives.ReadUInt32LittleEndian(invite));
        Assert.Equal(rig.SenderGuid, BinaryPrimitives.ReadUInt64LittleEndian(invite.AsSpan(4)));
    }
}
