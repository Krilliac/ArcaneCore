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

    private static SpellInfo Marker(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitEnemy) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, target, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 40),
        });

    private static SpellInfo AtEnemy(uint id) => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 40),
    });

    [Fact]
    public void StingerChargePrimer_ChargesCatalystTargetsWithTheHeavyCharge()
    {
        using SpellTestKit kit = Kit(AtEnemy(24150), Marker(25190), Marker(25191), Marker(25187, SpellImplicitTarget.UnitCaster));
        (Player stinger, _) = kit.AddPlayer(1);
        (Player plain, _) = kit.AddPlayer(2, 5, 0);
        (Player catalyst, _) = kit.AddPlayer(3, -5, 0);
        kit.System.Relations = new FakeRelations { Hostile = { plain.Guid, catalyst.Guid } };
        Assert.True(kit.System.AddAura(catalyst, 25187));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(stinger, 24150, SpellCastTargets.ForUnit(plain.Guid), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(stinger, 24150, SpellCastTargets.ForUnit(catalyst.Guid), triggered: true));

        Assert.True(kit.System.HasAura(plain, 25190));
        Assert.False(kit.System.HasAura(plain, 25191));
        Assert.True(kit.System.HasAura(catalyst, 25191));
    }

    [Fact]
    public void DespawnIceBlock_FreesThePlayerFromIcebolt()
    {
        using SpellTestKit kit = Kit(AtEnemy(30132), Marker(31800, SpellImplicitTarget.UnitCaster), Marker(28522, SpellImplicitTarget.UnitCaster));
        (Player sapphiron, _) = kit.AddPlayer(1);
        (Player frozen, _) = kit.AddPlayer(2, 5, 0);
        kit.System.Relations = new FakeRelations { Hostile = { frozen.Guid } };
        Assert.True(kit.System.AddAura(frozen, 31800));
        Assert.True(kit.System.AddAura(frozen, 28522));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(sapphiron, 30132, SpellCastTargets.ForUnit(frozen.Guid), triggered: true));

        Assert.False(kit.System.HasAura(frozen, 31800));
        Assert.False(kit.System.HasAura(frozen, 28522));
    }

    [Fact]
    public void AuraOfFrost_ChillsMarkedPlayers_AndEndsStealth()
    {
        using SpellTestKit kit = Kit(AtEnemy(23185), Marker(23182, SpellImplicitTarget.UnitCaster), Marker(23186, SpellImplicitTarget.UnitCaster),
            Instant(Spell(1784, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStealth)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
            }));
        (Player azuregos, _) = kit.AddPlayer(1);
        (Player rogue, _) = kit.AddPlayer(2, 5, 0);
        kit.System.Relations = new FakeRelations { Hostile = { rogue.Guid } };
        Assert.True(kit.System.AddAura(rogue, 23182));
        Assert.True(kit.System.AddAura(rogue, 1784));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(azuregos, 23185, SpellCastTargets.ForUnit(rogue.Guid), triggered: true));

        Assert.True(kit.System.HasAura(rogue, 23186));
        Assert.False(kit.System.HasAura(rogue, 1784));
    }

    [Fact]
    public void EveryGadgetScript_IsDiscovered()
    {
        IEnumerable<uint> ids = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds;
        Assert.Superset(new HashSet<uint> { 23074, 23075, 23076, 23133, 23134, 4050, 23453, 23448, 8344, 13006, 13180, 14537, 17009, 13120, 23725, 24150, 28352, 26626, 28006, 26399, 25754, 26559, 25676, 26457, 25671, 25755, 29182, 29188, 30132, 27687, 27695, 24320, 24590, 26465, 26656, 8897, 23185, 25044 },
            ids.ToHashSet());
    }
}