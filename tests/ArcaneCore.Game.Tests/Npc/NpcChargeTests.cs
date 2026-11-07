using ArcaneCore.Game.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>The fixed-price charge primitive for charters and tabards (vmangos Player::ModifyMoney).</summary>
public sealed class NpcChargeTests
{
    [Fact]
    public void TryCharge_RefusesWhenThePlayerCannotPay_AndChangesNothing()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Petitioner);
        kit.Player.Money = 999;
        int saves = kit.Sink.CharacterChanges;

        Assert.False(kit.Services.TryCharge(kit.Player, 1000));

        Assert.Equal(999u, kit.Player.Money);
        Assert.Equal(saves, kit.Sink.CharacterChanges);
    }

    [Fact]
    public void TryCharge_SubtractsExactly_AndSavesTheCharacter()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Petitioner);
        kit.Player.Money = 1000;

        Assert.True(kit.Services.TryCharge(kit.Player, 1000));

        Assert.Equal(0u, kit.Player.Money);
        Assert.True(kit.Sink.CharacterChanges > 0);
    }

    [Fact]
    public void ChargeFlightLeg_ClampsAtZeroAndNeverRefuses()
    {
        // vmangos FlightPathMovementGenerator::Update calls ModifyMoney(-cost) at a leg boundary, which clamps at zero (Player.h:1025-1031).
        using var kit = new NpcServiceTestKit(NpcFlags.Petitioner);
        kit.Player.Money = 30;
        int saves = kit.Sink.CharacterChanges;

        Assert.True(kit.Services.ChargeFlightLeg(kit.Player, 50));

        Assert.Equal(0u, kit.Player.Money);
        Assert.True(kit.Sink.CharacterChanges > saves);
    }

    [Fact]
    public void ChargeFlightLeg_SubtractsTheFareWhenThePlayerCanPay()
    {
        using var kit = new NpcServiceTestKit(NpcFlags.Petitioner);
        kit.Player.Money = 120;

        Assert.True(kit.Services.ChargeFlightLeg(kit.Player, 50));

        Assert.Equal(70u, kit.Player.Money);
    }
}
