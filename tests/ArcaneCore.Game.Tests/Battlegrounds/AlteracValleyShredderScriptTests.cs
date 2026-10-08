using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// vmangos AVCreateShredderScript (scripts/battlegrounds/battleground_alterac.cpp:4878-4891), the spell script of Create Shredder 21544 and
/// 21565: the wild summon (SPELL_EFFECT_SUMMON_WILD) is marked as created by the spell its second effect triggers (Control Shredder 21556 /
/// 21566) and gets the caster as its creator. Real spell system, summon service and creature system (<see cref="PetTestKit"/>).
/// </summary>
public sealed class AlteracValleyShredderScriptTests
{
    private const uint ControlShredderAlliance = 21566;
    private const uint ControlShredderHorde = 21556;
    private const uint OtherWildSpell = 991_301;

    private static IEnumerable<SpellInfo> ShredderSpells() =>
    [
        // spell_template 21565 / 21544 (mangos-classic Spell.sql): effect 1 SUMMON_WILD of the shredder, effect 2 TRIGGER_SPELL Control Shredder.
        Spell(AlteracValley.SpellSummonShredderAlliance,
            Effect(SpellEffectName.SummonWild, 1, misc: (int)PetTestKit.WildEntry),
            Effect(SpellEffectName.TriggerSpell, 0, trigger: ControlShredderAlliance)),
        Spell(AlteracValley.SpellSummonShredderHorde,
            Effect(SpellEffectName.SummonWild, 1, misc: (int)PetTestKit.WildEntry),
            Effect(SpellEffectName.TriggerSpell, 0, trigger: ControlShredderHorde)),
        Spell(ControlShredderAlliance, Effect(SpellEffectName.Dummy, 0)),
        Spell(ControlShredderHorde, Effect(SpellEffectName.Dummy, 0)),
        // Another wild summon that triggers a spell: no script, so it keeps the spell that summoned it and no creator.
        Spell(OtherWildSpell,
            Effect(SpellEffectName.SummonWild, 1, misc: (int)PetTestKit.WildEntry),
            Effect(SpellEffectName.TriggerSpell, 0, trigger: ControlShredderAlliance)),
    ];

    [Theory]
    [InlineData(AlteracValley.SpellSummonShredderAlliance, ControlShredderAlliance)]
    [InlineData(AlteracValley.SpellSummonShredderHorde, ControlShredderHorde)]
    public void CreateShredder_TheShredderIsCreatedByTheControlSpell_AndItsCreatorIsTheCaster(uint createShredder, uint controlShredder)
    {
        using var kit = new PetTestKit(ShredderSpells());
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, createShredder));

        Creature shredder = Assert.Single(kit.Creatures.Creatures, c => c.Summon is { Kind: SummonKind.Wild });
        Assert.Equal(controlShredder, shredder.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.Equal(player.Guid, shredder.CreatorGuid);
    }

    [Fact]
    public void AnotherWildSummon_KeepsItsOwnSpell_AndHasNoCreator()
    {
        using var kit = new PetTestKit(ShredderSpells());
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, OtherWildSpell));

        Creature summon = Assert.Single(kit.Creatures.Creatures, c => c.Summon is { Kind: SummonKind.Wild });
        Assert.Equal(OtherWildSpell, summon.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.True(summon.CreatorGuid.IsEmpty);
    }
}
