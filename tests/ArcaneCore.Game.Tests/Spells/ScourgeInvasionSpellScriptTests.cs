using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.WorldEvents;
using ArcaneCore.Kernel.WorldData.WorldState;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The two spell scripts of mangos-classic world/scourge_invasion.cpp bound at AddSC_scourge_invasion (:1378-1379):
/// spell_communique_trigger (:1305-1312, spell 28345) and spell_despawner_self (:1295-1303, spell 28091). The spells are declared here with
/// only a DUMMY effect, which is the hook the scripts run from; what they do is read off the system's SpellHit events.
/// </summary>
public sealed class ScourgeInvasionSpellScriptTests : IDisposable
{
    private readonly SpellTestKit _kit;
    private readonly Player _caster;
    private readonly Player _other;
    private readonly List<(Unit Caster, Unit Target, uint Spell)> _hits = [];

    public ScourgeInvasionSpellScriptTests()
    {
        _kit = new SpellTestKit(
            // 28345 Communique Trigger: a dummy effect on the explicit unit target.
            Spell(ScourgeInvasionCatalog.CommuniqueTrigger, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.Unit)),
            // 28281 Communique, Camp to Relay and 17680 Spirit Spawn-out only mark that they were cast: a dummy effect on the caster.
            Spell(ScourgeInvasionCatalog.CommuniqueCampToRelay, Effect(SpellEffectName.Dummy, 0)),
            Spell(ScourgeInvasionCatalog.DespawnerSelf, Effect(SpellEffectName.Dummy, 0)),
            Spell(ScourgeInvasionCatalog.SpiritSpawnOut, Effect(SpellEffectName.Dummy, 0)));
        (_caster, _) = _kit.AddPlayer(1);
        (_other, _) = _kit.AddPlayer(2, 1, 0);
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        _kit.System.SpellHit += (caster, target, spell) => _hits.Add((caster, target, spell.Id));
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void CommuniqueTriggerAndDespawnerSelfHaveTheirScripts()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        Assert.IsType<CommuniqueTriggerScript>(registry.Find(28345));
        Assert.IsType<DespawnerSelfScript>(registry.Find(28091));
        // The spells the two scripts cast, and the camp timer that triggers the first, have no script of their own in mangos-classic.
        Assert.Null(registry.Find(ScourgeInvasionCatalog.CommuniqueCampToRelay));
        Assert.Null(registry.Find(ScourgeInvasionCatalog.SpiritSpawnOut));
        Assert.Null(registry.Find(ScourgeInvasionCatalog.CommuniqueTimerCamp));
        // The catalog constants the scripts bind to are the ids in the C++ (scourge_invasion.h:39-71, :100-115).
        Assert.Equal(28345u, ScourgeInvasionCatalog.CommuniqueTrigger);
        Assert.Equal(28091u, ScourgeInvasionCatalog.DespawnerSelf);
        Assert.Equal(28281u, ScourgeInvasionCatalog.CommuniqueCampToRelay);
        Assert.Equal(17680u, ScourgeInvasionCatalog.SpiritSpawnOut);
    }

    [Fact]
    public void CommuniqueTriggerMakesItsTargetCastCampToRelay()
    {
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_caster, ScourgeInvasionCatalog.CommuniqueTrigger,
            SpellCastTargets.ForUnit(_other.Guid), triggered: true));

        // The unit target, not the caster of 28345, casts 28281.
        var cast = Assert.Single(_hits, h => h.Spell == ScourgeInvasionCatalog.CommuniqueCampToRelay);
        Assert.Same(_other, cast.Caster);
        Assert.Same(_other, cast.Target);
        Assert.DoesNotContain(_hits, h => h.Spell == ScourgeInvasionCatalog.CommuniqueCampToRelay && ReferenceEquals(h.Caster, _caster));
    }

    [Fact]
    public void CommuniqueTriggerCastOnItselfStillMakesTheCasterCastCampToRelay()
    {
        // A targetless (self) cast resolves the unit target to the caster (the camp timer's trigger is cast by the shard on itself).
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_caster, ScourgeInvasionCatalog.CommuniqueTrigger,
            SpellCastTargets.ForSelf(), triggered: true));

        Assert.Same(_caster, Assert.Single(_hits, h => h.Spell == ScourgeInvasionCatalog.CommuniqueCampToRelay).Caster);
    }

    [Fact]
    public void DespawnerSelfCastsSpiritSpawnOutOnlyOutOfCombat()
    {
        // In combat (UNIT_FLAG_IN_COMBAT): nothing.
        _caster.UnitFlags |= UnitFlags.InCombat;
        Assert.True(_caster.Combat.IsInCombat);
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_caster, ScourgeInvasionCatalog.DespawnerSelf,
            SpellCastTargets.ForSelf(), triggered: true));
        Assert.DoesNotContain(_hits, h => h.Spell == ScourgeInvasionCatalog.SpiritSpawnOut);

        // Out of combat: the caster casts 17680 on itself.
        _caster.UnitFlags &= ~UnitFlags.InCombat;
        Assert.False(_caster.Combat.IsInCombat);
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_caster, ScourgeInvasionCatalog.DespawnerSelf,
            SpellCastTargets.ForSelf(), triggered: true));
        var spawnOut = Assert.Single(_hits, h => h.Spell == ScourgeInvasionCatalog.SpiritSpawnOut);
        Assert.Same(_caster, spawnOut.Caster);
        Assert.Same(_caster, spawnOut.Target);
    }
}
