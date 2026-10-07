using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// White swing threat through the threat formula (MapCombat.AddDamageThreat → ThreatCalc): a melee swing is physical, so the attacker's MOD_THREAT
/// multiplier of the physical school applies once; spell damage uses its own school.
/// </summary>
public sealed class WhiteThreatModifierTests
{
    [Fact]
    public void NormalWhitePhysicalDamage_UsesTheModifierOnceWithPhysicalSchool()
    {
        (WorldRuntime world, Map map, Player attacker, Creature victim) = Scene();
        using (world)
        {
            var modifiers = new SchoolModifiers(attacker, 2f);
            map.Combat.ThreatModifiers = modifiers;

            MeleeDamageInfo info = Assert.IsType<MeleeDamageInfo>(map.Combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack));

            Assert.Equal([0], modifiers.Schools);
            Assert.Equal(info.TotalDamage * 2f, victim.Combat.Threat.Entries.Single(e => ReferenceEquals(e.Target, attacker)).Threat);
        }
    }

    [Fact]
    public void WhiteDamageWithoutModifierRetainsRawThreat()
    {
        (WorldRuntime world, Map map, Player attacker, Creature victim) = Scene();
        using (world)
        {
            MeleeDamageInfo info = Assert.IsType<MeleeDamageInfo>(map.Combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack));
            Assert.Equal(info.TotalDamage, victim.Combat.Threat.Entries.Single(e => ReferenceEquals(e.Target, attacker)).Threat);
        }
    }

    [Fact]
    public void SpellDamageUsesItsOwnSchoolNotThePhysicalWhitePath()
    {
        (WorldRuntime world, Map map, Player attacker, Creature victim) = Scene();
        using (world)
        {
            var modifiers = new SchoolModifiers(attacker, 3f);
            map.Combat.ThreatModifiers = modifiers;
            SpellInfo spell = SpellTestKit.Spell(991001, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
                with { School = SpellSchool.Fire };
            map.Combat.DealDamage(attacker, victim, 10, direct: true, meleeDamage: false, threatSpell: spell);
            Assert.Equal([(int)SpellSchool.Fire], modifiers.Schools);
            Assert.Equal(30f, victim.Combat.Threat.Entries.Single(e => ReferenceEquals(e.Target, attacker)).Threat);
        }
    }

    /// <summary>MOD_THREAT for one attacker: <paramref name="multiplier"/> for every school, recording the schools asked for.</summary>
    private sealed class SchoolModifiers(Unit attacker, float multiplier) : IThreatModifierSource
    {
        public List<int> Schools { get; } = [];

        public float ApplySpellMod(Unit hated, SpellInfo spell, float threat) => threat;

        public float CriticalThreatMultiplier(Unit hated, uint schoolMask) => 1f;

        public float TotalThreatMultiplier(Unit hated, int school)
        {
            Assert.Same(attacker, hated);
            Schools.Add(school);
            return multiplier;
        }
    }

    private static (WorldRuntime World, Map Map, Player Attacker, Creature Victim) Scene()
    {
        (WorldRuntime world, Map map, ScriptedRandom _, TestCombatHooks _) = CombatTestKit.CreateWorld();
        var session = new FakeSession(1);
        Player attacker = CombatTestKit.AddPlayer(world, 1, 0, 0, session);
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        var template = new CreatureTemplate { Entry = 991000, Name = "Threat victim", MinLevel = 1, MaxLevel = 1,
            MinLevelHealth = 1000, MaxLevelHealth = 1000, DisplayIds = [1], Faction = 35 };
        var victim = new Creature(991000, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        victim.Relocate(1, 0, 83.5f, 0, 0);
        victim.MapId = 0;
        map.AddObject(victim);
        map.Combat.Track(victim);
        return (world, map, attacker, victim);
    }
}
