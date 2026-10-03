using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// GUARD (characterization, green on arrival, not a RED proof): the Earth/Flame/Frost Shock share
/// spell category 19 with a 6000 ms category recovery in classic-db spell_template (17 castable ranks,
/// StartRecoveryCategory 133 / 1500 ms), so the shared shock cooldown is pure data through the existing
/// category-cooldown path (SpellSystem category recovery). Mutation that must turn it red: give the
/// Frost Shock below Category = 0.
/// </summary>
public sealed class ShockCooldownGuardTests
{
    private const uint Earth = 920001;
    private const uint Flame = 920002;
    private const uint Frost = 920003;

    private static SpellInfo Shock(uint id, uint category) => Spell(id, Effect(SpellEffectName.Dummy, 0)) with
    {
        Category = category,
        CategoryRecoveryTime = 6000,
        StartRecoveryCategory = SpellConstants.GlobalCooldownCategory,
        StartRecoveryTime = 1500,
    };

    [Fact]
    public void Shocks_ShareCategory19_SixSecondRecovery_AcrossSchools()
    {
        using var kit = new SpellTestKit(Shock(Earth, 19), Shock(Flame, 19), Shock(Frost, 19));
        (Player caster, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Earth, SpellCastTargets.ForSelf(), triggered: false));
        kit.Advance(2000);

        // The 1.5 s global cooldown is over; the category cooldown (6 s) is not.
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(caster, Flame, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(caster, Frost, SpellCastTargets.ForSelf(), triggered: false));

        kit.Advance(4500);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Frost, SpellCastTargets.ForSelf(), triggered: false));
    }
}
