using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class VanishScriptTests
{
    private static readonly SpellInfo Vanish = Spell(1856,
        Effect(SpellEffectName.TriggerSpell, 0, trigger: 11327),
        Effect(SpellEffectName.TriggerSpell, 0, trigger: 18461),
        Effect(SpellEffectName.Sanctuary, 0)) with
    { SpellFamilyName = 8, SpellFamilyFlags = 1UL << 11 };

    private static readonly SpellInfo[] Spells =
    [
        Vanish,
        Spell(11327, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { Duration = new SpellDuration(10_000, 0, 10_000) },
        Spell(18461, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { Duration = new SpellDuration(10_000, 0, 10_000) },
        Spell(1784, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
        Spell(1785, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
        Spell(91001, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModRoot)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
        Spell(91002, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModDecreaseSpeed)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
        Spell(91003, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.ModStalked)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000), RangeIndex = 4, Range = new SpellRange(0, 30),
            SpellFamilyName = 6, SpellFamilyFlags = 1UL << 26, // Mind Vision's stalked-target exemption
        },
    ];

    [Fact]
    public void Vanish_SecondTriggerClearsRootSlowAndMark_AndCastsHighestKnownStealth()
    {
        using var kit = new SpellTestKit(Spells);
        (Player rogue, _) = kit.AddPlayer(1);
        (Player observer, _) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(rogue, 1784, 1785);
        kit.System.RankChains = new SpellRankChains(
        [
            new SkillLineAbilityRecord(1, 8, 1784, 0, 0, 0, 1785, 0, 0, 0),
            new SkillLineAbilityRecord(2, 8, 1785, 0, 0, 0, 0, 0, 0, 0),
        ]);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellSystem).Assembly));
        foreach (uint spell in new uint[] { 91001, 91002 })
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, spell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(observer, 91003, SpellCastTargets.ForUnit(rogue.Guid), triggered: true));
        Assert.True(kit.System.HasAuraType(rogue, AuraType.ModRoot));
        Assert.True(kit.System.HasAuraType(rogue, AuraType.ModDecreaseSpeed));
        Assert.True(kit.System.HasAuraType(rogue, AuraType.ModStalked));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, 1856, SpellCastTargets.ForSelf(), triggered: true));

        Assert.False(kit.System.HasAuraType(rogue, AuraType.ModRoot));
        Assert.False(kit.System.HasAuraType(rogue, AuraType.ModDecreaseSpeed));
        Assert.False(kit.System.HasAuraType(rogue, AuraType.ModStalked));
        Assert.True(kit.System.HasAura(rogue, 1785));
        Assert.False(kit.System.HasAura(rogue, 1784));
        Assert.True(kit.System.HasAura(rogue, 18461));
    }

    [Fact]
    public void Vanish_WithoutKnownStealthStillClearsMovementAndTrackingAuras()
    {
        using var kit = new SpellTestKit(Spells);
        (Player rogue, _) = kit.AddPlayer(1);
        (Player observer, _) = kit.AddPlayer(2, 2);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellSystem).Assembly));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, 91001, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(observer, 91003, SpellCastTargets.ForUnit(rogue.Guid), triggered: true));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(rogue, 1856, SpellCastTargets.ForSelf(), triggered: true));

        Assert.False(kit.System.HasAuraType(rogue, AuraType.ModRoot));
        Assert.False(kit.System.HasAuraType(rogue, AuraType.ModStalked));
        Assert.False(kit.System.HasAura(rogue, 1784));
        Assert.False(kit.System.HasAura(rogue, 1785));
    }
}
