using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Feign Death (vmangos Aura::HandleFeignDeath, SpellAuras.cpp:3460-3500, and Unit::SetFeignDeath,
/// Unit.cpp:9228-9278): the resist roll of creatures that hold the feigner in their threat list,
/// the combat drop, interrupted casts and the dynamic flag. Synthetic spells; creature reactions
/// beyond the cleared threat list belong to the creature AI.
/// </summary>
public sealed class FeignDeathTests : IDisposable
{
    private const uint FeignSpell = 930001;
    private const uint SlowBolt = 930002;
    private const uint HiddenBuff = 930003;
    private const uint Channel = 930004;

    private readonly WorldRuntime _world;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly SpellSystem _spells;
    private readonly FakeRelations _relations = new();
    private readonly Player _hunter;
    private readonly FakeSession _session = new(1);
    private uint _now = 10_000;

    public FeignDeathTests()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 3, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem creatures) = CreateSystem(content);
        _world = world;
        _map = map;
        _creatures = creatures;
        var store = new SpellStore(
        [
            Spell(FeignSpell, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.FeignDeath)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(SlowBolt, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with
            {
                CastTime = new SpellCastTime(2000, 0, 0),
                RangeIndex = 4,
                Range = new SpellRange(0, 40),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(HiddenBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                AuraInterruptFlags = (SpellAuraInterruptFlags)0x00100000,
                SpellVisual = 1,
            },
            Spell(Channel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                AttributesEx = SpellAttributesEx.IsChanneled,
                Duration = new SpellDuration(5000, 0, 5000),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
        ], [], []);
        _spells = new SpellSystem(store, () => _now, random: new FixedRandom(10_000)) { MapUpdateIntervalMs = 0, Relations = _relations };
        RangedAuras.Register(_spells);
        _hunter = TestWorld.CreatePlayer(1, 0, 0, _session);
        _hunter.Level = 60;
        _world.AddPlayer(_hunter);
        _world.RunTick(50);
        _session.Clear();
        Wolf.Level = 60;
        _map.Combat.DealDamage(_hunter, Wolf, 7, direct: false); // the wolf now holds the hunter in its threat list
        _session.Clear();
    }

    private Creature Wolf => Assert.Single(_creatures.Creatures);

    public void Dispose() => _world.Dispose();

    private SpellCastResult CastFeign(bool triggered = false)
        => _spells.CastSpell(_hunter, FeignSpell, SpellCastTargets.ForSelf(), triggered);

    private void Advance(uint ms)
    {
        _now += ms;
        _spells.Update(ms);
    }

    private static List<WorldOpcode> Sent(FakeSession session) => [.. session.Sent.Select(p => p.Opcode)];

    private sealed class FixedRandom(int value) : Random
    {
        public int Value { get; set; } = value;

        public override int Next(int minValue, int maxValue) => Math.Clamp(Value, minValue, maxValue - 1);
    }

    private FixedRandom Roll => (FixedRandom)_spells.Random;

    [Fact]
    public void ASuccessfulFeign_ClearsTheThreat_LeavesCombat_AndAppearsDead()
    {
        Roll.Value = 10_000; // never resisted
        Assert.True(Wolf.Combat.Threat.GetThreat(_hunter) > 0);

        Assert.Equal(SpellCastResult.CastOk, CastFeign());

        Assert.True(_spells.IsFeigningDeath(_hunter));
        Assert.True(_hunter.HasFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead));
        Assert.Equal(0f, Wolf.Combat.Threat.GetThreat(_hunter));
        Assert.DoesNotContain(Wolf, _hunter.Combat.ThreatenedBy);
        Assert.False(_hunter.Combat.IsInCombat);
        Assert.Empty(_hunter.Combat.Attackers);
        Assert.Contains(WorldOpcode.SmsgCancelCombat, Sent(_session));
        Assert.DoesNotContain(WorldOpcode.SmsgFeignDeathResisted, Sent(_session));
    }

    [Fact]
    public void AResistedFeign_KeepsTheThreat_TellsTheClient_ButStillShowsTheDeadFlag()
    {
        Roll.Value = 0; // the roll that always resists
        Assert.True(_hunter.Combat.IsInCombat);

        Assert.Equal(SpellCastResult.CastOk, CastFeign());

        Assert.False(_spells.IsFeigningDeath(_hunter));
        Assert.True(_hunter.HasFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead));
        Assert.True(Wolf.Combat.Threat.GetThreat(_hunter) > 0);
        Assert.True(_hunter.Combat.IsInCombat);
        Assert.Contains(WorldOpcode.SmsgFeignDeathResisted, Sent(_session));
        Assert.Contains(WorldOpcode.SmsgCancelCombat, Sent(_session)); // SendAttackSwingCancelAttack
    }

    [Fact]
    public void ACreatureBeyondItsAttackDistance_CannotResist()
    {
        Roll.Value = 0;
        Wolf.Relocate(80, 0, 83.5f, 0, 0);

        Assert.Equal(SpellCastResult.CastOk, CastFeign());

        Assert.True(_spells.IsFeigningDeath(_hunter));
        Assert.Equal(0f, Wolf.Combat.Threat.GetThreat(_hunter));
    }

    [Fact]
    public void OnlyCreaturesResist_ANonCreatureHoldingTheHunterInItsThreatListDoesNot()
    {
        Roll.Value = 0;
        Wolf.Combat.Threat.Clear();
        var other = new CombatTestUnit();
        other.Spawn(_map, 3, 0);
        other.Combat.Threat.AddThreat(_hunter, 10);

        Assert.Equal(SpellCastResult.CastOk, CastFeign());

        Assert.True(_spells.IsFeigningDeath(_hunter));
        Assert.Equal(0f, other.Combat.Threat.GetThreat(_hunter));
    }

    [Fact]
    public void Feigning_InterruptsHostileCastsAimedAtTheFeigner_ButNotFriendlyOnes()
    {
        Roll.Value = 10_000;
        Player enemy = TestWorld.CreatePlayer(2, 5, 0, new FakeSession(2));
        Player friend = TestWorld.CreatePlayer(3, 6, 0, new FakeSession(3));
        _world.AddPlayer(enemy);
        _world.AddPlayer(friend);
        _relations.Hostile.Add(enemy.Guid);
        Assert.Equal(SpellCastResult.CastOk, _spells.CastSpell(enemy, SlowBolt, SpellCastTargets.ForUnit(_hunter.Guid), triggered: false));
        Assert.Equal(SpellCastResult.CastOk, _spells.CastSpell(friend, SlowBolt, SpellCastTargets.ForUnit(_hunter.Guid), triggered: false));
        Assert.NotNull(_spells.GetState(enemy.Guid)!.CurrentCast);

        Assert.Equal(SpellCastResult.CastOk, CastFeign());

        Assert.Null(_spells.GetState(enemy.Guid)?.CurrentCast);
        Assert.NotNull(_spells.GetState(friend.Guid)?.CurrentCast); // vmangos only interrupts non-friendly casters
    }

    [Fact]
    public void TheFeignersOwnChannelEnds_AndTheFeignCastSendsNoInterruptMessage()
    {
        Roll.Value = 10_000;
        Assert.Equal(SpellCastResult.CastOk, _spells.CastSpell(_hunter, Channel, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(SpellCastState.Casting, _spells.GetState(_hunter.Guid)!.CurrentCast!.State);
        Advance(1500); // clear the cooldown and global state of the channel start
        _session.Clear();
        Assert.Equal(SpellCastState.Casting, _spells.GetState(_hunter.Guid)!.CurrentCast!.State);

        // A triggered feign (no cast slot of its own) ends the running channel.
        Assert.Equal(SpellCastResult.CastOk, CastFeign(triggered: true));
        Assert.Null(_spells.GetState(_hunter.Guid)?.CurrentCast);
        _spells.RemoveAuras(_hunter, FeignSpell);
        _session.Clear();

        // A normal feign cast completes without a failure packet for itself.
        Assert.Equal(SpellCastResult.CastOk, CastFeign());
        Assert.DoesNotContain(WorldOpcode.SmsgSpellFailure, Sent(_session));
        Assert.DoesNotContain(WorldOpcode.SmsgSpellFailedOther, Sent(_session));
        Assert.True(_spells.IsFeigningDeath(_hunter));
    }

    [Fact]
    public void AStealthBreakingAura_IsStrippedBySuccess_ButNotByAResist()
    {
        _spells.CastSpell(_hunter, HiddenBuff, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(_spells.HasAura(_hunter, HiddenBuff));

        Roll.Value = 0;
        CastFeign();
        Assert.True(_spells.HasAura(_hunter, HiddenBuff)); // resisted: nothing is stripped
        _spells.RemoveAuras(_hunter, FeignSpell);

        Roll.Value = 10_000;
        CastFeign();
        Assert.False(_spells.HasAura(_hunter, HiddenBuff));
    }

    [Fact]
    public void MovementFlagsAreClearedOnApply()
    {
        Roll.Value = 10_000;
        _hunter.ApplyMovement(new MovementInfo { Flags = MovementFlags.Forward | MovementFlags.TurnLeft | MovementFlags.WalkMode, X = 0, Y = 0, Z = 83.5f }, 0);

        CastFeign();

        Assert.Equal(MovementFlags.WalkMode, _hunter.Movement.Flags);
    }

    [Fact]
    public void RemovingTheAura_ClearsTheFlagAndTheState_AndBreakFeignDeathRemovesIt()
    {
        Roll.Value = 10_000;
        CastFeign();
        Assert.True(_spells.IsFeigningDeath(_hunter));

        _spells.BreakFeignDeath(_hunter);

        Assert.False(_spells.IsFeigningDeath(_hunter));
        Assert.False(_spells.HasAura(_hunter, FeignSpell));
        Assert.False(_hunter.HasFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead));
        _spells.BreakFeignDeath(_hunter); // nothing to break: harmless
    }

    [Fact]
    public void ACastStartedWhileFeigning_IsCancelledAtTheNextUpdate()
    {
        Roll.Value = 10_000;
        Player enemy = TestWorld.CreatePlayer(2, 10, 0, new FakeSession(2));
        _world.AddPlayer(enemy);
        _relations.Hostile.Add(enemy.Guid);
        CastFeign();
        Assert.Equal(SpellCastResult.CastOk, _spells.CastSpell(_hunter, SlowBolt, SpellCastTargets.ForUnit(enemy.Guid), triggered: false));
        Assert.NotNull(_spells.GetState(_hunter.Guid)!.CurrentCast);

        Advance(100);

        Assert.Null(_spells.GetState(_hunter.Guid)?.CurrentCast);
    }

    [Fact]
    public void ADeadFeignerIsNotFeigning()
    {
        Roll.Value = 10_000;
        CastFeign();
        _hunter.Health = 0;

        Assert.False(_spells.IsFeigningDeath(_hunter));
    }
}
