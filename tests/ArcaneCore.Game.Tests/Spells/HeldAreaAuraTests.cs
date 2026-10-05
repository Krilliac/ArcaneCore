using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class HeldAreaAuraTests
{
    [Fact]
    public void RemovedSource_KeepsChildDuringSettlement_ThenRemovesItAfterRelease()
    {
        const uint area = 29803;
        SpellInfo spell = Spell(area, Effect(SpellEffectName.ApplyAreaAuraParty, 0, aura: AuraType.Dummy) with { Radius = 30 })
            with { Duration = new SpellDuration(-1, 0, -1) };
        using var kit = new SpellTestKit(spell);
        var (owner, _) = kit.AddPlayer(1);
        var (member, _) = kit.AddPlayer(2, 5);
        var groups = new FakeGroups();
        groups.Parties.Add([owner.Guid, member.Guid]);
        kit.System.Groups = groups;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(owner, area, SpellCastTargets.ForSelf(), triggered: true));
        kit.Advance(100);
        Assert.True(kit.System.HasAura(member, area));
        Guid operation = Guid.NewGuid();
        Assert.True(member.BeginQuestSettlement(operation));

        kit.System.RemoveAuras(owner, area);
        kit.Advance(100);
        Assert.True(kit.System.HasAura(member, area));

        Assert.True(member.EndQuestSettlement(operation));
        kit.Advance(100);
        Assert.False(kit.System.HasAura(member, area));
    }
}
