using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.ClassScripts;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// The common class DUMMY / SCRIPT_EFFECT scripts (vmangos SpellEffects.cpp:742-850 and :1440-1458, scripts/spells/spell_warrior.cpp, spell_mage.cpp,
/// spell_hunter.cpp, spell_warlock.cpp). Each spell keeps its build 5875 id, effect kind and the fields the script reads.
/// </summary>
public sealed class ClassDummyScriptTests : IDisposable
{
    private const uint LastStand = 12975;
    private const uint Execute = 5308;
    private const uint Bloodrage = 2687;
    private const uint DeepWounds = 12162;
    private const uint Bloodthirst = 23881;
    private const uint Berserking = 20554;
    private const uint Preparation = 14185;
    private const uint Sprint = 2983;
    private const uint Kick = 1766;
    private const uint ColdSnap = 12472;
    private const uint FrostNova = 122;
    private const uint FireBlast = 2136;
    private const uint Readiness = ReadinessScript.Readiness;
    private const uint RapidFire = 3045;
    private const uint Conflagrate = 17962;
    private const uint Immolate = 348;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo AtEnemy(SpellInfo spell) => Instant(spell with { RangeIndex = 4, Range = new SpellRange(0, 30) });

    private static SpellInfo Cooldown(uint id, uint family, SpellSchool school, uint recovery) => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0)) with
    {
        SpellFamilyName = family, School = school, RecoveryTime = recovery,
    });

    private readonly SpellTestKit _kit;
    private readonly Player _player;
    private readonly Player _enemy;

    public ClassDummyScriptTests()
    {
        _kit = new SpellTestKit(
            Instant(Spell(LastStand, Effect(SpellEffectName.Dummy, 0))),
            Instant(Spell(LastStandScript.LastStandHealth, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModIncreaseHealth)) with
            {
                Duration = new SpellDuration(20_000, 0, 20_000),
            }),
            AtEnemy(Spell(Execute, Effect(SpellEffectName.Dummy, 125, SpellImplicitTarget.UnitEnemy) with { DamageMultiplier = 0.3f }) with { SpellFamilyName = 4 }),
            AtEnemy(Spell(ExecuteDummyScript.ExecuteDamage, Effect(SpellEffectName.SchoolDamage, 0, SpellImplicitTarget.UnitEnemy)) with { SpellFamilyName = 4 }),
            Instant(Spell(Bloodrage, Effect(SpellEffectName.Energize, 100, misc: (int)PowerType.Rage)) with { SpellFamilyName = 4 }),
            AtEnemy(Spell(DeepWounds, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy))),
            AtEnemy(Spell(DeepWoundsScript.DeepWound, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(12_000, 0, 12_000),
            }),
            AtEnemy(Spell(Bloodthirst, Effect(SpellEffectName.SchoolDamage, 45, SpellImplicitTarget.UnitEnemy)) with { SpellFamilyName = 4 }),
            Instant(Spell(Berserking, Effect(SpellEffectName.Dummy, 0)) with { SpellIconId = 1661 }),
            Instant(Spell(BerserkingScript.BerserkingHaste,
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModMeleeHaste),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModRangedHaste),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModCastingSpeedNotStack)) with
            {
                Duration = new SpellDuration(10_000, 0, 10_000),
            }),
            Instant(Spell(Preparation, Effect(SpellEffectName.Dummy, 0))),
            Cooldown(Sprint, 8, SpellSchool.Normal, 300_000),
            Cooldown(Kick, 8, SpellSchool.Normal, 10_000),
            Instant(Spell(ColdSnap, Effect(SpellEffectName.Dummy, 0)) with { SpellFamilyName = 3 }),
            Cooldown(FrostNova, 3, SpellSchool.Frost, 25_000),
            Cooldown(FireBlast, 3, SpellSchool.Fire, 8_000),
            Instant(Spell(Readiness, Effect(SpellEffectName.Dummy, 0)) with { SpellFamilyName = 9, RecoveryTime = 300_000 }),
            Cooldown(RapidFire, 9, SpellSchool.Normal, 300_000),
            AtEnemy(Spell(Immolate, Effect(SpellEffectName.ApplyAura, 8, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                School = SpellSchool.Fire, SpellFamilyName = 5, SpellFamilyFlags = 0x4, Duration = new SpellDuration(15_000, 0, 15_000),
            }),
            AtEnemy(Spell(Conflagrate, Effect(SpellEffectName.SchoolDamage, 240, SpellImplicitTarget.UnitEnemy)) with
            {
                School = SpellSchool.Fire, SpellFamilyName = 5, SpellFamilyFlags = 0x200,
            }));
        (_player, _) = _kit.AddPlayer(1);
        (_enemy, _) = _kit.AddPlayer(2, 3, 0);
        _kit.System.Relations = new FakeRelations { Hostile = { _enemy.Guid } };
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        foreach (Player p in new[] { _player, _enemy })
        {
            p.MaxHealth = 10_000;
            p.Health = 5_000;
        }
    }

    public void Dispose() => _kit.Dispose();

    private SpellCastResult Cast(uint spell, Unit? target = null)
        => _kit.System.CastSpell(_player, spell, target is null ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid), triggered: true);

    private SpellAuraHolder? Holder(Unit unit, uint spell) => _kit.System.GetAuras(unit).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == spell);

    [Fact]
    public void LastStand_GivesThirtyPercentOfMaximumHealth()
    {
        Cast(LastStand);

        Assert.Equal(3_000, Holder(_player, LastStandScript.LastStandHealth)!.Auras[0]!.Amount);
    }

    [Fact]
    public void Execute_DealsBasePlusRageTimesTheMultiplier_AndTakesAllTheRage()
    {
        MapCombat.SetPower(_player, PowerType.Rage, 300); // 30 rage, stored in tenths
        uint before = _enemy.Health;

        Cast(Execute, _enemy);

        Assert.Equal(before - (125u + 90u), _enemy.Health); // 300 * 0.3 = 90
        Assert.Equal(0u, MapCombat.GetPower(_player, PowerType.Rage));
    }

    [Fact]
    public void Bloodrage_PutsTheWarriorInCombat()
    {
        Assert.False(_player.Combat.IsInCombat);

        Cast(Bloodrage);

        Assert.True(_player.Combat.IsInCombat);
    }

    [Fact]
    public void DeepWounds_TicksAQuarterOfTheWeaponsAverageShare()
    {
        _player.SetFloat(UpdateFields.UnitFieldMindamage, 100);
        _player.SetFloat(UpdateFields.UnitFieldMaxdamage, 200);

        Cast(DeepWounds, _enemy);

        Assert.Equal(7, Holder(_enemy, DeepWoundsScript.DeepWound)!.Auras[0]!.Amount); // 150 * 0.2 / 4 = 7.5
    }

    [Fact]
    public void Bloodthirst_DealsItsValueInPercentOfAttackPower()
    {
        _player.SetInt32(UpdateFields.UnitFieldAttackPower, 1000);
        uint before = _enemy.Health;

        Cast(Bloodthirst, _enemy);

        Assert.Equal(before - 450u, _enemy.Health);
    }

    [Theory]
    [InlineData(100u, 10)]
    [InlineData(70u, 20)]
    [InlineData(41u, 29)]
    [InlineData(40u, 30)]
    [InlineData(5u, 30)]
    public void Berserking_HasteFollowsMissingHealth(uint healthPct, int expected)
        => Assert.Equal(expected, BerserkingScript.MeleeMod(healthPct));

    [Fact]
    public void Berserking_CastsTheHasteWithTheComputedValue_AndSetsTheAuraState()
    {
        _player.Health = 7_000;

        Cast(Berserking);

        SpellAuraHolder haste = Holder(_player, BerserkingScript.BerserkingHaste)!;
        Assert.All(haste.Auras.OfType<SpellAura>(), a => Assert.Equal(20, a.Amount));
        Assert.NotEqual(0u, _player.GetUInt32(UpdateFields.UnitFieldAurastate) & (1u << ((int)AuraState.Berserking - 1)));
    }

    private bool OnCooldown(uint spell) => _kit.System.GetActiveCooldowns(_player).Any(c => c.SpellId == spell);

    [Fact]
    public void Preparation_ColdSnap_Readiness_FinishTheirClassesCooldowns()
    {
        foreach (uint spell in new[] { Sprint, Kick, FrostNova, FireBlast, RapidFire })
        {
            _kit.System.CastSpell(_player, spell, SpellCastTargets.ForSelf(), triggered: false);
            Assert.True(OnCooldown(spell), $"{spell} has no cooldown");
        }

        Cast(Preparation);
        Assert.False(OnCooldown(Sprint));
        Assert.False(OnCooldown(Kick));
        Assert.True(OnCooldown(FrostNova));

        Cast(ColdSnap);
        Assert.False(OnCooldown(FrostNova));
        Assert.True(OnCooldown(FireBlast)); // fire, not frost

        _kit.System.CastSpell(_player, Readiness, SpellCastTargets.ForSelf(), triggered: false);
        Assert.False(OnCooldown(RapidFire));
        Assert.True(OnCooldown(Readiness)); // Readiness keeps its own
    }

    [Fact]
    public void Conflagrate_NeedsTheCastersImmolate_AndConsumesIt()
    {
        Assert.Equal(SpellCastResult.TargetAurastate, Cast(Conflagrate, _enemy));

        Cast(Immolate, _enemy);
        uint before = _enemy.Health;
        Assert.Equal(SpellCastResult.CastOk, Cast(Conflagrate, _enemy));

        Assert.Equal(before - 240u, _enemy.Health);
        Assert.Null(Holder(_enemy, Immolate));
    }
}
