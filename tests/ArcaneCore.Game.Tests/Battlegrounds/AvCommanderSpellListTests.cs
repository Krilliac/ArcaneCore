using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;
using static ArcaneCore.Game.Tests.Battlegrounds.AvScriptRig;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The Alterac Valley commanders run the spell lists of <c>sql/content/world/&lt;provider&gt;/0006_creature_spell_lists_av.sql</c>, list id entry * 100 + 1: their EventAI rows
/// are replaced by <see cref="AvEventAI"/>, which names the list because the classic-db templates do not. It also runs Ichman's Greater Invisibility rows, which no list carries.
/// </summary>
public sealed class AvCommanderSpellListTests
{
    private const uint ListSpell = 930_201;

    private static CreatureSpellListCatalog ListOf(uint entry) => new([
        new CreatureSpellList(
            (entry * 100) + 1,
            "commander",
            0,
            0,
            [new CreatureSpellListEntry(1, ListSpell, CreatureSpellListFlags.None, CreatureSpellListTargets.SelfTarget, 0, 100, 0, 0, 0, 60_000, 60_000)]),
    ]);

    [Theory]
    [InlineData(NpcWingCommanderGuse)]
    [InlineData(NpcWingCommanderJeztor)]
    [InlineData(NpcWingCommanderMulverick)]
    [InlineData(NpcWingCommanderIchman)]
    [InlineData(NpcWingCommanderSlidore)]
    [InlineData(NpcWingCommanderVipore)]
    [InlineData(NpcRamRiderCommander)]
    public void ACommander_BindsTheListOfHisEntry(uint entry)
    {
        using AvScriptRig rig = Create([AvNpc(entry)], [Spawn(1, entry, -1338.6f, -328.16f, Z)], -1335, -325, spellLists: ListOf(entry));
        Creature commander = rig.Single(entry);

        Assert.IsType<AvEventAI>(commander.AI);
        Assert.Equal((entry * 100) + 1, commander.SpellLists?.List?.Id);
    }

    [Fact]
    public void AnotherNpcOfTheSameScript_RunsNoList()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcPrimalistThurloga)], [Spawn(1, NpcPrimalistThurloga, -1338.6f, -328.16f, Z)], -1335, -325, spellLists: ListOf(NpcPrimalistThurloga));
        Creature thurloga = rig.Single(NpcPrimalistThurloga);

        Assert.IsType<AvEventAI>(thurloga.AI);
        Assert.False(thurloga.AI!.RunsSpellList);
        Assert.Null(thurloga.SpellLists);
    }

    [Fact]
    public void ACommanderInAFight_CastsHisList()
    {
        using var spells = new SpellTestKit(SpellTestKit.Spell(ListSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        using AvScriptRig rig = Create(
            [AvNpc(NpcWingCommanderGuse)], [Spawn(1, NpcWingCommanderGuse, -1338.6f, -328.16f, Z)], -1335, -325,
            spells: spells, spellLists: ListOf(NpcWingCommanderGuse));
        Creature guse = rig.Single(NpcWingCommanderGuse);
        SpellInfo spell = spells.Store.Get(ListSpell)!;
        Assert.True(spells.System.IsSpellReady(guse, spell));

        rig.Map.Combat.DealDamage(rig.Player, guse, 1, direct: false);
        rig.Run(1500);

        Assert.False(spells.System.IsSpellReady(guse, spell)); // the entry's minute started
    }

    private static AvScriptRig IchmanRig(out Creature ichman)
    {
        AvScriptRig rig = Create(
            [AvNpc(NpcWingCommanderIchman)], [Spawn(1, NpcWingCommanderIchman, -1338.6f, -328.16f, Z)], -1335, -325, spellLists: ListOf(NpcWingCommanderIchman));
        ichman = rig.Single(NpcWingCommanderIchman);
        return rig;
    }

    private static int InvisibilityCasts(AvScriptRig rig) => rig.Caster.Casts.Count(c => c.Spell == AvEventAI.SpellGreaterInvisibility);

    [Fact]
    public void Ichman_CastsGreaterInvisibilityOnHimself_OnceASecondAfterHisSpawn()
    {
        using AvScriptRig rig = IchmanRig(out Creature ichman);

        rig.Run(500);
        Assert.Equal(0, InvisibilityCasts(rig));

        rig.Run(1000);
        (uint spell, Unit? target, bool triggered) = Assert.Single(rig.Caster.Casts, c => c.Spell == AvEventAI.SpellGreaterInvisibility);
        Assert.Equal(16380u, spell);
        Assert.Same(ichman, target);
        Assert.False(triggered);

        rig.Run(5000);
        Assert.Equal(1, InvisibilityCasts(rig)); // row 1343701 does not repeat
    }

    [Fact]
    public void Ichman_WhoHasTheAura_DoesNotCastItAgain()
    {
        using AvScriptRig rig = IchmanRig(out Creature ichman);
        rig.Caster.Auras.Add((ichman, AvEventAI.SpellGreaterInvisibility));

        rig.Run(1500);

        Assert.Equal(0, InvisibilityCasts(rig));
    }

    [Fact]
    public void Ichman_LosesGreaterInvisibilityOnAggro_AndCastsItOnlyAfterTheEvade()
    {
        using AvScriptRig rig = IchmanRig(out Creature ichman);
        rig.Run(500); // the timer is still running when the fight starts
        rig.Caster.Auras.Add((ichman, AvEventAI.SpellGreaterInvisibility));

        rig.Map.Combat.DealDamage(rig.Player, ichman, 1, direct: false);

        Assert.Contains(rig.Caster.RemovedAuras, r => ReferenceEquals(r.Unit, ichman) && r.Spell == AvEventAI.SpellGreaterInvisibility);
        Assert.False(rig.Caster.HasAura(ichman, AvEventAI.SpellGreaterInvisibility));

        rig.Run(1500);
        Assert.True(ichman.Combat.IsInCombat);
        Assert.Equal(0, InvisibilityCasts(rig)); // the row is out of combat

        rig.Creatures.EnterEvadeMode(ichman);
        rig.RunUntil(() => InvisibilityCasts(rig) == 1, "the cast after the evade");
    }

    [Fact]
    public void Ichman_CastsGreaterInvisibilityAgain_AfterARespawn()
    {
        using AvScriptRig rig = IchmanRig(out Creature ichman);
        rig.Run(1500);
        Assert.Equal(1, InvisibilityCasts(rig));
        rig.Caster.Casts.Clear();

        rig.Map.Combat.Kill(null, ichman);
        rig.Creatures.ForceRespawn(ichman);
        Assert.True(ichman.IsAlive);

        rig.Run(1500);
        Assert.Equal(1, InvisibilityCasts(rig));
    }

    [Fact]
    public void AnotherWingCommander_HasNoGreaterInvisibility()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcWingCommanderSlidore)], [Spawn(1, NpcWingCommanderSlidore, -1338.6f, -328.16f, Z)], -1335, -325, spellLists: ListOf(NpcWingCommanderSlidore));

        rig.Run(1500);

        Assert.Equal(0, InvisibilityCasts(rig));
    }
}
