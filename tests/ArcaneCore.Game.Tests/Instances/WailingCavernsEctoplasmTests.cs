using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class WailingCavernsEctoplasmTests
{
    [Fact]
    public void IdleTicksDoNotRepeatedlyRemoveAuras_AndResetStripsEveryAura()
    {
        var caster = new ResetRecordingCaster();
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [5776], [5776], [], aiServices: new CreatureAiServices { Spells = caster });
        var ai = run.Creature(5776).AI!;
        caster.Clear();
        ai.OnUpdate(50);
        ai.OnUpdate(50);
        Assert.Empty(caster.Removed);
        ai.OnRespawn();
        Assert.Equal(1, caster.RemoveAllCalls);
    }

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

    private sealed class ResetRecordingCaster : ICreatureSpellCaster, ICreatureAuraReset
    {
        public List<uint> Removed { get; } = [];
        public int RemoveAllCalls { get; private set; }
        public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }
        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered) => CreatureCastResult.Ok;
        public bool IsCasting(Creature caster) => false;
        public bool HasAura(Unit unit, uint spellId) => false;
        public void RemoveAuras(Unit unit, uint spellId) => Removed.Add(spellId);
        public void Interrupt(Creature caster) { }
        public void OnCreatureRemoved(Creature creature) { }
        public void ResetAuras(Creature creature, bool keepPositive) { }
        public void RemoveAllAuras(Creature creature) => RemoveAllCalls++;
        public void Clear() { Removed.Clear(); RemoveAllCalls = 0; }
    }
}
