using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// S02b spell seams: cast checks with a stable priority table, cast observers, value modifiers, the
/// next-swing melee slot, CancelCast and the chain-range extension point. Reference: vmangos
/// Spell::prepare / cast / cancel (Spell.cpp:3352-3560, 3680-3790), SpellCaster::SetCurrentCastedSpell
/// (SpellCaster.cpp:1917-1992), WorldSession::HandleCancelCastOpcode (SpellHandler.cpp:320-331).
/// </summary>
public sealed class SpellSeamTests
{
    private const uint NextSwing = 910001;    // Attributes 0x4 (Heroic Strike / Cleave shape)
    private const uint NextSwingAlt = 910002; // Attributes 0x400 (Maul / Raptor Strike shape)
    private const uint ChainBolt = 910003;

    private static SpellInfo MeleeSwingSpell(uint id, SpellAttributes bit) => Spell(id, Effect(SpellEffectName.SchoolDamage, 11, SpellImplicitTarget.UnitEnemy)) with
    {
        Attributes = bit | SpellAttributes.IsAbility,
        RangeIndex = SpellConstants.RangeIndexCombat,
        Range = new SpellRange(0, 5),
        PowerType = (int)PowerType.Rage,
        ManaCost = 15,
        DamageClass = SpellDamageClass.Melee,
    };

    private static SpellTestKit Kit() => new(
        MeleeSwingSpell(NextSwing, (SpellAttributes)0x4),
        MeleeSwingSpell(NextSwingAlt, SpellAttributes.OnNextSwing),
        Spell(ChainBolt, Effect(SpellEffectName.SchoolDamage, 20, SpellImplicitTarget.UnitEnemy) with { ChainTarget = 3, Radius = 5 })
            with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 });

    private static (Player Caster, FakeSession Session, Player Target) Duel(SpellTestKit kit, float distance = 3)
    {
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, distance, 0);
        kit.Spellbook.Teach(caster, NextSwing, NextSwingAlt, CastBolt, ChannelSpell, InstantHeal, DotSpell);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        kit.World.RunTick(0);
        session.Clear();
        return (caster, session, target);
    }

    // --- cast checks -------------------------------------------------------------------------------------

    private sealed class RecordingCheck(SpellCheckPhase phase, int order, List<string> log, string name, SpellCastResult result = SpellCastResult.CastOk) : ISpellCastCheck
    {
        public SpellCheckPhase Phase { get; } = phase;

        public int Order { get; } = order;

        public Unit? LastTarget { get; private set; }

        public List<bool> Strict { get; } = [];

        public SpellCastResult Check(in SpellCastCheckContext context)
        {
            log.Add(name);
            LastTarget = context.Target;
            Strict.Add(context.Strict);
            return result;
        }
    }

    [Fact]
    public void CastCheck_Seam_CanVetoWithAnyResult_StrictFlagPassed()
    {
        using var kit = Kit();
        (Player caster, FakeSession session, Player target) = Duel(kit);
        var log = new List<string>();
        var veto = new RecordingCheck(SpellCheckPhase.Caster, SpellCastCheckOrder.Shapeshift, log, "shape", SpellCastResult.OnlyShapeshift);
        kit.System.RegisterCastCheck(veto);

        Assert.Equal(SpellCastResult.OnlyShapeshift, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));

        Assert.Equal((byte)SpellCastResult.OnlyShapeshift, Packets(session, WorldOpcode.SmsgCastResult).Single()[5]);
        Assert.Equal([true], veto.Strict);
        Assert.Equal(60u, target.Health);
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
    }

    [Fact]
    public void CastCheck_IsRepeatedAtLanding_WithStrictFalse()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        var log = new List<string>();
        var check = new RecordingCheck(SpellCheckPhase.Caster, SpellCastCheckOrder.CasterAuraState, log, "aura");
        kit.System.RegisterCastCheck(check);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        kit.Advance(2000);

        Assert.Equal([true, false], check.Strict);   // vmangos Spell::prepare CheckCast(true), Spell::cast CheckCast(false)
        Assert.Equal(45u, target.Health);
    }

    [Fact]
    public void CastChecks_RunByPhaseThenOrder_NotRegistrationOrder()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        var log = new List<string>();
        kit.System.RegisterCastCheck(new RecordingCheck(SpellCheckPhase.Items, SpellCastCheckOrder.Equipment, log, "equipment"));
        kit.System.RegisterCastCheck(new RecordingCheck(SpellCheckPhase.Target, SpellCastCheckOrder.TargetAuraState, log, "targetState"));
        kit.System.RegisterCastCheck(new RecordingCheck(SpellCheckPhase.Caster, SpellCastCheckOrder.CasterAuraState, log, "casterState"));
        kit.System.RegisterCastCheck(new RecordingCheck(SpellCheckPhase.Caster, SpellCastCheckOrder.Shapeshift, log, "shape"));

        kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid));

        Assert.Equal(["shape", "casterState", "targetState", "equipment"], log);
    }

    [Fact]
    public void CastCheck_OrderTable_FollowsVmangosCheckCastLineOrder()
    {
        // vmangos Spell.cpp: shapeshift 5342 < CasterAuraState 5392 < TargetAuraState 5636 < CheckItems 5698 (< CheckRange 5707 < CheckPower 5721).
        Assert.True(SpellCastCheckOrder.Shapeshift < SpellCastCheckOrder.CasterAuraState);
        Assert.True(SpellCastCheckOrder.TargetAuraState < SpellCastCheckOrder.Equipment);
        Assert.Equal(1, (int)SpellCheckPhase.Caster);
        Assert.Equal(2, (int)SpellCheckPhase.Target);
        Assert.Equal(3, (int)SpellCheckPhase.Items);
    }

    [Fact]
    public void CastCheck_TargetPhase_SeesTheResolvedTarget_AndIsSkippedWithoutAUnitTarget()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        var log = new List<string>();
        var check = new RecordingCheck(SpellCheckPhase.Target, SpellCastCheckOrder.TargetAuraState, log, "target");
        kit.System.RegisterCastCheck(check);

        kit.System.HandleCastRequest(caster, InstantHeal, SpellCastTargets.ForSelf());
        Assert.Empty(log);   // InstantHeal names no explicit unit target

        kit.Advance(1500);
        kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid));
        Assert.Same(target, check.LastTarget);
    }

    [Fact]
    public void CastCheck_Registration_RejectsNullAndDuplicates()
    {
        using var kit = Kit();
        var check = new RecordingCheck(SpellCheckPhase.Caster, 1, [], "x");
        kit.System.RegisterCastCheck(check);

        Assert.Throws<ArgumentNullException>(() => kit.System.RegisterCastCheck(null!));
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterCastCheck(check));
    }

    // --- observers ---------------------------------------------------------------------------------------

    private sealed class RecordingObserver : ISpellCastObserver
    {
        public List<string> Events { get; } = [];

        public List<SpellTargetOutcome> Outcomes { get; } = [];

        public void OnPrepared(SpellCast cast) => Events.Add($"prepared:{cast.Spell.Id}");

        public void OnCast(SpellCast cast) => Events.Add($"cast:{cast.Spell.Id}");

        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
        {
            Events.Add($"outcome:{cast.Spell.Id}");
            Outcomes.Add(outcome);
        }

        public void OnFinished(SpellCast cast, bool completed) => Events.Add($"finished:{cast.Spell.Id}:{completed}");
    }

    [Fact]
    public void Observer_ReceivesPrepared_Cast_Outcome_Finished_InOrder_ForHealing()
    {
        using var kit = Kit();
        (Player caster, _, _) = Duel(kit);
        caster.Health = 30;
        var observer = new RecordingObserver();
        kit.System.RegisterObserver(observer);

        kit.System.HandleCastRequest(caster, InstantHeal, SpellCastTargets.ForSelf());

        Assert.Equal([$"prepared:{InstantHeal}", $"cast:{InstantHeal}", $"outcome:{InstantHeal}", $"finished:{InstantHeal}:True"], observer.Events);
        SpellTargetOutcome outcome = Assert.Single(observer.Outcomes);
        Assert.Same(caster, outcome.Target);
        Assert.Equal(SpellMissInfo.None, outcome.Miss);
        Assert.Equal(20u, outcome.Healing);
        Assert.Equal(0u, outcome.Damage);
    }

    [Fact]
    public void Observer_ReceivesTargetOutcome_MissCritDamage()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        var rules = new FixedRules { Crit = true, Multiplier = 2.0f };
        kit.System.CombatRules = rules;
        var observer = new RecordingObserver();
        kit.System.RegisterObserver(observer);

        kit.System.CastSpell(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        SpellTargetOutcome crit = Assert.Single(observer.Outcomes);
        Assert.Equal(SpellMissInfo.None, crit.Miss);
        Assert.True(crit.Critical);
        Assert.Equal(30u, crit.Damage);   // 15 * 2.0
        Assert.Equal(1, crit.EffectMask & 1);

        observer.Outcomes.Clear();
        rules.Miss = SpellMissInfo.Dodge;
        rules.Crit = false;
        kit.System.CastSpell(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        SpellTargetOutcome miss = Assert.Single(observer.Outcomes);
        Assert.Equal(SpellMissInfo.Dodge, miss.Miss);
        Assert.Equal(0u, miss.Damage);
        Assert.False(miss.Critical);
    }

    [Fact]
    public void Observer_CancelledCast_IsFinishedWithCompletedFalse_AndNeverCast()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        var observer = new RecordingObserver();
        kit.System.RegisterObserver(observer);

        kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid));
        kit.System.CancelCast(caster, CastBolt);

        Assert.Equal([$"prepared:{CastBolt}", $"finished:{CastBolt}:False"], observer.Events);
    }

    [Fact]
    public void Observer_FailedLandingCheck_IsFinishedWithCompletedFalse()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        var observer = new RecordingObserver();
        kit.System.RegisterObserver(observer);

        kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid));
        SpellSystem.SetPower(caster, PowerType.Rage, 0);   // not enough rage when the cast lands
        kit.Advance(2000);

        Assert.Equal([$"prepared:{CastBolt}", $"finished:{CastBolt}:False"], observer.Events);
        Assert.Equal(60u, target.Health);
    }

    // --- value modifiers ---------------------------------------------------------------------------------

    private sealed class TestModifier : ISpellValueModifier
    {
        public List<SpellValueKind> Seen { get; } = [];

        public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
        {
            Seen.Add(kind);
            return kind switch
            {
                SpellValueKind.EffectValue => value + 5,
                SpellValueKind.Duration => value * 2,
                SpellValueKind.PowerCost => value > 0 ? value + 10 : value,
                SpellValueKind.CastTime => value / 2,
                _ => value,
            };
        }
    }

    [Fact]
    public void ValueModifier_SeamAdjustsEffectValueAndDurationAndCost()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        var modifier = new TestModifier();
        kit.System.RegisterValueModifier(modifier);

        // Effect value: heal 20 -> 25.
        caster.Health = 10;
        kit.System.HandleCastRequest(caster, InstantHeal, SpellCastTargets.ForSelf());
        Assert.Equal(35u, caster.Health);
        kit.Advance(1500);

        // Cast time 2000 -> 1000 and power cost 50 -> 60; the damage value 15 -> 20.
        kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid));
        Assert.Equal(1000, kit.System.GetState(caster.Guid)!.CurrentCast!.CastTime);
        kit.Advance(1000);
        Assert.Equal(40u, target.Health);
        Assert.Equal(40u, SpellSystem.GetPower(caster, PowerType.Rage));
        kit.Advance(1500);

        // Duration 12000 -> 24000 on the applied aura.
        kit.System.HandleCastRequest(caster, DotSpell, SpellCastTargets.ForUnit(target.Guid));
        Assert.Equal(24000, kit.System.GetState(target.Guid)!.AuraHolders.Single(h => h.Spell.Id == DotSpell).MaxDuration);

        Assert.Contains(SpellValueKind.EffectValue, modifier.Seen);
        Assert.Contains(SpellValueKind.CastTime, modifier.Seen);
        Assert.Contains(SpellValueKind.PowerCost, modifier.Seen);
        Assert.Contains(SpellValueKind.Duration, modifier.Seen);
    }

    [Fact]
    public void ValueModifier_NeverTouchesPermanentOrZeroDurations()
    {
        using var kit = Kit();
        (Player caster, _, _) = Duel(kit);
        kit.System.RegisterValueModifier(new TestModifier());

        // The passive aura has duration -1 (permanent): a modifier must see no Duration call for it.
        var spy = new TestModifier();
        kit.System.RegisterValueModifier(spy);
        kit.System.CastSpell(caster, Passive, SpellCastTargets.ForSelf(), triggered: true);

        Assert.DoesNotContain(SpellValueKind.Duration, spy.Seen);
        Assert.Equal(-1, kit.System.GetState(caster.Guid)!.AuraHolders.Single(h => h.Spell.Id == Passive).MaxDuration);
    }

    // --- next-swing melee slot ---------------------------------------------------------------------------

    [Fact]
    public void Prepare_NextSwing_QueuesWithoutCasting_SendsSpellStart_NoPowerTaken()
    {
        using var kit = Kit();
        (Player caster, FakeSession session, Player target) = Duel(kit);
        var observer = new RecordingObserver();
        kit.System.RegisterObserver(observer);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid)));

        UnitSpellState state = kit.System.GetState(caster.Guid)!;
        Assert.NotNull(state.MeleeCast);
        Assert.Equal(NextSwing, state.MeleeCast!.Spell.Id);
        Assert.Equal(SpellCastState.Preparing, state.MeleeCast.State);
        Assert.Null(state.CurrentCast);
        Assert.Equal([WorldOpcode.SmsgSpellStart], Opcodes(session));
        Assert.Equal(100u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.Equal(60u, target.Health);
        Assert.False(kit.System.IsSpellReady(caster, kit.Store.Get(InstantHeal)!));   // the global cooldown started (vmangos AddGCD)
        Assert.Equal([$"prepared:{NextSwing}"], observer.Events);
    }

    [Fact]
    public void QueuedMeleeSpell_IsNotCastByTheUpdateLoop()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));

        kit.Advance(5000);

        Assert.NotNull(kit.System.GetState(caster.Guid)!.MeleeCast);   // vmangos Spell::update skips IsNextMeleeSwingSpell (Spell.cpp:4104)
        Assert.Equal(60u, target.Health);
    }

    [Fact]
    public void CastQueuedMeleeSpell_TakesPower_DealsDamage_AndClearsTheSlot()
    {
        using var kit = Kit();
        (Player caster, FakeSession session, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));
        session.Clear();
        var observer = new RecordingObserver();
        kit.System.RegisterObserver(observer);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastQueuedMeleeSpell(caster, target));

        Assert.Equal(49u, target.Health);
        Assert.Equal(85u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.Null(kit.System.GetState(caster.Guid)?.MeleeCast);
        Assert.Contains(WorldOpcode.SmsgSpellGo, Opcodes(session));
        Assert.Equal([$"cast:{NextSwing}", $"outcome:{NextSwing}", $"finished:{NextSwing}:True"], observer.Events);
        Assert.Equal(11u, observer.Outcomes[0].Damage);
    }

    [Fact]
    public void CastQueuedMeleeSpell_WithNothingQueued_IsNotFound()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);

        Assert.Equal(SpellCastResult.NotFound, kit.System.CastQueuedMeleeSpell(caster, target));
        Assert.Equal(60u, target.Health);
    }

    [Fact]
    public void CastQueuedMeleeSpell_WithoutEnoughPower_FailsAndFreesTheSlot()
    {
        using var kit = Kit();
        (Player caster, FakeSession session, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));
        SpellSystem.SetPower(caster, PowerType.Rage, 5);
        session.Clear();

        Assert.Equal(SpellCastResult.NoPower, kit.System.CastQueuedMeleeSpell(caster, target));

        Assert.Null(kit.System.GetState(caster.Guid)?.MeleeCast);
        Assert.Equal(60u, target.Health);
        Assert.Equal(5u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.Equal((byte)SpellCastResult.NoPower, Packets(session, WorldOpcode.SmsgCastResult).Single()[5]);
    }

    [Fact]
    public void QueueingAnotherNextSwingSpell_InterruptsTheQueuedOne()
    {
        using var kit = Kit();
        (Player caster, FakeSession session, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1500);
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, NextSwingAlt, SpellCastTargets.ForUnit(target.Guid)));

        Assert.Equal(NextSwingAlt, kit.System.GetState(caster.Guid)!.MeleeCast!.Spell.Id);
        Assert.Contains(WorldOpcode.SmsgSpellFailedOther, Opcodes(session));   // vmangos Spell::cancel -> SendInterrupted
        Assert.Equal((byte)SpellCastResult.Interrupted, Packets(session, WorldOpcode.SmsgCastResult).Single()[5]);
    }

    [Fact]
    public void CancelCast_CancelsQueuedMeleeSpell_AndNotAChannel()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1500);
        kit.System.HandleCastRequest(caster, ChannelSpell, SpellCastTargets.ForSelf());
        UnitSpellState state = kit.System.GetState(caster.Guid)!;
        Assert.Equal(SpellCastState.Casting, state.CurrentCast!.State);

        kit.System.CancelCast(caster, 0);

        Assert.Null(state.MeleeCast);
        Assert.Equal(SpellCastState.Casting, state.CurrentCast!.State);   // CMSG_CANCEL_CAST does not stop a channel
    }

    [Fact]
    public void CancelCast_CancelsTheQueuedMeleeSpell_EvenForAnotherSpellId()
    {
        // vmangos HandleCancelCastOpcode (SpellHandler.cpp:329-330) interrupts the next-swing spell without a spell id filter.
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));

        kit.System.CancelCast(caster, 12345);

        Assert.Null(kit.System.GetState(caster.Guid)?.MeleeCast);
    }

    [Fact]
    public void CancelQueuedMeleeSpell_ReportsWhetherSomethingWasCancelled()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        Assert.False(kit.System.CancelQueuedMeleeSpell(caster));

        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));
        Assert.True(kit.System.CancelQueuedMeleeSpell(caster));
        Assert.False(kit.System.CancelQueuedMeleeSpell(caster));
    }

    [Fact]
    public void GenericCast_NeitherBlocksNorCancelsTheQueuedMeleeSpell()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1500);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));

        UnitSpellState state = kit.System.GetState(caster.Guid)!;
        Assert.Equal(CastBolt, state.CurrentCast!.Spell.Id);
        Assert.Equal(NextSwing, state.MeleeCast!.Spell.Id);
    }

    [Fact]
    public void TriggeredNextSwingSpell_CastsImmediately_WithoutQueueing()
    {
        // vmangos Spell::prepare adds only non-triggered (or channeled) spells to the current-spell slots (Spell.cpp:3480).
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid), triggered: true));

        Assert.Equal(49u, target.Health);
        Assert.Null(kit.System.GetState(caster.Guid)?.MeleeCast);
    }

    [Fact]
    public void RemoveUnit_DropsTheQueuedMeleeSpell_AndTellsObservers()
    {
        using var kit = Kit();
        (Player caster, _, Player target) = Duel(kit);
        kit.System.HandleCastRequest(caster, NextSwing, SpellCastTargets.ForUnit(target.Guid));
        var observer = new RecordingObserver();
        kit.System.RegisterObserver(observer);

        kit.System.RemoveUnit(caster);

        Assert.Null(kit.System.GetState(caster.Guid));
        Assert.Equal([$"finished:{NextSwing}:False"], observer.Events);
    }

    // --- chain range extension point --------------------------------------------------------------------

    private sealed class EffectRadiusChain : ISpellChainRangeProvider
    {
        public bool TryGetChainRange(SpellCast cast, SpellEffectInfo effect, out float range)
        {
            range = effect.Radius;
            return effect.Radius > 0;
        }
    }

    [Fact]
    public void ChainRangeProvider_ReplacesTheDefaultJumpRadius()
    {
        using var kit = Kit();
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        WorldCollision.Of(kit.World).Install(new FakeLineOfSight());
        (Player caster, _) = kit.AddPlayer(1);
        Player primary = Enemy(kit, relations, 2, 10);
        Player second = Enemy(kit, relations, 3, 14);   // 4 from the primary
        Player third = Enemy(kit, relations, 4, 22);    // 8 from the second
        kit.World.RunTick(0);

        kit.System.CastSpell(caster, ChainBolt, SpellCastTargets.ForUnit(primary.Guid), triggered: true);
        Assert.True(third.Health < 60u);   // default ChainJumpRadius 10: reaches the third

        third.Health = 60;
        second.Health = 60;
        primary.Health = 60;
        kit.System.RegisterChainRangeProvider(new EffectRadiusChain());
        kit.System.CastSpell(caster, ChainBolt, SpellCastTargets.ForUnit(primary.Guid), triggered: true);

        Assert.True(second.Health < 60u);    // within the effect radius (5)
        Assert.Equal(60u, third.Health);     // 8 yd away: out of the provider's radius
    }

    private static Player Enemy(SpellTestKit kit, FakeRelations relations, uint guid, float x)
    {
        (Player player, _) = kit.AddPlayer(guid, x, 0);
        relations.Hostile.Add(player.Guid);
        return player;
    }
}
