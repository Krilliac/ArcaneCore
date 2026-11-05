using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Feign Death against creature targeting, driven through a real Feign Death aura (success and resist) and the creature paths that
/// choose a victim. vmangos Unit::IsTargetableBy (Unit.cpp:6158): a creature cannot single-target a unit whose feign succeeded
/// (UNIT_STATE_FEIGN_DEATH is added only in the success branch of Unit::SetFeignDeath, Unit.cpp:9240-9250); a resisted feign
/// leaves the creature on the hunter. The dead flag alone is not the state. Area effects keep the plain attackability.
/// </summary>
public sealed class FeignDeathCreatureTests : IDisposable
{
    private const uint FeignSpell = 930101;
    private const uint NpcBolt = 930102;

    private readonly WorldRuntime _world;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly SpellSystem _spells;
    private readonly FakeRelations _relations = new();
    private readonly Player _hunter;
    private readonly Creature _wolf;
    private readonly RollSource _roll = new();
    private uint _now = 10_000;

    public FeignDeathCreatureTests()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 3, 0)]);
        (_world, _map, _creatures) = CreatureAiTestKit.CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        var store = new SpellStore(
        [
            Spell(FeignSpell, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.FeignDeath)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(NpcBolt, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 40),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
        ], [], []);
        _spells = new SpellSystem(store, () => _now, random: _roll) { MapUpdateIntervalMs = 0, Relations = _relations };
        RangedHandlers.Register(_spells);
        _map.Combat.SpellMitigation = _spells; // the SpellFeature does this per map in the host
        (_hunter, _) = CreatureAiTestKit.AddPlayer(_world, 1, 0, 0);
        _hunter.Level = 60;
        _wolf = Assert.Single(_creatures.Creatures);
        _wolf.Level = 60;
        _relations.Hostile.Add(_hunter.Guid);
    }

    public void Dispose() => _world.Dispose();

    private SpellCastResult CastFeign()
    {
        SpellCastResult result = _spells.CastSpell(_hunter, FeignSpell, SpellCastTargets.ForSelf(), triggered: false);
        if (_spells.SpellBatchWindowMs != 0)
        {
            _now += _spells.SpellBatchWindowMs;
            _spells.Update(_spells.SpellBatchWindowMs);
        }

        return result;
    }

    private SpellCastResult NpcCastsAtHunter()
        => _spells.CastSpell(_wolf, NpcBolt, SpellCastTargets.ForUnit(_hunter.Guid), triggered: false);

    private void WolfHoldsHunterInItsThreatList() => _map.Combat.DealDamage(_hunter, _wolf, 7, direct: false);

    [Fact]
    public void ASuccessfulFeign_StopsTheCreatureFromAttackStartAndAggroOnSight_UntilItEnds()
    {
        Assert.True(_creatures.CanAggroOnSight(_wolf, _hunter)); // the scene is otherwise aggressive
        _roll.Value = 10_000;

        Assert.Equal(SpellCastResult.CastOk, CastFeign());
        Assert.True(_spells.IsFeigningDeath(_hunter));

        Assert.False(_creatures.CanAggroOnSight(_wolf, _hunter));
        Assert.False(_creatures.AttackStart(_wolf, _hunter));
        Assert.Null(_wolf.Combat.Victim);
        Assert.True(_map.Combat.Hooks.CanAttack(_wolf, _hunter)); // an area effect still sees the feigner

        _spells.BreakFeignDeath(_hunter);

        Assert.True(_creatures.CanAggroOnSight(_wolf, _hunter));
        Assert.True(_creatures.AttackStart(_wolf, _hunter));
        Assert.Same(_hunter, _wolf.Combat.Victim);
    }

    [Fact]
    public void AResistedFeign_KeepsTheCreatureOnTheHunter_AlthoughTheDeadFlagIsSet()
    {
        WolfHoldsHunterInItsThreatList();
        _roll.Value = 0; // the creature resists

        Assert.Equal(SpellCastResult.CastOk, CastFeign());

        Assert.False(_spells.IsFeigningDeath(_hunter));
        Assert.True(_hunter.HasFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead));
        Assert.True(_map.Combat.CanAttackSingleTarget(_wolf, _hunter));
        Assert.True(_creatures.SelectHostileTarget(_wolf));
        Assert.Same(_hunter, _wolf.Combat.Victim);
        Assert.False(_wolf.IsEvading);
    }

    [Fact]
    public void ASuccessfulFeign_MakesTheCreatureDropTheHunter()
    {
        WolfHoldsHunterInItsThreatList();
        _roll.Value = 10_000;

        Assert.Equal(SpellCastResult.CastOk, CastFeign());

        Assert.True(_spells.IsFeigningDeath(_hunter));
        Assert.False(_map.Combat.CanAttackSingleTarget(_wolf, _hunter));
        Assert.False(_creatures.SelectHostileTarget(_wolf));
        Assert.Null(_wolf.Combat.Victim);
    }

    [Fact]
    public void ALivingUnitWithOnlyTheDeadFlag_IsStillATarget()
    {
        _hunter.SetFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead); // what a script sets without any feign

        Assert.False(_spells.IsFeigningDeath(_hunter));
        Assert.True(_map.Combat.CanAttackSingleTarget(_wolf, _hunter));
        Assert.True(_creatures.AttackStart(_wolf, _hunter));
    }

    [Fact]
    public void ACreatureSingleTargetSpell_IsRefusedOnASuccessfulFeign_ButNotOnAResistedOne()
    {
        Assert.NotEqual(SpellCastResult.BadTargets, NpcCastsAtHunter()); // control: the hunter is a valid explicit target

        WolfHoldsHunterInItsThreatList();
        _roll.Value = 0;
        CastFeign();
        Assert.False(_spells.IsFeigningDeath(_hunter));
        Assert.NotEqual(SpellCastResult.BadTargets, NpcCastsAtHunter());

        _spells.BreakFeignDeath(_hunter);
        _roll.Value = 10_000;
        CastFeign();
        Assert.True(_spells.IsFeigningDeath(_hunter));
        Assert.Equal(SpellCastResult.BadTargets, NpcCastsAtHunter());
    }

    [Fact]
    public void BreakSuccessfulFeignDeath_LeavesAResistedFeignInPlace_AndEndsASuccessfulOne()
    {
        WolfHoldsHunterInItsThreatList();
        _roll.Value = 0;
        CastFeign();
        _spells.BreakSuccessfulFeignDeath(_hunter);
        Assert.True(_spells.HasAura(_hunter, FeignSpell)); // vmangos guards every site with HasUnitState(UNIT_STATE_FEIGN_DEATH)

        _spells.BreakFeignDeath(_hunter);
        _roll.Value = 10_000;
        CastFeign();
        Assert.True(_spells.IsFeigningDeath(_hunter));
        _spells.BreakSuccessfulFeignDeath(_hunter);
        Assert.False(_spells.HasAura(_hunter, FeignSpell));
    }

    private sealed class RollSource : Random
    {
        public int Value { get; set; } = 10_000;

        public override int Next(int minValue, int maxValue) => Math.Clamp(Value, minValue, maxValue - 1);
    }
}
