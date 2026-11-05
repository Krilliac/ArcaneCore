using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
using ArcaneCore.Game.Creatures;
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
/// are replaced by <see cref="AvEventAI"/>, which names the list because the classic-db templates do not.
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
}
