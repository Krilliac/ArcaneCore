using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class WailingCavernsEctoplasmTests
{
    [Theory]
    [InlineData(5776u)]
    [InlineData(5777u)]
    [InlineData(5778u)]
    public void VmangosColouredEntriesAllSelectTheScript(uint entry)
    {
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [entry], [entry], []);
        Assert.Equal("EvolvingEctoplasmAi", run.Creature(entry).AI?.GetType().Name);
    }

    [Theory]
    [InlineData(SpellSchool.Fire, 7943u, 7942u)]
    [InlineData(SpellSchool.Frost, 7944u, 7940u)]
    [InlineData(SpellSchool.Nature, 7945u, 7941u)]
    [InlineData(SpellSchool.Shadow, 7946u, 7743u)]
    public void FirstElementalHitTransformsForTenSeconds_ThenResetsOnTheManualClock(
        SpellSchool school, uint transform, uint immune)
    {
        // vmangos wailing_caverns.cpp EvolvingEctoplasmAI::SpellHit/UpdateAI.
        var caster = new AuraTrackingCaster();
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [5776], [5776], [], aiServices: new ArcaneCore.Game.Creatures.CreatureAiServices { Spells = caster });
        var ectoplasm = run.Creature(5776);
        Assert.Equal("EvolvingEctoplasmAi", ectoplasm.AI?.GetType().Name);
        caster.Removed.Clear();

        ectoplasm.AI!.OnSpellHit(run.Player, new SpellInfo { Id = 1, School = school });
        Assert.Contains(caster.Casts, c => c.Spell == transform);
        Assert.Contains(caster.Casts, c => c.Spell == immune);
        int casts = caster.Casts.Count;
        ectoplasm.AI.OnSpellHit(run.Player,
            new SpellInfo { Id = 2, School = school == SpellSchool.Fire ? SpellSchool.Frost : SpellSchool.Fire });
        Assert.Equal(casts, caster.Casts.Count);

        run.Tick(10_000);
        Assert.DoesNotContain(caster.Removed, c => c.Spell == transform);
        run.Tick(1);
        Assert.Contains(caster.Removed, c => c.Spell == transform);
        Assert.Contains(caster.Removed, c => c.Spell == immune);

        ectoplasm.AI.OnSpellHit(run.Player, new SpellInfo { Id = 3, School = school });
        Assert.Equal(casts + 2, caster.Casts.Count);
    }

    [Fact]
    public void EvadeClearsTheCurrentElementalTransformation()
    {
        var caster = new AuraTrackingCaster();
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [5776], [5776], [], aiServices: new ArcaneCore.Game.Creatures.CreatureAiServices { Spells = caster });
        var ectoplasm = run.Creature(5776);
        ectoplasm.AI!.OnSpellHit(run.Player, new SpellInfo { Id = 1, School = SpellSchool.Fire });
        caster.Removed.Clear();
        ectoplasm.AI.OnEvade();
        Assert.Contains(caster.Removed, c => c.Spell == 7943);
        Assert.Contains(caster.Removed, c => c.Spell == 7942);
    }
}
