using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Lane g1-scripts: the DUMMY effects that act on creatures (Dream Fog 24781, the Tesla effects 28098 and 28110).</summary>
public sealed class G1CreatureScriptTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo AtUnit(uint id) => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.Unit)) with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 40),
    });

    private static SpellInfo Marker(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, target, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 40),
        });

    private static PetTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new PetTestKit(spells);
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void DreamFog_MakesACreatureCasterAttackItsTarget()
    {
        using PetTestKit kit = Kit(AtUnit(24781));
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature fog = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 8, 5, 83.5f, 0);

        kit.Spells.System.CastSpell(fog, 24781, SpellCastTargets.ForUnit(player.Guid), triggered: true);

        Assert.Same(player, fog.Combat.Victim);
    }

    [Fact]
    public void TeslaEffect_AnAddNearItsCoilEvadesAndChainsAgain()
    {
        using PetTestKit kit = Kit(AtUnit(28098), Marker(28097), Marker(28096));
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature coil = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 8, 5, 83.5f, 0);
        Creature stalagg = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 10, 5, 83.5f, 0);
        Assert.True(kit.Spells.System.AddAura(stalagg, 28097));
        stalagg.Combat.Threat.AddThreat(player, 100f);

        kit.Spells.System.CastSpell(coil, 28098, SpellCastTargets.ForUnit(stalagg.Guid), triggered: true);

        Assert.False(kit.Spells.System.HasAura(stalagg, 28097)); // Stalagg Tesla Passive removed
        Assert.True(stalagg.Combat.Threat.IsEmpty);              // threat list deleted
        Assert.True(kit.Spells.System.HasAura(stalagg, 28096));  // chain cast again (the marker lands on its caster)
    }

    [Theory]
    [InlineData(28098u, 28097u, 28096u)]
    [InlineData(28110u, 28109u, 28111u)]
    public void TeslaEffect_PicksPassiveAndChainBySpell(uint spell, uint passive, uint chain)
        => Assert.Equal((passive, chain), TeslaEffectScript.For(spell));
}
