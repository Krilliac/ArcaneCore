using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// The combat-side spell mods: THREAT before the MOD_THREAT auras (vmangos ThreatManager.cpp:43-44), RESIST_MISS_CHANCE on the
/// melee spell table as the hit chance bonus (SpellCaster.cpp:381-388), and the crit chance / crit damage bonus the existing
/// rules already read through the seam (Unit.cpp:5311-5314, SpellCaster.cpp:958-980), now live.
/// </summary>
public sealed class ThreatCritModTests
{
    private const uint ThreatHoly = 944001;
    private const uint HolyThreatAura = 944002;
    private const uint ThreatFlat = 944003;
    private const uint MeleeHit = 944004;
    private const uint HitMod = 944005;
    private const uint MagicHit = 944006;
    private const uint CritChanceMod = 944007;
    private const uint CritBonusPct = 944008;

    private static SpellTestKit Kit() => new(
        InFamily(Spell(ThreatHoly, Effect(SpellEffectName.Threat, 150, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Holy }),
        Spell(HolyThreatAura, Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModThreat, misc: 1 << (int)SpellSchool.Holy)) with { Duration = new SpellDuration(-1, 0, -1) },
        Flat(ThreatFlat, SpellModOp.Threat, 30),
        InFamily(Spell(MeleeHit, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            DamageClass = SpellDamageClass.Melee,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        }),
        Flat(HitMod, SpellModOp.ResistMissChance, 100),
        InFamily(Spell(MagicHit, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Fire,
            DamageClass = SpellDamageClass.Magic,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        }),
        Flat(CritChanceMod, SpellModOp.CriticalChance, 3),
        Pct(CritBonusPct, SpellModOp.CritDamageBonus, 100));

    private sealed class FixedResolver(Unit unit) : ISpellUnitResolver
    {
        public Unit? Find(Unit source, ObjectGuid guid) => guid == unit.Guid ? unit : null;
    }

    private static Creature MakeCreature(SpellTestKit kit)
    {
        CreatureTemplate template = CreatureTestSupport.Template(CreatureTestSupport.WolfEntry, t =>
        {
            t.MinLevelHealth = 100;
            t.MaxLevelHealth = 100;
        });
        CreatureSpawn spawn = CreatureTestSupport.Spawn(77, CreatureTestSupport.WolfEntry, 3, 0);
        var system = new CreatureMapSystem(kit.World.GetMap(0), CreatureTestSupport.Content([template], [spawn]), null, random: new Random(1));
        kit.World.GetMap(0).AddUpdater(system);
        kit.World.RunTick(50);
        Creature creature = Assert.Single(system.Creatures);
        kit.System.Units = new FixedResolver(creature);
        return creature;
    }

    [Fact]
    public void Threat_ModRunsBeforeTheModThreatAura()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Creature wolf = MakeCreature(kit);
        kit.System.CastSpell(caster, HolyThreatAura, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CastSpell(caster, ThreatHoly, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Equal(75f, wolf.Combat.Threat.GetThreat(caster));            // 150 * 0.5

        wolf.Combat.Threat.Clear();
        kit.System.LearnSpell(caster, ThreatFlat);
        kit.System.CastSpell(caster, ThreatHoly, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);

        // (150 + 30) * 0.5 = 90: the mod is on the raw amount, so the aura halves the bonus too (after it would be 105).
        Assert.Equal(90f, wolf.Combat.Threat.GetThreat(caster));
    }

    private static int Misses(SpellTestKit kit, Player caster, Player target)
    {
        var rules = new VanillaSpellCombatRules();
        SpellInfo spell = kit.Store.Get(MeleeHit)!;
        int misses = 0;
        for (int i = 0; i < 4000; i++)
        {
            if (rules.RollHit(kit.System, caster, target, spell) == SpellMissInfo.Miss)
            {
                misses++;
            }
        }

        return misses;
    }

    [Fact]
    public void MeleeSpellTable_ResistMissChanceModIsAHitBonus()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        kit.System.Relations = new FakeRelations { Hostile = { target.Guid } };

        Assert.True(Misses(kit, caster, target) > 0);   // the 5% base miss, seeded

        kit.System.LearnSpell(caster, HitMod);
        Assert.Equal(0, Misses(kit, caster, target));
    }

    [Fact]
    public void CritChance_FlatModAddsToTheChance()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        kit.System.Relations = new FakeRelations { Hostile = { target.Guid } };
        var rules = new VanillaSpellCombatRules();
        SpellInfo bolt = kit.Store.Get(MagicHit)!;
        float before = rules.CritChance(kit.System, caster, target, bolt);

        kit.System.LearnSpell(caster, CritChanceMod);

        Assert.Equal(before + 3f, rules.CritChance(kit.System, caster, target, bolt), 3);
    }

    [Fact]
    public void CritDamageBonus_PctModScalesTheBonus_ForSpellAndMeleeClass()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var rules = new VanillaSpellCombatRules();
        SpellInfo magic = kit.Store.Get(MagicHit)!;
        SpellInfo melee = kit.Store.Get(MeleeHit)!;
        Assert.Equal(150u, rules.CriticalDamage(kit.System, caster, target, magic, 100));
        Assert.Equal(200u, rules.CriticalDamage(kit.System, caster, target, melee, 100));

        kit.System.LearnSpell(caster, CritBonusPct);

        Assert.Equal(200u, rules.CriticalDamage(kit.System, caster, target, magic, 100));   // bonus 50 -> 100
        Assert.Equal(300u, rules.CriticalDamage(kit.System, caster, target, melee, 100));   // bonus 100 -> 200
    }
}
