using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// vmangos SpellAuraHolder::HandleSpellSpecificBoosts (SpellAuras.cpp:7130-7198): Bestial Wrath brings and takes its four immunity spells,
/// Ghost Wolf removes Water Walking.
/// </summary>
public sealed class SpellSpecificBoostTests
{
    private static readonly uint[] Immunities = [24395, 24396, 24397, 26592];

    private static SpellInfo Aura(uint id, AuraType type, int misc = 0) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, type, misc: misc)) with
        {
            Duration = new SpellDuration(18_000, 0, 18_000),
            SpellVisual = 1,
        };

    private static SpellTestKit Kit() => new(
        [Aura(19574, AuraType.ModDamagePercentDone, 127), .. Immunities.Select(id => Aura(id, AuraType.MechanicImmunity, 5)),
            Aura(2645, AuraType.ModIncreaseSpeed), Aura(546, AuraType.WaterWalk)]);

    [Fact]
    public void BestialWrath_BringsItsImmunities_AndTakesThemAway()
    {
        using SpellTestKit kit = Kit();
        (Player hunter, _) = kit.AddPlayer(1);
        (Player pet, _) = kit.AddPlayer(2, 3, 0);

        Assert.True(kit.System.AddAura(pet, 19574, caster: hunter));
        Assert.All(Immunities, id => Assert.True(kit.System.HasAura(pet, id)));
        Assert.All(kit.System.GetAuras(pet), h => Assert.Equal(hunter.Guid, h.CasterGuid));

        kit.System.RemoveAuras(pet, 19574);
        Assert.All(Immunities, id => Assert.False(kit.System.HasAura(pet, id)));
    }

    [Fact]
    public void GhostWolf_RemovesWaterWalking()
    {
        using SpellTestKit kit = Kit();
        (Player shaman, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(shaman, 546));

        Assert.True(kit.System.AddAura(shaman, 2645));
        Assert.False(kit.System.HasAura(shaman, 546));
    }
}