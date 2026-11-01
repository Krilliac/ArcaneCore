using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Tanaris field sampling 11759-11761 (vmangos spell_item.cpp TanarisFieldSamplingScript): the sample item is created half the time.</summary>
public sealed class QuestItemScriptTests
{
    private sealed class FixedRandom(int next) : Random(1)
    {
        public override int Next(int maxValue) => Math.Min(next, maxValue - 1);
    }

    [Theory]
    [InlineData(11759u, 49, true)]
    [InlineData(11760u, 50, false)]
    [InlineData(11761u, 99, false)]
    [InlineData(11761u, 0, true)]
    public void TanarisFieldSampling_CreatesTheItemOnlyOnAHalfChanceRoll(uint spell, int roll, bool created)
    {
        using var kit = new SpellTestKit(
            Spell(spell, Effect(SpellEffectName.CreateItem, 1, SpellImplicitTarget.UnitCaster)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 });
        int count = 0;
        kit.System.RegisterEffect(SpellEffectName.CreateItem, _ => count++);
        kit.System.Random = new FixedRandom(roll);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(created ? 1 : 0, count);
    }
}
