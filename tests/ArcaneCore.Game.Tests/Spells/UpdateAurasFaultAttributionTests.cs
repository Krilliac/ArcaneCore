using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class UpdateAurasFaultAttributionTests
{
    private sealed class ThrowingGroups(Exception fault) : ISpellGroupResolver
    {
        public bool Armed { get; set; }

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
            => Armed ? throw fault : [unit.Guid];
    }

    [Fact]
    public void UpdateAreaAurasFault_DoesNotBlameUnrelatedFirstLiveAura()
    {
        const uint passive = 29810;
        const uint area = 29811;
        SpellInfo passiveSpell = Spell(passive, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy))
            with { Duration = new SpellDuration(-1, 0, -1) };
        SpellInfo areaSpell = Spell(area, Effect(SpellEffectName.ApplyAreaAuraParty, 0, aura: AuraType.Dummy) with { Radius = 30 })
            with { Duration = new SpellDuration(-1, 0, -1) };
        using var kit = new SpellTestKit(passiveSpell, areaSpell);
        var (owner, _) = kit.AddPlayer(1);
        var fault = new InvalidOperationException("group resolver failed");
        var groups = new ThrowingGroups(fault);
        kit.System.Groups = groups;

        // The unrelated passive aura is the first live holder on the unit; the area source is not.
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(owner, passive, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(owner, area, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(owner, passive));
        Assert.True(kit.System.HasAura(owner, area));

        groups.Armed = true;
        kit.Advance(100);

        // The per-source catch in UpdateAreaAuras records the fault against the area source's spell,
        // and must not blame the unrelated passive aura that happens to be the first live holder.
        Assert.True(fault.Data.Contains("SpellId"), "the per-source UpdateAreaAuras catch should have recorded the fault");
        Assert.Equal(area, Assert.IsType<uint>(fault.Data["SpellId"]));
        Assert.NotEqual(passive, Assert.IsType<uint>(fault.Data["SpellId"]));
        Assert.Equal($"Spell {area}", fault.Data["SpellName"]);
    }
}
