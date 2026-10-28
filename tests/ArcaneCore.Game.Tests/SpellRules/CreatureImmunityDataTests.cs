using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// Data-driven creature immunities: the creature_template mechanic and school masks (vmangos Creature::IsImmuneToSpell, Creature.cpp:2438-2480)
/// and the creature_immunities sets (mangos-classic Creature::UpdateImmunitiesSet, Creature.cpp:2462-2481; Unit::IsImmuneToSpell,
/// Unit.cpp:7343-7384; Unit::IsImmuneToSpellEffect, :7385-7418). Template ids are synthetic.
/// </summary>
public sealed class CreatureImmunityDataTests
{
    private const uint Boss = 931_001;
    private const uint FireBolt = 931_101;
    private const uint ShadowBolt = 931_102;
    private const uint StunSpell = 931_103;
    private const uint RootSpell = 931_104;
    private const uint MagicDebuff = 931_105;
    private const uint PoisonDebuff = 931_106;
    private const uint FrostBolt = 931_107;

    private static SpellInfo Hostile(uint id, SpellSchool school, params SpellEffectInfo[] effects) =>
        Spell(id, effects) with
        {
            School = school,
            DamageClass = SpellDamageClass.Magic,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(30_000, 0, 30_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellEffectInfo Damage() => Effect(SpellEffectName.SchoolDamage, 6, SpellImplicitTarget.UnitEnemy);

    private static SpellEffectInfo Aura(AuraType type) => Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, type);

    private static SpellTestKit Kit() => new(
        Hostile(FireBolt, SpellSchool.Fire, Damage()),
        Hostile(ShadowBolt, SpellSchool.Shadow, Damage()),
        Hostile(FrostBolt, SpellSchool.Frost, Damage()),
        Hostile(StunSpell, SpellSchool.Nature, Aura(AuraType.ModStun)) with { Mechanic = (uint)SpellMechanic.Stun },
        Hostile(RootSpell, SpellSchool.Nature, Aura(AuraType.ModRoot)) with { Mechanic = (uint)SpellMechanic.Root },
        Hostile(MagicDebuff, SpellSchool.Arcane, Aura(AuraType.Dummy)) with { Dispel = (uint)DispelType.Magic },
        Hostile(PoisonDebuff, SpellSchool.Nature, Aura(AuraType.Dummy)) with { Dispel = (uint)DispelType.Poison });

    private static CreatureImmunity Row(uint set, CreatureImmunityType type, uint value) => new(Boss, set, type, value);

    private static Creature Make(Action<CreatureTemplateBuilder>? configure, uint maskMechanic, uint maskSchool, params CreatureImmunity[] rows)
    {
        CreatureTemplate template = Template(Boss, configure) with { MechanicImmuneMask = maskMechanic, SchoolImmuneMask = maskSchool };
        CreatureSpawn spawn = Spawn(1, Boss, 0, 0);
        var content = new CreatureContent([template], [spawn], [], [], [], immunities: rows);
        return new Creature(1, template, spawn, content, new Random(1));
    }

    [Fact]
    public void TemplateMasks_MakeAStunSpellImmune_AndFireDamageImmune()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, SpellMechanics.Mask(SpellMechanic.Stun), SpellSchoolMasks.Of(SpellSchool.Fire));

        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(StunSpell)!, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(RootSpell)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToDamage(kit.System, boss, SpellSchoolMasks.Of(SpellSchool.Fire), kit.Store.Get(FireBolt)));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(FireBolt)!, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToDamage(kit.System, boss, SpellSchoolMasks.Of(SpellSchool.Frost), kit.Store.Get(FrostBolt)));
    }

    [Fact]
    public void ACreatureWithNoData_IsImmuneToNothing()
    {
        using SpellTestKit kit = Kit();
        Creature plain = Make(null, 0, 0);

        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, plain, kit.Store.Get(StunSpell)!, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToDamage(kit.System, plain, SpellSchoolMasks.Of(SpellSchool.Fire), kit.Store.Get(FireBolt)));
        Assert.Equal(0u, plain.ImmunitySetId);
    }

    [Fact]
    public void ADispelRow_BlocksSpellsOfThatDispelType_Only()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, 0, 0, Row(0, CreatureImmunityType.Dispel, (uint)DispelType.Magic));

        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(MagicDebuff)!, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(PoisonDebuff)!, castOnSelf: false));
    }

    [Fact]
    public void AStateRow_BlocksTheAuraTheEffectApplies()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, 0, 0, Row(0, CreatureImmunityType.State, (uint)AuraType.ModStun));

        Assert.True(ImmunityRules.IsImmuneToSpellEffect(kit.System, boss, kit.Store.Get(StunSpell)!, 0, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpellEffect(kit.System, boss, kit.Store.Get(RootSpell)!, 0, castOnSelf: false));
    }

    [Fact]
    public void AnEffectRow_BlocksThatEffectId()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, 0, 0, Row(0, CreatureImmunityType.Effect, (uint)SpellEffectName.SchoolDamage));

        Assert.True(ImmunityRules.IsImmuneToSpellEffect(kit.System, boss, kit.Store.Get(FireBolt)!, 0, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpellEffect(kit.System, boss, kit.Store.Get(StunSpell)!, 0, castOnSelf: false));
    }

    [Fact]
    public void MechanicSchoolAndDamageRows_FeedTheSameChecksAsTheTemplateMasks()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, 0, 0,
            Row(0, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Root),
            Row(0, CreatureImmunityType.School, SpellSchoolMasks.Of(SpellSchool.Frost)),
            Row(0, CreatureImmunityType.Damage, SpellSchoolMasks.Of(SpellSchool.Shadow)));

        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(RootSpell)!, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(StunSpell)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(FrostBolt)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToDamage(kit.System, boss, SpellSchoolMasks.Of(SpellSchool.Shadow), kit.Store.Get(ShadowBolt)));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(ShadowBolt)!, castOnSelf: false));
    }

    [Fact]
    public void TemplateMasksAndSetRows_AreUnioned()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, SpellMechanics.Mask(SpellMechanic.Stun), SpellSchoolMasks.Of(SpellSchool.Fire),
            Row(0, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Root),
            Row(0, CreatureImmunityType.School, SpellSchoolMasks.Of(SpellSchool.Frost)));

        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(StunSpell)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(RootSpell)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(FireBolt)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(FrostBolt)!, castOnSelf: false));
    }

    [Fact]
    public void SelfCasts_AreNotBlockedByTheStaticRows()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, 0, 0,
            Row(0, CreatureImmunityType.Dispel, (uint)DispelType.Magic),
            Row(0, CreatureImmunityType.State, (uint)AuraType.ModStun));

        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(MagicDebuff)!, castOnSelf: true));
        Assert.False(ImmunityRules.IsImmuneToSpellEffect(kit.System, boss, kit.Store.Get(StunSpell)!, 0, castOnSelf: true));
    }

    [Fact]
    public void SwitchingTheSet_SwapsWhichRowsApply_AndAnUnknownSetIsIgnored()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, 0, 0,
            Row(0, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Stun),
            Row(1, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Root));
        SpellInfo stun = kit.Store.Get(StunSpell)!;
        SpellInfo root = kit.Store.Get(RootSpell)!;

        Assert.True(boss.UpdateImmunitySet(0));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, stun, castOnSelf: false));
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, root, castOnSelf: false));

        Assert.True(boss.UpdateImmunitySet(1));
        Assert.Equal(1u, boss.ImmunitySetId);
        Assert.False(ImmunityRules.IsImmuneToSpell(kit.System, boss, stun, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, root, castOnSelf: false));

        Assert.False(boss.UpdateImmunitySet(7)); // mangos-classic: a set the creature has no rows for changes nothing
        Assert.Equal(1u, boss.ImmunitySetId);
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, root, castOnSelf: false));
    }

    [Fact]
    public void TheTemplateMask_StaysWhenTheSetSwitches()
    {
        using SpellTestKit kit = Kit();
        Creature boss = Make(null, SpellMechanics.Mask(SpellMechanic.Stun), 0, Row(1, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Root));

        Assert.True(boss.UpdateImmunitySet(1));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(StunSpell)!, castOnSelf: false));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, boss, kit.Store.Get(RootSpell)!, castOnSelf: false));
    }

    [Fact]
    public void EventAiAction63_SwitchesTheCreatureFromSetZeroToSetOne()
    {
        CreatureTemplate template = Template(Boss, t => t.AIName = CreatureAiFactory.EventAIName);
        CreatureImmunity[] rows =
        [
            Row(0, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Stun),
            Row(1, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Root),
        ];
        var content = new CreatureContent([template], [Spawn(1, Boss, 0, 0)], [], [], [], immunities: rows);
        (WorldRuntime world, _, CreatureMapSystem system) = CreatureAiTestKit.CreateAiSystem(content);
        using (world)
        {
            CreatureAiTestKit.AddPlayer(world, 1, 40, 0);
            Creature boss = system.Creatures.Single();
            var ai = (CreatureEventAI)boss.AI!;
            Assert.Equal(0u, boss.ImmunitySetId);

            Assert.NotNull(EventAiRegistry.Default.FindAction(63));
            Assert.True(new SetImmunitySetAction().Execute(ai.Engine.Context, new CreatureAiAction(63, 1, 0, 0), default));

            Assert.Equal(1u, boss.ImmunitySetId);
        }
    }

    [Fact]
    public void AnEntryChange_ReturnsTheCreatureToSetZero()
    {
        const uint other = Boss + 1;
        CreatureTemplate template = Template(Boss, null);
        CreatureTemplate changed = Template(other, null);
        CreatureImmunity[] rows =
        [
            Row(0, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Stun),
            Row(1, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Root),
            new(other, 1, CreatureImmunityType.Mechanic, (uint)SpellMechanic.Fear),
        ];
        var content = new CreatureContent([template, changed], [Spawn(1, Boss, 0, 0)], [], [], [], immunities: rows);
        (WorldRuntime world, _, CreatureMapSystem system) = CreatureAiTestKit.CreateAiSystem(content);
        using (world)
        {
            CreatureAiTestKit.AddPlayer(world, 1, 40, 0);
            Creature boss = system.Creatures.Single();
            Assert.True(boss.UpdateImmunitySet(1));

            Assert.True(system.UpdateEntry(boss, other));

            Assert.Equal(0u, boss.ImmunitySetId); // cmangos Creature::UpdateEntry ends with UpdateImmunitiesSet(0)
        }
    }

    [Fact]
    public void ATwinEmperorSchoolImmunity_ComposesOverTheDataProvider()
    {
        using var kit = new SpellTestKit(
            Hostile(931_201, SpellSchool.Frost, Damage()),
            Hostile(931_202, SpellSchool.Normal, Damage()),
            Hostile(StunSpell, SpellSchool.Nature, Aura(AuraType.ModStun)) with { Mechanic = (uint)SpellMechanic.Stun });
        Map map = kit.World.GetMap(531);
        map.AddUpdater(new TempleOfAhnQirajInstance(map));
        CreatureTemplate veklor = Template(15276) with { MechanicImmuneMask = SpellMechanics.Mask(SpellMechanic.Stun) };
        var content = new CreatureContent([veklor], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1), aiServices: new CreatureAiServices());
        map.AddUpdater(creatures);
        Creature lor = creatures.SpawnTemporary(veklor, 5, 0, -104, 0);

        // The script immunity (Vek'lor resists physical damage) and the data immunity (stun, from the template mask) both hold.
        Assert.True(ImmunityRules.IsImmuneToDamage(kit.System, lor, SpellSchoolMasks.Of(SpellSchool.Normal), kit.Store.Get(931_202)));
        Assert.False(ImmunityRules.IsImmuneToDamage(kit.System, lor, SpellSchoolMasks.Of(SpellSchool.Frost), kit.Store.Get(931_201)));
        Assert.True(ImmunityRules.IsImmuneToSpell(kit.System, lor, kit.Store.Get(StunSpell)!, castOnSelf: false));
    }
}
