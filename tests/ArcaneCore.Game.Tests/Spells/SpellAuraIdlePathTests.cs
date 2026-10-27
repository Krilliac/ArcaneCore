using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The idle-aura fast path of <c>SpellSystem.UpdateAuras</c>: a unit whose holders are all permanent passives with nothing to tick
/// does no snapshot work, while timed, periodic and area holders still tick and expire exactly as before, and the
/// <c>aura/holders-excess</c> anomaly and the max-count counter watch for a holder leak.
/// </summary>
public sealed class SpellAuraIdlePathTests
{
    private const uint PassiveA = 961001;
    private const uint PassiveB = 961002;
    private const uint PassiveC = 961003;
    private const uint Hot = 961010;
    private const uint Timed3s = 961011;
    private const uint Timed5s = 961012;
    private const uint StandingPassive = 961014;
    private const uint NoPeriodPassive = 961015;
    private const uint StackPassive = 961016;
    private const uint FirstBulkPassive = 962000;
    private const int BulkPassives = 520;

    private static SpellInfo Passive(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 3, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo TimedDummy(uint id, int ms) => Spell(id, Effect(SpellEffectName.ApplyAura, 3, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(ms, 0, ms),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo SelfHot(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.PeriodicHeal, amplitude: 1000)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit Kit(int bulk = 0)
    {
        var spells = new List<SpellInfo>
        {
            Passive(PassiveA), Passive(PassiveB), Passive(PassiveC), SelfHot(Hot), TimedDummy(Timed3s, 3000), TimedDummy(Timed5s, 5000),
            Passive(StandingPassive) with { AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels },
            Spell(NoPeriodPassive, Effect(SpellEffectName.ApplyAura, 3, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage)) with { Attributes = SpellAttributes.Passive, StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Passive(StackPassive),
        };
        for (int i = 0; i < bulk; i++)
        {
            spells.Add(Passive(FirstBulkPassive + (uint)i));
        }

        return new SpellTestKit(spells.ToArray());
    }

    private static Player AddUnit(SpellTestKit kit, uint guid = 1)
    {
        (Player player, _) = kit.AddPlayer(guid);
        kit.World.RunTick(0);
        return player;
    }

    private static void CastSelf(SpellTestKit kit, Player player, uint spell)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));
        kit.Advance(kit.System.SpellBatchWindowMs);
    }

    // --- 1: a passive-only unit does no snapshot work -------------------------------------------------------

    [Fact]
    public void AUnitWithOnlyPermanentPassives_RentsNoSnapshot_AndIsCountedAsIdleSkips()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, PassiveA);
        CastSelf(kit, player, PassiveB);
        CastSelf(kit, player, PassiveC);
        UnitSpellState state = Assert.IsType<UnitSpellState>(kit.System.GetState(player.Guid));
        Assert.Equal(3, state.Auras.Count(h => h.IsPermanent && !h.IsRemoved));

        long rentsBefore = kit.System.AuraSnapshotRents;
        long skipsBefore = kit.System.AuraIdleSkips;
        kit.Advance(1000);

        Assert.Equal(rentsBefore, kit.System.AuraSnapshotRents);
        Assert.True(kit.System.AuraIdleSkips >= skipsBefore + 10, $"idle skips {kit.System.AuraIdleSkips - skipsBefore}");
        Assert.False(state.HasTickingHolders);
        Assert.Equal(3, state.Auras.Count);
    }

    [Fact]
    public void AUnitWithAPassiveAndAPeriodicAura_StillRentsASnapshotEveryTick()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, PassiveA);
        CastSelf(kit, player, Hot);
        long rentsBefore = kit.System.AuraSnapshotRents;

        kit.Advance(1000);

        Assert.True(kit.System.AuraSnapshotRents >= rentsBefore + 10, $"rents {kit.System.AuraSnapshotRents - rentsBefore}");
        Assert.True(kit.System.GetState(player.Guid)!.HasTickingHolders);
    }

    // --- 2: golden tick and expiry sequence -----------------------------------------------------------------

    /// <summary>
    /// A passive, a periodic heal, a 3 s and a 5 s aura on one unit, advanced in 100 ms steps. The trace (captured on the code before the
    /// fast path, same scenario) is: per step, the heal's tick count, the two timed holders' remaining ms ("-" once gone) and the holder count.
    /// </summary>
    [Fact]
    public void MixedPassivePeriodicAndTimedAuras_GiveTheSameTickAndExpirySequence()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, PassiveA);
        CastSelf(kit, player, Hot);
        CastSelf(kit, player, Timed3s);
        CastSelf(kit, player, Timed5s);
        SpellAuraHolder hot = kit.System.GetAuras(player).Single(h => h.Spell.Id == Hot);
        var trace = new StringBuilder();
        for (int step = 1; step <= 60; step++)
        {
            kit.Advance(100);
            trace.Append(step % 10 == 0 ? "|" : ",");
            trace.Append(hot.AuraSpan[0]!.TickCount).Append(':');
            trace.Append(Remaining(kit, player, Timed3s)).Append(':').Append(Remaining(kit, player, Timed5s)).Append(':').Append(kit.System.GetAuras(player).Count);
        }

        Assert.Equal(GoldenTrace, trace.ToString());
    }

    private static string Remaining(SpellTestKit kit, Player player, uint spell)
        => kit.System.GetAuras(player).SingleOrDefault(h => h.Spell.Id == spell) is { } holder ? holder.Duration.ToString() : "-";

    private const string GoldenTrace = ",0:2900:4900:4,0:2800:4800:4,0:2700:4700:4,0:2600:4600:4,0:2500:4500:4,0:2400:4400:4,0:2300:4300:4,0:2200:4200:4,0:2100:4100:4|1:2000:4000:4,1:1900:3900:4,1:1800:3800:4,1:1700:3700:4,1:1600:3600:4,1:1500:3500:4,1:1400:3400:4,1:1300:3300:4,1:1200:3200:4,1:1100:3100:4|2:1000:3000:4,2:900:2900:4,2:800:2800:4,2:700:2700:4,2:600:2600:4,2:500:2500:4,2:400:2400:4,2:300:2300:4,2:200:2200:4,2:100:2100:4|3:-:2000:3,3:-:1900:3,3:-:1800:3,3:-:1700:3,3:-:1600:3,3:-:1500:3,3:-:1400:3,3:-:1300:3,3:-:1200:3,3:-:1100:3|4:-:1000:3,4:-:900:3,4:-:800:3,4:-:700:3,4:-:600:3,4:-:500:3,4:-:400:3,4:-:300:3,4:-:200:3,4:-:100:3|5:-:-:2,5:-:-:2,5:-:-:2,5:-:-:2,5:-:-:2,5:-:-:2,5:-:-:2,5:-:-:2,5:-:-:2,5:-:-:2|6:-:-:2";

    // --- 3: the ticking count follows add, remove, refresh, dispel and expiry --------------------------------

    [Fact]
    public void TheTickingCount_FollowsAddRemoveRefreshDispelAndExpiry()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, PassiveA);
        UnitSpellState state = kit.System.GetState(player.Guid)!;
        kit.Advance(100);
        Assert.False(state.HasTickingHolders);
        Assert.False(SpellSystem.AnyTickingHolder(state));

        // add: a periodic and a timed aura turn the count on
        CastSelf(kit, player, Hot);
        CastSelf(kit, player, Timed3s);
        kit.Advance(100);
        Assert.True(state.HasTickingHolders);
        Assert.True(SpellSystem.AnyTickingHolder(state));

        // refresh: the same holder keeps ticking, the count is unchanged
        SpellAuraHolder timed = state.Auras.Single(h => h.Spell.Id == Timed3s);
        CastSelf(kit, player, Timed3s);
        kit.Advance(100);
        Assert.Same(timed, state.Auras.Single(h => h.Spell.Id == Timed3s));
        Assert.True(state.HasTickingHolders);

        // remove: dropping the periodic aura leaves the timed one
        kit.System.RemoveAuras(player, Hot);
        kit.Advance(100);
        Assert.True(state.HasTickingHolders);

        // dispel: removing with the dispel mode leaves only the passive, so the unit goes idle
        kit.System.RemoveAuras(player, Timed3s, AuraRemoveMode.Dispel);
        long rents = kit.System.AuraSnapshotRents;
        kit.Advance(300);
        Assert.False(state.HasTickingHolders);
        Assert.Equal(rents, kit.System.AuraSnapshotRents);

        // expiry: a timed aura added again ticks down, expires on schedule and the count returns to 0
        CastSelf(kit, player, Timed3s);
        kit.Advance(100);
        Assert.True(state.HasTickingHolders);
        kit.Advance(3000);
        Assert.DoesNotContain(state.Auras, h => h.Spell.Id == Timed3s);
        kit.Advance(100);
        Assert.False(state.HasTickingHolders);
        Assert.Single(state.Auras);
    }

    [Fact]
    public void ARemovedHolderStillListed_CountsAsTicking_SoTheCleanupStillRuns()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, PassiveA);
        UnitSpellState state = kit.System.GetState(player.Guid)!;
        SpellAuraHolder passive = state.Auras.Single();

        passive.IsRemoved = true;
        Assert.True(SpellSystem.AnyTickingHolder(state));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "aura/expired-not-removed" && n.Subject == $"spell {PassiveA}");
        kit.Advance(100);

        Assert.Single(capture.Notices);
        Assert.DoesNotContain(passive, state.Auras);
    }

    // --- 4: the excess anomaly and the max-count counter ----------------------------------------------------

    [Fact]
    public void MoreHoldersThanTheBound_ReportsHoldersExcess_OnceAndTracksTheMax()
    {
        using SpellTestKit kit = Kit(BulkPassives);
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit, 77);
        for (int i = 0; i < BulkPassives; i++)
        {
            CastSelf(kit, player, FirstBulkPassive + (uint)i);
        }

        UnitSpellState state = kit.System.GetState(player.Guid)!;
        Assert.True(state.Auras.Count > SpellSystem.AuraHoldersExcessBound, $"holders {state.Auras.Count}");
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "aura/holders-excess" && n.Subject.Contains("77", StringComparison.Ordinal));

        kit.Advance(300);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains($"holders={state.Auras.Count}", notice.Detail);
        Assert.Equal(state.Auras.Count, kit.System.MaxAuraCountSeen);
    }

    [Fact]
    public void ABoundedHolderCount_ReportsNoHoldersExcess()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit, 78);
        CastSelf(kit, player, PassiveA);
        CastSelf(kit, player, PassiveB);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "aura/holders-excess" && n.Subject.Contains("78", StringComparison.Ordinal));

        kit.Advance(300);

        Assert.Empty(capture.Notices);
        Assert.Equal(2, kit.System.MaxAuraCountSeen);
    }

    // --- stack validation, phased golden trace, re-arm, and every ticking reason ---------------------------

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    public void APassiveWithAZeroOrOverMaxStack_IsRepairedAndReportedOnce_EvenOnAnOtherwiseIdleUnit(byte badStack)
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, StackPassive);
        UnitSpellState state = kit.System.GetState(player.Guid)!;
        SpellAuraHolder holder = state.Auras.Single();
        kit.Advance(200);
        Assert.False(state.HasTickingHolders);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "aura/stack-overflow" && n.Subject == $"spell {StackPassive}");

        holder.StackAmount = badStack;
        Assert.True(SpellSystem.HolderNeedsTick(holder));
        kit.Advance(300);

        Assert.Single(capture.Notices);
        Assert.Equal(1, holder.StackAmount);
        Assert.False(SpellSystem.HolderNeedsTick(holder));
        Assert.False(state.HasTickingHolders);
    }

    /// <summary>One run of the phased scenario: idle, timed aura added, expired (idle again), periodic aura added. Returns the trace and the snapshot counters.</summary>
    private static (string Trace, long Rents, long Skips, int PassiveAnomalies) RunPhases(bool fastPath)
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        kit.System.AuraIdleFastPathEnabled = fastPath;
        Player player = AddUnit(kit);
        CastSelf(kit, player, PassiveA);
        SpellAuraHolder passive = kit.System.GetAuras(player).Single();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject == $"spell {PassiveA}");
        var trace = new StringBuilder();
        void Steps(string phase, int steps)
        {
            trace.Append('[').Append(phase).Append(']');
            for (int i = 0; i < steps; i++)
            {
                kit.Advance(100);
                SpellAuraHolder? hot = kit.System.GetAuras(player).SingleOrDefault(h => h.Spell.Id == Hot);
                trace.Append(hot is null ? "-" : hot.AuraSpan[0]!.TickCount.ToString()).Append(':')
                    .Append(Remaining(kit, player, Timed3s)).Append(':').Append(kit.System.GetAuras(player).Count).Append(':')
                    .Append(passive.Duration).Append('/').Append(passive.MaxDuration).Append('/').Append(passive.StackAmount).Append(',');
            }
        }

        Steps("idle", 10);
        CastSelf(kit, player, Timed3s);
        Steps("timed", 35);
        Steps("idle2", 10);
        CastSelf(kit, player, Hot);
        Steps("periodic", 25);
        return (trace.ToString(), kit.System.AuraSnapshotRents, kit.System.AuraIdleSkips, capture.Notices.Count);
    }

    [Fact]
    public void PhasedIdleTimedIdlePeriodic_GiveTheSameTraceWithAndWithoutTheFastPath()
    {
        (string slow, long slowRents, long slowSkips, int slowAnomalies) = RunPhases(fastPath: false);
        (string fast, long fastRents, long fastSkips, int fastAnomalies) = RunPhases(fastPath: true);

        Assert.True(GoldenPhased == slow, "golden no longer matches the always-snapshot run: " + slow);
        Assert.Equal(slow, fast);
        Assert.Equal(0, slowSkips);
        Assert.Equal(0, slowAnomalies);
        Assert.Equal(0, fastAnomalies);
        Assert.True(fastSkips >= 20, $"idle skips {fastSkips}");
        Assert.True(fastRents < slowRents, $"rents fast {fastRents} slow {slowRents}");
    }

    // GoldenPhased was captured by running RunPhases(fastPath: false) (AuraIdleFastPathEnabled = false: the original always-snapshot behaviour).
    private const string GoldenPhased = "[idle]-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,[timed]-:2900:2:-1/-1/1,-:2800:2:-1/-1/1,-:2700:2:-1/-1/1,-:2600:2:-1/-1/1,-:2500:2:-1/-1/1,-:2400:2:-1/-1/1,-:2300:2:-1/-1/1,-:2200:2:-1/-1/1,-:2100:2:-1/-1/1,-:2000:2:-1/-1/1,-:1900:2:-1/-1/1,-:1800:2:-1/-1/1,-:1700:2:-1/-1/1,-:1600:2:-1/-1/1,-:1500:2:-1/-1/1,-:1400:2:-1/-1/1,-:1300:2:-1/-1/1,-:1200:2:-1/-1/1,-:1100:2:-1/-1/1,-:1000:2:-1/-1/1,-:900:2:-1/-1/1,-:800:2:-1/-1/1,-:700:2:-1/-1/1,-:600:2:-1/-1/1,-:500:2:-1/-1/1,-:400:2:-1/-1/1,-:300:2:-1/-1/1,-:200:2:-1/-1/1,-:100:2:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,[idle2]-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,-:-:1:-1/-1/1,[periodic]0:-:2:-1/-1/1,0:-:2:-1/-1/1,0:-:2:-1/-1/1,0:-:2:-1/-1/1,0:-:2:-1/-1/1,0:-:2:-1/-1/1,0:-:2:-1/-1/1,0:-:2:-1/-1/1,0:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,1:-:2:-1/-1/1,2:-:2:-1/-1/1,2:-:2:-1/-1/1,2:-:2:-1/-1/1,2:-:2:-1/-1/1,2:-:2:-1/-1/1,2:-:2:-1/-1/1,";

    [Fact]
    public void TheExcessAnomaly_ReArmsWhenTheCountFallsBack_AndFiresAgain()
    {
        using SpellTestKit kit = Kit(BulkPassives);
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit, 79);
        for (int i = 0; i < BulkPassives; i++)
        {
            CastSelf(kit, player, FirstBulkPassive + (uint)i);
        }

        UnitSpellState state = kit.System.GetState(player.Guid)!;
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "aura/holders-excess" && n.Subject.Contains("79", StringComparison.Ordinal));
        kit.Advance(200);
        Assert.Single(capture.Notices);

        int drop = state.Auras.Count - SpellSystem.AuraHoldersExcessBound;
        for (int i = 0; i < drop; i++)
        {
            kit.System.RemoveAuras(player, FirstBulkPassive + (uint)i);
        }

        Assert.Equal(SpellSystem.AuraHoldersExcessBound, state.Auras.Count);
        kit.Advance(200);
        Assert.Single(capture.Notices);

        for (int i = 0; i < drop; i++)
        {
            CastSelf(kit, player, FirstBulkPassive + (uint)i);
        }

        kit.Advance(200);
        Assert.Equal(2, capture.Notices.Count);
    }

    [Fact]
    public void APermanentHolderWithAStandingCancelsSpell_AnAreaParent_OrAHeartbeat_CountsAsTicking()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, StandingPassive);
        CastSelf(kit, player, PassiveA);
        CastSelf(kit, player, Timed3s);
        UnitSpellState state = kit.System.GetState(player.Guid)!;
        SpellAuraHolder standing = state.Auras.Single(h => h.Spell.Id == StandingPassive);
        SpellAuraHolder plain = state.Auras.Single(h => h.Spell.Id == PassiveA);
        SpellAuraHolder other = state.Auras.Single(h => h.Spell.Id == Timed3s);
        Assert.True(standing.IsPermanent && plain.IsPermanent);

        Assert.True(SpellSystem.HolderNeedsTick(standing));
        Assert.False(SpellSystem.HolderNeedsTick(plain));

        plain.HeartbeatRandValue = 5f;
        Assert.True(SpellSystem.HolderNeedsTick(plain));
        plain.HeartbeatRandValue = 0f;
        Assert.False(SpellSystem.HolderNeedsTick(plain));

        plain.PveHeartbeatTimer = 0;
        Assert.True(SpellSystem.HolderNeedsTick(plain));
        plain.PveHeartbeatTimer = -1;
        Assert.False(SpellSystem.HolderNeedsTick(plain));

        plain.AreaParent = other;
        Assert.True(SpellSystem.HolderNeedsTick(plain));
        plain.AreaParent = null;
        Assert.False(SpellSystem.HolderNeedsTick(plain));
    }

    [Fact]
    public void APermanentHolderWhoseTimedAuraTypeHasNoPeriod_CountsAsTicking_UntilTheStallIsReported()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, NoPeriodPassive);
        UnitSpellState state = kit.System.GetState(player.Guid)!;
        SpellAuraHolder holder = state.Auras.Single();
        SpellAura aura = holder.AuraSpan.ToArray().Single(a => a is not null)!;
        Assert.False(aura.IsPeriodic);

        // AddAuraHolder already validated it, so the flag is set; clear it to see the unreported state.
        aura.PeriodicStalledReported = false;
        Assert.True(SpellSystem.HolderNeedsTick(holder));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "aura/periodic-stalled" && n.Subject == $"spell {NoPeriodPassive}");

        kit.Advance(200);

        Assert.Single(capture.Notices);
        Assert.True(aura.PeriodicStalledReported);
        Assert.False(SpellSystem.HolderNeedsTick(holder));
        Assert.False(state.HasTickingHolders);
    }

    [Fact]
    public void APassiveWithABadDuration_CountsAsTicking_IsRepairedAndReportedOnce_ThenGoesIdle()
    {
        using SpellTestKit kit = Kit();
        kit.System.SpellBatchWindowMs = 0;
        Player player = AddUnit(kit);
        CastSelf(kit, player, PassiveA);
        UnitSpellState state = kit.System.GetState(player.Guid)!;
        SpellAuraHolder holder = state.Auras.Single();
        kit.Advance(200);
        Assert.False(state.HasTickingHolders);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "aura/duration-invalid" && n.Subject == $"spell {PassiveA}");

        holder.Duration = 5000;
        Assert.True(SpellSystem.HolderNeedsTick(holder));
        kit.Advance(300);

        Assert.Single(capture.Notices);
        Assert.Equal(-1, holder.Duration);
        Assert.Equal(-1, holder.MaxDuration);
        Assert.False(SpellSystem.HolderNeedsTick(holder));
        Assert.False(state.HasTickingHolders);
    }
}
