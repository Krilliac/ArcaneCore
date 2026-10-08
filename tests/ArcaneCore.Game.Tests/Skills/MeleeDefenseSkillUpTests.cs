using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Skills;
using Xunit;

namespace ArcaneCore.Game.Tests.Skills;

/// <summary>
/// The defense skill-up of a white swing (vmangos Unit::ProcSkillsAndReactives via SpellCaster::ProcDamageAndSpell, SpellCaster.cpp:271-283,
/// and Player::UpdateCombatSkills, Player.cpp:5341-5380). The roll runs from ProcDamageAndSpell, which Unit::AttackerStateUpdate calls before
/// DealMeleeDamage (Unit.cpp:2260-2275), so the killing swing still rolls; the attacker's level is GetLevelForTarget (a world boss counts as
/// the player's level + 3) capped at the player's level + 5; and a player-controlled attacker (a player or a player's pet) gives no skill.
/// </summary>
public sealed class MeleeDefenseSkillUpTests
{
    private sealed class Rig : IDisposable
    {
        public Rig(byte playerLevel = 10, byte creatureLevel = 10)
        {
            (World, Map, _, _) = CombatTestKit.CreateWorld();
            Player = CombatTestKit.AddPlayer(World, 1, 0, 0, new FakeSession(1), level: playerLevel);
            SkillRandom = new ScriptedSkillRandom();
            Skills = new PlayerSkills(Player, SkillTestKit.Catalog(), new SkillOptions(), new FakeSkillSpellHost(), SkillRandom);
            Player.AttachSkills(Skills);
            Skills.Set(SkillIds.Defense, 20, 50);
            Creature = new CombatTestUnit(level: creatureLevel);
            Creature.Spawn(Map, 1, 0);
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public Player Player { get; }

        public PlayerSkills Skills { get; }

        public ScriptedSkillRandom SkillRandom { get; }

        public CombatTestUnit Creature { get; }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void TheKillingSwing_StillRollsTheVictimsDefense()
    {
        using var rig = new Rig();
        rig.Player.Health = 1;
        rig.SkillRandom.Floats.Enqueue(0f); // the defense roll wins

        rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);

        Assert.False(rig.Player.IsAlive);
        Assert.Equal((ushort)21, rig.Skills.GetValuePure(SkillIds.Defense));
    }

    [Fact]
    public void AWorldBoss_CountsAsThePlayersLevelPlusThree_ForTheDefenseChance()
    {
        // Player level 10 (gray level 4), defense 20 of 50. As level 13 (10 + 3) the chance is 3 x (13 - 4) x 30 / 10 = 81;
        // the raw level 63, capped at 10 + 5, would give 3 x (15 - 4) x 30 / 10 = 99.
        using var rig = new Rig(playerLevel: 10, creatureLevel: 63);
        rig.Creature.IsWorldBoss = true;
        rig.SkillRandom.Floats.Enqueue(90f);

        rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);

        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Defense));
        Assert.Empty(rig.SkillRandom.Floats); // the roll happened and lost

        rig.SkillRandom.Floats.Enqueue(80f);
        rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);
        Assert.Equal((ushort)21, rig.Skills.GetValuePure(SkillIds.Defense));
    }

    [Fact]
    public void AWorldBoss_CountsAsThePlayersLevelPlusTheConfiguredDifference_ForTheDefenseChance()
    {
        // vmangos GetLevelForTarget reads CONFIG_UINT32_WORLD_BOSS_LEVEL_DIFF (World.cpp:744), the SpellRules:WorldBossLevelDiff setting here.
        // Configured 1: as level 11 the chance is 3 x (11 - 4) x 30 / 10 = 63, so a roll of 70 loses (with the default 3 it is 81 and wins).
        using var rig = new Rig(playerLevel: 10, creatureLevel: 63);
        CombatEnvironment.GetOrCreate(rig.World, () => new CombatOptions()).WorldBossLevelDiff = 1;
        rig.Creature.IsWorldBoss = true;
        rig.SkillRandom.Floats.Enqueue(70f);

        rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);

        Assert.Empty(rig.SkillRandom.Floats);
        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Defense));

        rig.SkillRandom.Floats.Enqueue(60f);
        rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);
        Assert.Equal((ushort)21, rig.Skills.GetValuePure(SkillIds.Defense));
    }

    [Fact]
    public void APlayersPet_GivesNoDefenseSkill()
    {
        using var rig = new Rig();
        Player owner = CombatTestKit.AddPlayer(rig.World, 2, 2, 0, new FakeSession(2), Race.Orc, level: 10);
        rig.Creature.SetOwnerGuid(owner.Guid);
        rig.SkillRandom.Floats.Enqueue(0f);

        rig.Map.Combat.AttackerStateUpdate(rig.Creature, rig.Player, WeaponAttackType.BaseAttack);

        Assert.Equal((ushort)20, rig.Skills.GetValuePure(SkillIds.Defense));
        Assert.Single(rig.SkillRandom.Floats); // never rolled
    }
}
