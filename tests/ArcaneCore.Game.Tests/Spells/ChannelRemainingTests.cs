using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <see cref="SpellSystem.ChannelRemainingMs"/>: vmangos <c>GetCurrentSpell(CURRENT_CHANNELED_SPELL)->GetCastedTime()</c>, the time
/// left on the running channel, which SMSG_AREA_SPIRIT_HEALER_TIME reports for a spirit guide (Creature.cpp:3553-3564).
/// </summary>
public sealed class ChannelRemainingTests
{
    [Fact]
    public void CountsDownWhileChannelling_AndIsZeroOtherwise()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(0u, kit.System.ChannelRemainingMs(player));

        kit.Spellbook.Teach(player, ChannelSpell);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());
        uint start = kit.System.ChannelRemainingMs(player);
        Assert.True(start > 0);

        kit.Advance(1000);
        Assert.Equal(start - 1000, kit.System.ChannelRemainingMs(player));

        kit.System.CancelChannel(player);
        Assert.Equal(0u, kit.System.ChannelRemainingMs(player));
    }
}
