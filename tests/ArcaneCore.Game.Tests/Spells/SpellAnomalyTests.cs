using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// H1 outcome checks of the spell system, one fires/silent pair per key: <c>spell/effect-unhandled</c>, <c>spell/cast-stuck</c>
/// (three paths), <c>aura/duration-invalid</c>, <c>aura/stack-overflow</c>, <c>aura/periodic-stalled</c>, <c>aura/expired-not-removed</c>
/// (live sweep and login restore) and <c>proc/recursion-cut</c>. Each scenario owns a spell id: the ledger is process-wide, so every
/// capture filters on the subject <c>spell &lt;id&gt;</c>. Corrupt state is injected through the internal setters the checks exist to catch.
/// </summary>
public sealed class SpellAnomalyTests
{
    private const uint UnhandledEffectSpell = 947001;
    private const uint HandledEffectSpell = 947002;
    private const uint StuckNegativeTimer = 947010;
    private const uint StuckElapsed = 947011;
    private const uint StuckTerminal = 947012;
    private const uint HealthyCast = 947013;
    private const uint HealthyChannel = 947014;
    private const uint StuckBoundaryLow = 947015;
    private const uint StuckBoundaryHigh = 947016;
    private const uint DurationBad = 947020;
    private const uint DurationOk = 947021;
    private const uint StackBad = 947022;
    private const uint StackOk = 947023;
    private const uint StackMaxOk = 947024;
    private const uint StackMaxBad = 947025;
    private const uint PeriodicBad = 947026;
    private const uint PeriodicNegative = 947027;
    private const uint PeriodicOk = 947028;
    private const uint ExpiredBad = 947029;
    private const uint ExpiredOk = 947030;
    private const uint RemovedStillListed = 947050;
    private const uint RestoreBadMax = 947031;
    private const uint RestoreBadRemaining = 947032;
    private const uint RestoreOk = 947033;
    private const uint RestoreBadTimer = 947034;
    private const uint RestoreBadStacks = 947035;
    private const uint RestoreMaxStacksOk = 947036;
    private const uint ProcSpellCut = 947040;
    private const uint ProcSpellOk = 947041;
    private const uint PeriodicRestarted = 947042;
    private const long Saved = 1_800_000_000_000;

    private static SpellInfo Instant(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Timed(uint id, uint stacks = 0, params SpellEffectInfo[] effects) => Spell(id, effects) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        StackAmount = stacks,
    };

    private static SpellInfo SelfHot(uint id, uint stacks = 0)
        => Timed(id, stacks, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.PeriodicHeal, amplitude: 1000));

    private static SpellInfo SlowCast(uint id) => Spell(id, Effect(SpellEffectName.Heal, 20)) with
    {
        CastTime = new SpellCastTime(2000, 0, 0),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo SelfChannel(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        AttributesEx = SpellAttributesEx.IsChanneled,
        Duration = new SpellDuration(5000, 0, 5000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit Kit() => new(
        Instant(UnhandledEffectSpell, Effect(SpellEffectName.Dodge, 0)),
        Instant(HandledEffectSpell, Effect(SpellEffectName.Heal, 20)),
        SlowCast(StuckNegativeTimer), SlowCast(StuckElapsed), SlowCast(StuckTerminal), SlowCast(HealthyCast),
        SlowCast(StuckBoundaryLow), SlowCast(StuckBoundaryHigh),
        SelfChannel(HealthyChannel),
        SelfHot(DurationBad), SelfHot(DurationOk), SelfHot(StackBad), SelfHot(StackOk),
        SelfHot(StackMaxOk, stacks: 3), SelfHot(StackMaxBad, stacks: 3),
        SelfHot(PeriodicBad), SelfHot(PeriodicNegative), SelfHot(PeriodicOk), SelfHot(ExpiredBad), SelfHot(ExpiredOk), SelfHot(RemovedStillListed),
        SelfHot(RestoreBadMax), SelfHot(RestoreBadRemaining), SelfHot(RestoreOk), SelfHot(RestoreBadTimer), SelfHot(RestoreBadStacks),
        SelfHot(RestoreMaxStacksOk, stacks: 3),
        Instant(ProcSpellCut, Effect(SpellEffectName.Heal, 1)), Instant(ProcSpellOk, Effect(SpellEffectName.Heal, 1)), SelfHot(PeriodicRestarted));

    private static AnomalyCapture CaptureFor(uint spellId, string key)
        => AnomalyLedger.Shared.Capture(n => n.Key == key && n.Subject == $"spell {spellId}");

    private static Player Caster(SpellTestKit kit, params uint[] spells)
    {
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, spells);
        kit.World.RunTick(0);
        return player;
    }

    private static SpellCast BeginCast(SpellTestKit kit, Player player, uint spell)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, spell, SpellCastTargets.ForSelf()));
        return Assert.IsType<SpellCast>(kit.System.GetState(player.Guid)?.CurrentCast);
    }

    private static SpellAuraHolder CastAura(SpellTestKit kit, Player player, uint spell)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, spell, SpellCastTargets.ForSelf()));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)
        return Assert.Single(kit.System.GetAuras(player), h => !h.IsRemoved && h.Spell.Id == spell);
    }

    // --- spell/effect-unhandled ---------------------------------------------------------------------------

    [Fact]
    public void EffectWithoutAHandler_ReportsEffectUnhandled_ForThatSpell()
    {
        using SpellTestKit kit = Kit();
        Assert.False(kit.System.HasEffectHandler(SpellEffectName.Dodge), "the scenario needs an effect nothing in the game assembly handles");
        Player player = Caster(kit, UnhandledEffectSpell);
        using AnomalyCapture capture = CaptureFor(UnhandledEffectSpell, "spell/effect-unhandled");

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, UnhandledEffectSpell, SpellCastTargets.ForSelf()));
        kit.Advance(Math.Max(100, kit.System.SpellBatchWindowMs)); // with spell batching on, the held go resolves at the next boundary

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains($"kind=effect value={(uint)SpellEffectName.Dodge}", notice.Detail);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
    }

    [Fact]
    public void EffectWithAHandler_ReportsNothing()
    {
        using SpellTestKit kit = Kit();
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Heal));
        Player player = Caster(kit, HandledEffectSpell);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject == $"spell {HandledEffectSpell}");

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, HandledEffectSpell, SpellCastTargets.ForSelf()));
        kit.Advance(100);

        Assert.Empty(capture.Notices);
    }

    // --- spell/cast-stuck ---------------------------------------------------------------------------------

    [Fact]
    public void ACastWithANegativeTimer_ReportsCastStuck_OncePerCast()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, StuckNegativeTimer);
        SpellCast cast = BeginCast(kit, player, StuckNegativeTimer);
        using AnomalyCapture capture = CaptureFor(StuckNegativeTimer, "spell/cast-stuck");

        cast.Timer = -1;
        kit.Advance(100);
        kit.Advance(100);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("reason=negative-timer", notice.Detail);
    }

    [Fact]
    public void ACastPastItsTimeBudget_ReportsCastStuck_ButIsNotCancelled()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, StuckElapsed);
        SpellCast cast = BeginCast(kit, player, StuckElapsed);
        using AnomalyCapture capture = CaptureFor(StuckElapsed, "spell/cast-stuck");

        cast.ElapsedMs = 1_000_000;
        kit.Advance(100);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("maxAllowedMs=12000", notice.Detail); // CastTime 2000 + CastStuckMarginMs 10000
        Assert.Same(cast, kit.System.GetState(player.Guid)?.CurrentCast);
        Assert.Equal(SpellCastState.Preparing, cast.State);

        kit.Advance(100);
        Assert.Single(capture.Notices); // once per cast, not once per sweep
    }

    [Fact]
    public void ACastLeftInATerminalState_ReportsCastStuck_AndTheSweepClearsIt()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, StuckTerminal);
        SpellCast cast = BeginCast(kit, player, StuckTerminal);
        using AnomalyCapture capture = CaptureFor(StuckTerminal, "spell/cast-stuck");

        cast.State = SpellCastState.Finished;
        kit.Advance(100);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("reason=terminal-cast-not-cleared", notice.Detail);
        Assert.Null(kit.System.GetState(player.Guid)?.CurrentCast);
    }

    [Fact]
    public void ACastThatRunsToCompletion_ReportsNoCastStuck()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, HealthyCast);
        SpellCast cast = BeginCast(kit, player, HealthyCast);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject == $"spell {HealthyCast}");

        kit.Advance(2500);

        Assert.Equal(SpellCastState.Finished, cast.State);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AChannelRunningItsFullDuration_ReportsNothing()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, HealthyChannel);
        BeginCast(kit, player, HealthyChannel);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject == $"spell {HealthyChannel}");

        kit.Advance(500);
        Assert.Equal(SpellCastState.Casting, kit.System.GetState(player.Guid)?.CurrentCast?.State);
        kit.Advance(4000);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void TheCastTimeBudget_IsInclusiveAtItsLimit_AndReportsOneMillisecondPastIt()
    {
        // Preparing budget = CastTime (2000) + PushbackCount * 1000 + CastStuckMarginMs (10000) = 12000; the check is ElapsedMs > budget.
        // A 100 ms step adds its diff to ElapsedMs before the sweep.
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, StuckBoundaryLow, StuckBoundaryHigh);
        kit.World.RunTick(0);

        SpellCast atLimit = BeginCast(kit, player, StuckBoundaryLow);
        using (AnomalyCapture low = CaptureFor(StuckBoundaryLow, "spell/cast-stuck"))
        {
            atLimit.ElapsedMs = 12_000 - 100;
            kit.Advance(100);
            Assert.Empty(low.Notices);
        }

        kit.System.CancelCast(player, StuckBoundaryLow);
        kit.Advance(100);

        SpellCast pastLimit = BeginCast(kit, player, StuckBoundaryHigh);
        using AnomalyCapture high = CaptureFor(StuckBoundaryHigh, "spell/cast-stuck");
        pastLimit.ElapsedMs = 12_000 - 99;
        kit.Advance(100);
        Assert.Single(high.Notices);
    }

    // --- aura/duration-invalid ----------------------------------------------------------------------------

    [Fact]
    public void AnAuraWithMoreTimeThanItsMax_ReportsDurationInvalid_OnceAndIsClamped()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, DurationBad);
        SpellAuraHolder holder = CastAura(kit, player, DurationBad);
        using AnomalyCapture capture = CaptureFor(DurationBad, "aura/duration-invalid");

        holder.Duration = holder.MaxDuration + 5_000;
        kit.Advance(100);
        kit.Advance(100);

        Assert.Single(capture.Notices);
        Assert.True(holder.Duration <= holder.MaxDuration, $"duration {holder.Duration} was not clamped to {holder.MaxDuration}");
    }

    [Fact]
    public void AHealthyAura_ReportsNoDurationInvalid_StackOverflow_OrPeriodicStall()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0; // assumes same-call resolution: the aura's exact duration and first-tick timer count from the cast call
        Player player = Caster(kit, DurationOk);
        SpellAuraHolder holder = CastAura(kit, player, DurationOk);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject == $"spell {DurationOk}");

        kit.Advance(5_000);

        Assert.Empty(capture.Notices);
        Assert.Equal(55_000, holder.Duration);
        Assert.True(Assert.Single(holder.AuraSpan.ToArray(), a => a is not null)!.TickCount > 0, "the periodic aura never ticked, so the silence proves nothing");
    }

    // --- aura/stack-overflow ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void AnAuraWithAnImpossibleStackCount_ReportsStackOverflow_AndIsClamped(int stacks)
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, StackBad);
        SpellAuraHolder holder = CastAura(kit, player, StackBad);
        using AnomalyCapture capture = CaptureFor(StackBad, "aura/stack-overflow");

        holder.StackAmount = (byte)stacks;
        kit.Advance(100);
        kit.Advance(100);

        Assert.Single(capture.Notices);
        Assert.Equal(1, holder.StackAmount); // a spell with StackAmount 0 holds one stack
    }

    [Fact]
    public void AnAuraAtItsStackLimit_ReportsNothing_AndOneAboveIt_Reports()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, StackMaxOk, StackMaxBad);
        kit.World.RunTick(0);
        SpellAuraHolder atLimit = CastAura(kit, player, StackMaxOk);
        SpellAuraHolder overLimit = CastAura(kit, player, StackMaxBad);
        using AnomalyCapture ok = CaptureFor(StackMaxOk, "aura/stack-overflow");
        using AnomalyCapture bad = CaptureFor(StackMaxBad, "aura/stack-overflow");

        atLimit.StackAmount = 3;
        overLimit.StackAmount = 4;
        kit.Advance(100);

        Assert.Empty(ok.Notices);
        Assert.Single(bad.Notices);
        Assert.Equal(3, overLimit.StackAmount);
    }

    [Fact]
    public void AHealthyStackCount_ReportsNothing()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, StackOk);
        CastAura(kit, player, StackOk);
        using AnomalyCapture capture = CaptureFor(StackOk, "aura/stack-overflow");

        kit.Advance(1_000);

        Assert.Empty(capture.Notices);
    }

    // --- aura/periodic-stalled ----------------------------------------------------------------------------

    [Theory]
    [InlineData(99_999)]
    [InlineData(-99_999)]
    public void APeriodicTimerBeyondItsPeriod_ReportsPeriodicStalled_OnceAndIsReset(int timer)
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, PeriodicBad);
        SpellAuraHolder holder = CastAura(kit, player, PeriodicBad);
        SpellAura aura = Assert.Single(holder.AuraSpan.ToArray(), a => a is not null)!;
        using AnomalyCapture capture = CaptureFor(PeriodicBad, "aura/periodic-stalled");

        aura.PeriodicTimer = timer;
        kit.Advance(100);
        kit.Advance(100);

        Assert.Single(capture.Notices);
        Assert.InRange(aura.PeriodicTimer, -(int)aura.MaximumPeriodicTimer, (int)aura.MaximumPeriodicTimer);
    }

    [Fact]
    public void ATimerAtExactlyItsPeriod_AndANormallyTickingAura_ReportNoPeriodicStall()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0; // assumes same-call resolution: the aura's exact duration and first-tick timer count from the cast call
        Player player = Caster(kit, PeriodicOk);
        SpellAuraHolder holder = CastAura(kit, player, PeriodicOk);
        SpellAura aura = Assert.Single(holder.AuraSpan.ToArray(), a => a is not null)!;
        using AnomalyCapture capture = CaptureFor(PeriodicOk, "aura/periodic-stalled");

        Assert.Equal((int)aura.MaximumPeriodicTimer, aura.PeriodicTimer); // a fresh aura starts at exactly the limit
        kit.Advance(10_000);

        Assert.Empty(capture.Notices);
        Assert.True(aura.TickCount >= 9, $"ticks: {aura.TickCount}");
    }

    /// <summary>
    /// The live world reported Ignite (12654) as stalled after a refresh: RestartHolderTicks puts a fresh full-period timer on the aura, so the
    /// time since the last tick may pass one period once without a stall. The watch restarts with the timer.
    /// </summary>
    [Fact]
    public void APeriodicAuraWhoseTicksRestart_IsNotReportedStalled()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, PeriodicRestarted);
        SpellAuraHolder holder = CastAura(kit, player, PeriodicRestarted);
        SpellAura aura = Assert.Single(holder.AuraSpan.ToArray(), a => a is not null)!;
        using AnomalyCapture capture = CaptureFor(PeriodicRestarted, "aura/periodic-stalled");

        kit.Advance(900);
        kit.System.RestartHolderTicks(holder);
        kit.Advance(900);
        kit.System.RestartHolderTicks(holder);
        kit.Advance(3_000);

        Assert.Empty(capture.Notices);
        Assert.True(aura.TickCount >= 2, $"ticks: {aura.TickCount}");
    }

    // --- aura/expired-not-removed -------------------------------------------------------------------------

    [Fact]
    public void ATimedAuraAtZeroDuration_ThatIsStillAttached_ReportsExpiredNotRemoved_OnceAndIsRemoved()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, ExpiredBad);
        SpellAuraHolder holder = CastAura(kit, player, ExpiredBad);
        using AnomalyCapture capture = CaptureFor(ExpiredBad, "aura/expired-not-removed");

        holder.Duration = 0;
        kit.Advance(100);
        kit.Advance(100);

        Assert.Single(capture.Notices);
        Assert.True(holder.IsRemoved);
        Assert.DoesNotContain(holder, kit.System.GetAuras(player));
    }

    [Fact]
    public void ARemovedHolderLeftInTheAuraList_ReportsExpiredNotRemoved_AndIsDropped()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, RemovedStillListed);
        SpellAuraHolder holder = CastAura(kit, player, RemovedStillListed);
        using AnomalyCapture capture = CaptureFor(RemovedStillListed, "aura/expired-not-removed");

        holder.IsRemoved = true; // flagged removed but never taken out of the unit's list
        kit.Advance(100);
        kit.Advance(100);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("isRemoved=true", notice.Detail);
        Assert.DoesNotContain(holder, kit.System.GetAuras(player));
    }

    [Fact]
    public void AnAuraThatExpiresNaturally_IsRemovedWithoutAReport()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, ExpiredOk);
        SpellAuraHolder holder = CastAura(kit, player, ExpiredOk);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject == $"spell {ExpiredOk}");

        kit.Advance(61_000);

        Assert.True(holder.IsRemoved);
        Assert.Empty(capture.Notices);
    }

    // --- login restore (SpellSystem.Persistence) ----------------------------------------------------------

    private static PersistedAura Persisted(uint spell, ObjectGuid caster, int max = 60_000, int remaining = 40_000, byte stacks = 1, int timer = 1000) => new()
    {
        SpellId = spell,
        CasterGuid = caster,
        CasterLevel = 60,
        StackAmount = stacks,
        MaxDurationMs = max,
        RemainingMs = remaining,
        EffectMask = 0b001,
        Amounts = [5, 0, 0],
        PeriodicTimers = [timer, 0, 0],
        SavedAtUnixMs = Saved,
    };

    [Theory]
    [InlineData(RestoreBadMax, 0, 40_000)]
    [InlineData(RestoreBadRemaining, 60_000, 60_001)]
    public void RestoringAnAuraWithInconsistentDurations_ReportsDurationInvalid(uint spell, int max, int remaining)
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        using AnomalyCapture capture = CaptureFor(spell, "aura/duration-invalid");

        kit.System.RestoreAuras(player, [Persisted(spell, player.Guid, max, remaining)], Saved + 1_000);

        Assert.Single(capture.Notices);
    }

    [Fact]
    public void RestoringAConsistentAura_ReportsNothing_AtItsBoundaries()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject == $"spell {RestoreOk}" || n.Subject == $"spell {RestoreMaxStacksOk}");

        // remaining == max, timer == period, stacks == limit are all valid.
        kit.System.RestoreAuras(player, [Persisted(RestoreOk, player.Guid, 60_000, 60_000, 1, 1000)], Saved + 1_000);
        kit.System.RestoreAuras(player, [Persisted(RestoreMaxStacksOk, player.Guid, 60_000, 30_000, 3, 1)], Saved + 1_000);

        Assert.Empty(capture.Notices);
        Assert.Equal(2, kit.System.GetAuras(player).Count(h => h.Spell.Id is RestoreOk or RestoreMaxStacksOk));
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(-1)]
    public void RestoringAPeriodicTimerOutsideItsPeriod_ReportsPeriodicStalled(int timer)
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        using AnomalyCapture capture = CaptureFor(RestoreBadTimer, "aura/periodic-stalled");

        kit.System.RestoreAuras(player, [Persisted(RestoreBadTimer, player.Guid, timer: timer)], Saved + 1_000);

        Assert.Single(capture.Notices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RestoringAZeroStackAuraOnAOneStackSpell_ReportsOnlyZero(byte stacks)
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        using AnomalyCapture capture = CaptureFor(RestoreBadStacks, "aura/stack-overflow");

        kit.System.RestoreAuras(player, [Persisted(RestoreBadStacks, player.Guid, stacks: stacks)], Saved + 1_000);

        Assert.Equal(stacks == 0 ? 1 : 0, capture.Notices.Count);
    }

    // --- proc/recursion-cut -------------------------------------------------------------------------------

    private static void SetProcDepth(SpellSystem system, int depth)
        => typeof(SpellSystem).GetField("_procDepth", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(system, depth);

    private static ProcEvent HitWith(SpellTestKit kit, Player victim, uint spellId) => new()
    {
        Victim = victim,
        AttackerFlags = ProcFlags.DealHarmfulSpell,
        VictimFlags = ProcFlags.TakeHarmfulSpell,
        Extra = ProcFlagsEx.NormalHit,
        Amount = 10,
        OriginalAmount = 10,
        ProcSpell = kit.Store.Get(spellId),
    };

    [Fact]
    public void AProcEventAtTheDepthLimit_IsCutAndReportsRecursionCut()
    {
        using SpellTestKit kit = Kit();
        (Player actor, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        using AnomalyCapture capture = CaptureFor(ProcSpellCut, "proc/recursion-cut");

        SetProcDepth(kit.System, SpellSystem.MaxProcDepth);
        kit.System.ProcDamageAndSpell(actor, HitWith(kit, victim, ProcSpellCut));

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains($"procDepth={SpellSystem.MaxProcDepth} maxProcDepth={SpellSystem.MaxProcDepth}", notice.Detail);
    }

    [Fact]
    public void AProcEventBelowTheDepthLimit_RunsWithoutAReport()
    {
        using SpellTestKit kit = Kit();
        (Player actor, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        using AnomalyCapture capture = CaptureFor(ProcSpellOk, "proc/recursion-cut");

        SetProcDepth(kit.System, SpellSystem.MaxProcDepth - 1);
        kit.System.ProcDamageAndSpell(actor, HitWith(kit, victim, ProcSpellOk));

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ACorruptNegativeProcDepth_ReportsRecursionCut_AndIsResetToZero()
    {
        using SpellTestKit kit = Kit();
        (Player actor, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        using AnomalyCapture capture = CaptureFor(ProcSpellCut, "proc/recursion-cut");

        SetProcDepth(kit.System, -1);
        kit.System.ProcDamageAndSpell(actor, HitWith(kit, victim, ProcSpellCut));

        Assert.Single(capture.Notices);
        Assert.Equal(0, (int)typeof(SpellSystem).GetField("_procDepth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kit.System)!);
    }
}
