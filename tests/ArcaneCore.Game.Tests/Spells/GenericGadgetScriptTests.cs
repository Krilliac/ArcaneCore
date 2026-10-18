using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>vmangos Spell::EffectDummy item and gadget cases ported to <c>Spells/Generic</c>.</summary>
public sealed class GenericGadgetScriptTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void GiftOfLife_GrantsFifteenPercentOfMaximumHealth()
    {
        using SpellTestKit kit = Kit(
            Instant(Spell(23725, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster))),
            Instant(Spell(GiftOfLifeScript.GiftOfLifeAura, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModIncreaseHealth)) with
            {
                Duration = new SpellDuration(20_000, 0, 20_000),
            }));
        (Player warrior, _) = kit.AddPlayer(1);
        warrior.MaxHealth = 4000;
        warrior.Health = 4000;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, 23725, SpellCastTargets.ForSelf(), triggered: true));

        SpellAuraHolder holder = Assert.Single(kit.System.GetAuras(warrior), h => h.Spell.Id == GiftOfLifeScript.GiftOfLifeAura);
        Assert.Equal(600, holder.Auras[0]!.Amount);
    }

    [Fact]
    public void ADragonlingTrinket_SummonsOnlyFromItsItem()
    {
        using SpellTestKit kit = Kit(
            Instant(Spell(23074, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster))),
            Instant(Spell(19804, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(20_000, 0, 20_000),
            }));
        (Player engineer, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(engineer, 23074, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(kit.System.HasAura(engineer, 19804));
        Assert.Equal(19804u, EngineeringSummonTrinketScript.SummonFor(23074));
        Assert.Equal(13166u, EngineeringSummonTrinketScript.SummonFor(23133));
    }

    [Theory]
    [InlineData(0, 11921u, false)]
    [InlineData(49, 13322u, false)]
    [InlineData(69, 21179u, false)]
    [InlineData(76, 13323u, false)]
    [InlineData(79, 13323u, true)]
    [InlineData(94, 25189u, false)]
    [InlineData(99, 14642u, true)]
    public void SixDemonBag_FollowsTheVmangosTable(int roll, uint spell, bool onSelf)
        => Assert.Equal((spell, onSelf), SixDemonBagScript.Pick(roll));

    [Theory]
    [InlineData(5, 16566u)]
    [InlineData(15, 13119u)]
    [InlineData(50, 13099u)]
    public void NetOMatic_FollowsTheVmangosTable(int roll, uint spell) => Assert.Equal(spell, NetOMaticScript.Pick(roll));

    [Fact]
    public void EveryGadgetScript_IsDiscovered()
    {
        IEnumerable<uint> ids = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds;
        Assert.Superset(new HashSet<uint> { 23074, 23075, 23076, 23133, 23134, 4050, 23453, 23448, 8344, 13006, 13180, 14537, 17009, 13120, 23725 },
            ids.ToHashSet());
    }
}