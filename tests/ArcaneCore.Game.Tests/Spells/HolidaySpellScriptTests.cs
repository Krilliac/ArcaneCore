using ArcaneCore.Game.Spells.Holidays;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>The holiday spell scripts (vmangos SpellEffects.cpp EffectDummy / EffectScriptEffect cases).</summary>
public sealed class HolidaySpellScriptTests
{
    [Fact]
    public void Every_holiday_spell_has_its_script()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        Assert.IsType<HallowsEndTreatScript>(registry.Find(24930));
        Assert.IsType<HallowsEndTrickScript>(registry.Find(24714));
        foreach (uint id in new uint[] { 24717, 24718, 24719, 24720, 24737 }) Assert.IsType<HallowedWandScript>(registry.Find(id));
        Assert.IsType<TrickOrTreatScript>(registry.Find(24751));
        Assert.IsType<MistletoeScript>(registry.Find(26004));
        Assert.IsType<MistletoeKissScript>(registry.Find(26218));
        Assert.IsType<WinterWondervoltScript>(registry.Find(26275));
        Assert.IsType<RibbonPoleTriggerScript>(registry.Find(29710));
        Assert.IsType<SnowballKnockdownScript>(registry.Find(21343));
        Assert.IsType<ReindeerTransformationScript>(registry.Find(25860));
    }

    [Theory]
    [InlineData(24717u, true, 0, 24708u)]
    [InlineData(24717u, false, 0, 24709u)]
    [InlineData(24718u, true, 0, 24711u)]
    [InlineData(24719u, false, 0, 24713u)]
    [InlineData(24737u, true, 0, 24735u)]
    [InlineData(24720u, true, 3, 24723u)]
    [InlineData(24720u, false, 5, 24736u)]
    [InlineData(24720u, true, 6, 24740u)]
    public void Hallowed_wands_pick_the_costume_by_gender(uint spell, bool male, int roll, uint costume)
        => Assert.Equal(costume, HallowedWandScript.CostumeFor(spell, male, roll));

    [Fact]
    public void Trick_has_eight_costumes_by_gender()
    {
        Assert.Equal(8, HallowsEndTrickScript.Costumes(true).Length);
        Assert.Equal(24709u, HallowsEndTrickScript.Costumes(false)[0]);
        Assert.Equal(24753u, HallowsEndTrickScript.Costumes(true)[7]);
    }
}
