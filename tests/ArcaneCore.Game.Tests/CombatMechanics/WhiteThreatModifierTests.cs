using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

public sealed class WhiteThreatModifierTests
{
    [Fact]
    public void NormalWhitePhysicalDamage_UsesTheModifierOnceWithPhysicalSchool()
    {
        (WorldRuntime world, Map map, Player attacker, Creature victim) = Scene();
        using (world)
        {
            int calls = 0;
            CombatEnvironment.Register(world, new CombatEnvironment(new CombatOptions(), threatModifier: (unit, school, threat) =>
            {
                calls++;
                Assert.Same(attacker, unit);
                Assert.Equal(1u, school);
                return threat * 2;
            }));

            MeleeDamageInfo info = Assert.IsType<MeleeDamageInfo>(map.Combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack));

            Assert.Equal(1, calls);
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
    public void PreScaledSpellDamageDoesNotEnterTheWhiteModifierPath()
    {
        (WorldRuntime world, Map map, Player attacker, Creature victim) = Scene();
        using (world)
        {
            int calls = 0;
            CombatEnvironment.Register(world, new CombatEnvironment(new CombatOptions(), threatModifier: (_, _, _) =>
            {
                calls++;
                return 999;
            }));
            SpellInfo spell = SpellTestKit.Spell(991001, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy));
            map.Combat.DealDamage(attacker, victim, 10, direct: true, meleeDamage: false, spell: spell, spellThreat: 30);
            Assert.Equal(0, calls);
            Assert.Equal(30f, victim.Combat.Threat.Entries.Single(e => ReferenceEquals(e.Target, attacker)).Threat);
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
