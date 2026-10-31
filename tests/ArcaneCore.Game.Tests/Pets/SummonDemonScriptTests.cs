using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Warlock;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// SPELL_EFFECT_SUMMON_DEMON runs the spell script's OnSummon (vmangos Spell::EffectSummonDemon, SpellEffects.cpp:5818): Inferno 1122 and 24670
/// enslave the Infernal (20882 cast by the warlock on it) and the Infernal roots (22707) and stuns (22703) itself
/// (scripts/spells/spell_warlock.cpp:239-253). The spell shapes are reduced to one aura effect each.
/// </summary>
public sealed class SummonDemonScriptTests
{
    private const uint InfernalEntry = PetTestKit.WildEntry;
    private const uint PlainDemonSpell = 971_201;

    private static IEnumerable<SpellInfo> Spells() =>
    [
        Spell(WarlockInfernoScript.Inferno, Effect(SpellEffectName.SummonDemon, 0, SpellImplicitTarget.LocationCasterDest, misc: (int)InfernalEntry)) with
        {
            Duration = new SpellDuration(3_600_000, 0, 3_600_000),
        },
        Spell(WarlockInfernoScript.InfernoRank2, Effect(SpellEffectName.SummonDemon, 0, SpellImplicitTarget.LocationCasterDest, misc: (int)InfernalEntry)) with
        {
            Duration = new SpellDuration(3_600_000, 0, 3_600_000),
        },
        Spell(PlainDemonSpell, Effect(SpellEffectName.SummonDemon, 0, SpellImplicitTarget.LocationCasterDest, misc: (int)InfernalEntry)),
        Spell(WarlockInfernoScript.EnslaveDemon, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(300_000, 0, 300_000),
        },
        Spell(WarlockInfernoScript.InfernoRoot, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(300_000, 0, 300_000),
        },
        Spell(WarlockInfernoScript.InfernoEffect, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(300_000, 0, 300_000),
        },
    ];

    [Theory]
    [InlineData(WarlockInfernoScript.Inferno)]
    [InlineData(WarlockInfernoScript.InfernoRank2)]
    public void Inferno_TheWarlockEnslavesTheInfernal_AndTheInfernalRootsAndStunsItself(uint inferno)
    {
        using var kit = new PetTestKit(Spells());
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player warlock, _) = kit.AddPlayer(1, 5, 6);
        warlock.Level = 50;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(warlock, inferno));

        Creature infernal = Assert.Single(kit.Creatures.Creatures, c => c.Entry == InfernalEntry);
        SpellAuraHolder enslave = Assert.Single(kit.Spells.System.GetAuras(infernal), h => h.Spell.Id == WarlockInfernoScript.EnslaveDemon);
        Assert.Equal(warlock.Guid, enslave.CasterGuid);
        SpellAuraHolder root = Assert.Single(kit.Spells.System.GetAuras(infernal), h => h.Spell.Id == WarlockInfernoScript.InfernoRoot);
        Assert.Equal(infernal.Guid, root.CasterGuid);
        SpellAuraHolder stun = Assert.Single(kit.Spells.System.GetAuras(infernal), h => h.Spell.Id == WarlockInfernoScript.InfernoEffect);
        Assert.Equal(infernal.Guid, stun.CasterGuid);
    }

    [Fact]
    public void AnotherDemonSummon_WithoutAScript_GetsNoAuras()
    {
        using var kit = new PetTestKit(Spells());
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player warlock, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(warlock, PlainDemonSpell));

        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.Entry == InfernalEntry);
        Assert.Empty(kit.Spells.System.GetAuras(demon));
    }
}
