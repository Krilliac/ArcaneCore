using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// vmangos removes the holders a new holder replaces before it adds it (Unit::AddSpellAuraHolder, Unit.cpp:3216-3224 calls
/// RemoveNoStackAurasDueToAuraHolder, which removes them at :3575-3576) and checks the 16 debuff limit only after that (:3282-3305). So at the
/// cap a sting over the same caster's sting takes the old sting's place and every other debuff stays.
/// </summary>
public sealed class SpellSpecificLoserOrderTests
{
    private const uint FirstDot = 949_401; // 949_401 .. 949_415: plain DoTs
    private const uint SerpentSting = 1978;
    private const uint ViperSting = 3034;
    private const uint FamilyHunter = 9;
    private const uint DispelPoison = 4;

    private static SpellInfo Dot(uint id) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellInfo Sting(uint id, string name) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
        {
            Name = name,
            SpellFamilyName = FamilyHunter,
            Dispel = DispelPoison,
            Attributes = SpellAttributes.AuraIsDebuff,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            Duration = new SpellDuration(15_000, 0, 15_000),
        };

    [Fact]
    public void AStingOverTheSameCastersSting_AtTheDebuffCap_KeepsEveryOtherDebuff()
    {
        var spells = new List<SpellInfo> { Sting(SerpentSting, "Serpent Sting"), Sting(ViperSting, "Viper Sting") };
        for (uint i = 0; i < 15; i++)
        {
            spells.Add(Dot(FirstDot + i));
        }

        using var kit = new SpellTestKit([.. spells]);
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        victim.MaxHealth = 1_000_000;
        victim.Health = 1_000_000;
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        for (uint i = 0; i < 15; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, FirstDot + i, SpellCastTargets.ForUnit(victim.Guid), triggered: true));
        }

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, SerpentSting, SpellCastTargets.ForUnit(victim.Guid), triggered: true));
        Assert.Equal(16, kit.System.GetAuras(victim).Count(h => !h.IsRemoved && !h.IsPositive));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, ViperSting, SpellCastTargets.ForUnit(victim.Guid), triggered: true));

        Assert.True(kit.System.HasAura(victim, ViperSting));
        Assert.False(kit.System.HasAura(victim, SerpentSting));
        for (uint i = 0; i < 15; i++)
        {
            Assert.True(kit.System.HasAura(victim, FirstDot + i), $"DoT {FirstDot + i} was evicted");
        }

        Assert.Equal(16, kit.System.GetAuras(victim).Count(h => !h.IsRemoved && !h.IsPositive));
    }
}
