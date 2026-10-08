using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Transports;
using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Duel.DuelRig;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// Duels and ships (vmangos Spell.cpp:6195-6196 NOT_ON_TRANSPORT; SpellEffects.cpp:4750-4755 DuelInfo.transportGuid;
/// Player::CheckDuelDistance, Player.cpp:6685-6689: aboard, the ship is the duel area).
/// </summary>
public sealed class DuelTransportTests
{
    private static ShipTransport Ferry(DuelRig rig)
    {
        TransportSystem system = TransportTestKit.Install(rig.World, TransportTestKit.Ferry);
        return system.FindByEntry(TransportTestKit.Ferry)!;
    }

    [Fact]
    public void Challenge_BetweenShipAndLand_IsRefused_NotOnTransport()
    {
        using var rig = new DuelRig();
        ShipTransport ship = Ferry(rig);
        ship.AddPassenger(rig.A);

        Assert.Equal(SpellCastResult.NotOnTransport, rig.Service.CheckChallenge(rig.A, rig.B));
        Assert.Equal(SpellCastResult.NotOnTransport, rig.Service.CheckChallenge(rig.B, rig.A));

        ship.AddPassenger(rig.B);
        Assert.Equal(SpellCastResult.CastOk, rig.Service.CheckChallenge(rig.A, rig.B));
    }

    [Fact]
    public void DuelRequestedAboard_IsBoundToTheShip()
    {
        using var rig = new DuelRig();
        ShipTransport ship = Ferry(rig);
        ship.AddPassenger(rig.A);
        ship.AddPassenger(rig.B);

        rig.Challenge();

        Assert.Equal(ship.Guid.Low, rig.A.Duel!.TransportGuid);
        Assert.Equal(ship.Guid.Low, rig.B.Duel!.TransportGuid);
        Assert.Equal(TransportTestKit.Ferry, rig.A.Duel.TransportGuid);
    }

    [Fact]
    public void DuelOnLand_HasNoShip()
    {
        using var rig = new DuelRig();
        Ferry(rig);

        rig.Challenge();

        Assert.Equal(0u, rig.A.Duel!.TransportGuid);
    }

    [Fact]
    public void ShipDuel_IgnoresDistance_WhileBothRideTheShip()
    {
        using var rig = new DuelRig();
        ShipTransport ship = Ferry(rig);
        ship.AddPassenger(rig.A);
        ship.AddPassenger(rig.B);
        rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        rig.B.SetPosition(rig.B.X + 500f, rig.B.Y, rig.B.Z, 0); // far from the flag, still aboard
        rig.Tick();
        rig.Tick();

        Assert.Empty(Packets(rig.SessionB, WorldOpcode.SmsgDuelOutofbounds));
        Assert.Equal(0, rig.B.Duel!.OutOfBoundSeconds);
    }

    [Fact]
    public void LeavingTheShip_IsLeavingTheDuelArea_AndTenSecondsOffItIsFled()
    {
        using var rig = new DuelRig();
        ShipTransport ship = Ferry(rig);
        ship.AddPassenger(rig.A);
        ship.AddPassenger(rig.B);
        rig.Challenge();
        rig.AcceptAndStart();
        rig.ClearPackets();

        ship.RemovePassenger(rig.B); // B steps off, though it stands right by the flag
        rig.Tick();
        Assert.Empty(Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelOutofbounds)));

        rig.Now += 9;
        rig.Tick();
        Assert.NotNull(rig.B.Duel);

        rig.Now += 1;
        rig.Tick();
        rig.Tick();
        Assert.Null(rig.A.Duel);
        Assert.Null(rig.B.Duel);
        Assert.NotEmpty(Packets(rig.SessionA, WorldOpcode.SmsgDuelComplete));
    }

    [Fact]
    public void BackAboardInTime_IsBackInBounds()
    {
        using var rig = new DuelRig();
        ShipTransport ship = Ferry(rig);
        ship.AddPassenger(rig.A);
        ship.AddPassenger(rig.B);
        rig.Challenge();
        rig.AcceptAndStart();
        ship.RemovePassenger(rig.B);
        rig.Tick();
        rig.ClearPackets();

        ship.AddPassenger(rig.B);
        rig.Now += 5;
        rig.Tick();

        Assert.Single(Packets(rig.SessionB, WorldOpcode.SmsgDuelInbounds));
        Assert.Equal(0, rig.B.Duel!.OutOfBoundSeconds);
    }
}
