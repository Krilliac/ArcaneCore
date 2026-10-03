using ArcaneCore.Game.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>The fixed-price charge primitive for charters and tabards (vmangos Player::ModifyMoney).</summary>
public sealed class NpcChargeTests
{
    [Fact]
    public void TryCharge_RefusesWhenThePlayerCannotPay_AndChangesNothing()
    {
        using var kit = new NpcServiceKit(NpcFlags.Petitioner);
        kit.Player.Money = 999;
        int saves = kit.Sink.CharacterChanges;

        Assert.False(kit.Services.TryCharge(kit.Player, 1000));

        Assert.Equal(999u, kit.Player.Money);
        Assert.Equal(saves, kit.Sink.CharacterChanges);
    }

    [Fact]
    public void TryCharge_SubtractsExactly_AndSavesTheCharacter()
    {
        using var kit = new NpcServiceKit(NpcFlags.Petitioner);
        kit.Player.Money = 1000;

        Assert.True(kit.Services.TryCharge(kit.Player, 1000));

        Assert.Equal(0u, kit.Player.Money);
        Assert.True(kit.Sink.CharacterChanges > 0);
    }
}
