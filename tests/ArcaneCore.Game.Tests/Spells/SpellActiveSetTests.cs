using System.Diagnostics;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>How a wake scenario runs: wake on, one wake reason switched off with the audit off, or switched off with the audit healing it.</summary>
public enum WakeMode
{
    /// <summary>The wake is on and the audit cannot help (budget 0): the effect must happen.</summary>
    Enabled,

    /// <summary>The wake reason is switched off and the audit cannot help: the effect must NOT happen (the RED proof that the test sees a missing wake).</summary>
    Disabled,

    /// <summary>The wake reason is switched off, the audit runs with budget 64: the effect happens eventually and the audit counted a stale heal.</summary>
    AuditHeals,
}

/// <summary>
/// The spell tick's active set (SpellSystem.ActiveSet.cs): every wake reason, the idle cost, the equivalence with the visit-every-state loop
/// and a mass wake. A wake test makes a unit dormant first (a permanent passive, advanced until <c>IsDormant</c> and not visited), then
/// causes one event and checks the effect the per-state update must produce. Each reason runs three ways (<see cref="WakeMode"/>); the
/// Disabled run fails when the effect still happens, which would mean the wake is redundant or the test is weak.
/// </summary>
public sealed class SpellActiveSetTests(ITestOutputHelper output)
{
    private const uint TimedSelf = 963001;
    private const uint SelfDot = 963002;
    private const uint SlowSelf = 963003;
    private const uint ShortDot = 963004;

    private static SpellTestKit NewKit()
    {
        var kit = new SpellTestKit(
            SpellTestKit.Spell(TimedSelf, SpellTestKit.Effect(SpellEffectName.ApplyAura, 3, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(2000, 0, 2000),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            SpellTestKit.Spell(SelfDot, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage, amplitude: 1000)) with
            {
                Duration = new SpellDuration(4000, 0, 4000),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            SpellTestKit.Spell(SlowSelf, SpellTestKit.Effect(SpellEffectName.Heal, 20)) with
            {
                CastTime = new SpellCastTime(1500, 0, 0),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            SpellTestKit.Spell(ShortDot, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage, amplitude: 1000)) with
            {
                Duration = new SpellDuration(3000, 0, 3000),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        kit.System.SpellBatchWindowMs = 0;
        return kit;
    }

    /// <summary>A player that is in <paramref name="host"/>'s map without being registered in it (no visibility work), so thousands are cheap.</summary>
    private static Player AddCheapUnit(Map host, uint guid, IPlayerSession? session = null)
    {
        Player player = TestWorld.CreatePlayer(guid, 0, 0, session ?? new FakeSession((int)guid));
        player.Map = host;
        return player;
    }

    private static void PassiveOn(SpellTestKit kit, Player player)
        => Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellTestKit.Passive, SpellCastTargets.ForSelf(), triggered: true));

    private static void Settle(SpellTestKit kit, int ticks = 3)
    {
        for (int i = 0; i < ticks; i++)
        {
            kit.Advance(100);
        }
    }

    /// <summary>Settle until the system visited nothing in a tick; returns the number of ticks it took.</summary>
    private static int SettleUntilIdle(SpellTestKit kit)
    {
        for (int tick = 1; tick <= 10; tick++)
        {
            kit.Advance(100);
            if (kit.System.LastStatesVisited == 0)
            {
                return tick;
            }
        }

        throw new InvalidOperationException("the spell system never went idle");
    }

    /// <summary>A player holding one permanent passive, dormant: <c>IsDormant</c> and not visited in the last tick.</summary>
    private static Player MakeDormant(SpellTestKit kit, uint guid)
    {
        (Player player, _) = kit.AddPlayer(guid);
        PassiveOn(kit, player);
        SettleUntilIdle(kit);
        UnitSpellState state = Assert.IsType<UnitSpellState>(kit.System.GetState(player.Guid));
        Assert.True(kit.System.IsDormant(state));
        kit.Advance(100);
        Assert.Equal(0, kit.System.LastStatesVisited);
        Assert.Equal(1, kit.System.LastStatesTracked);
        return player;
    }

    // --- (a) one scenario per wake reason --------------------------------------------------------------------------------------

    private sealed class Rig(SpellTestKit kit, Player player)
    {
        public SpellTestKit Kit { get; } = kit;

        public Player Player { get; } = player;

        public UnitSpellState State => Kit.System.GetState(Player.Guid)!;

        /// <summary>States visited, summed over the ticks advanced through <see cref="Advance"/>.</summary>
        public int Visits { get; private set; }

        public void Advance(uint ms)
        {
            for (uint done = 0; done < ms; done += 100)
            {
                Kit.Advance(100);
                Visits += Kit.System.LastStatesVisited;
            }
        }
    }

    private sealed record Scenario(
        SpellWakeReason Reason,
        Action<Rig> Trigger,
        Func<Rig, bool> EffectHappened,
        uint AdvanceMs,
        bool StartDormant = true,
        SpellWakeReason AlsoMasked = SpellWakeReason.None);

    private void Verify(WakeMode mode, Scenario scenario)
    {
        using SpellTestKit kit = NewKit();
        Player player = scenario.StartDormant ? MakeDormant(kit, 1) : kit.AddPlayer(1).Player;
        var rig = new Rig(kit, player);
        switch (mode)
        {
            case WakeMode.Enabled:
                kit.System.ActiveSetAuditBudget = 0;
                break;
            case WakeMode.Disabled:
                kit.System.ActiveSetAuditBudget = 0;
                kit.System.DisabledWakeReasons = scenario.Reason | scenario.AlsoMasked;
                break;
            default:
                kit.System.ActiveSetAuditThrows = false;
                kit.System.ActiveSetAuditBudget = 64;
                kit.System.DisabledWakeReasons = scenario.Reason | scenario.AlsoMasked;
                break;
        }

        scenario.Trigger(rig);
        rig.Advance(scenario.AdvanceMs);
        output.WriteLine($"{scenario.Reason} {mode}: effect={scenario.EffectHappened(rig)} staleHeals={kit.System.ActiveSetStaleHeals}");

        switch (mode)
        {
            case WakeMode.Enabled:
                Assert.True(scenario.EffectHappened(rig), $"{scenario.Reason}: the effect did not happen with the wake enabled");
                Assert.Equal(0, kit.System.ActiveSetStaleHeals);
                break;
            case WakeMode.Disabled:
                Assert.False(scenario.EffectHappened(rig), $"{scenario.Reason}: the effect happened with the wake disabled, so the wake is redundant or the test is weak");
                Assert.Equal(0, kit.System.ActiveSetStaleHeals);
                break;
            default:
                Assert.True(scenario.EffectHappened(rig), $"{scenario.Reason}: the audit did not heal the missing wake");
                Assert.True(kit.System.ActiveSetStaleHeals > 0, $"{scenario.Reason}: the audit healed without counting a stale state");
                break;
        }
    }

    // StateCreated: a state created for a unit that has none. Effect: the empty state is dropped on its first visit (TrackedUnitCount 0).
    private static Scenario StateCreated => new(
        SpellWakeReason.StateCreated,
        rig =>
        {
            Assert.Equal(0, rig.Kit.System.TrackedUnitCount);
            Assert.NotNull(rig.Kit.System.StateOf(rig.Player));
            Assert.Equal(1, rig.Kit.System.TrackedUnitCount);
        },
        rig => rig.Kit.System.TrackedUnitCount == 0,
        300,
        StartDormant: false);

    // AuraAdded: HolderChanged is also masked in the Disabled and AuditHeals runs (see AlsoMasked): the real cast writes holder fields
    // (Duration, StackAmount...) after attaching the holder, and that wake alone would also rescue the unit, so the cast alone cannot isolate AuraAdded.
    // AuraAdded: a timed aura cast on a dormant unit. Effect: it expires on time (the holder is gone).
    private static Scenario AuraAddedExpires => new(
        SpellWakeReason.AuraAdded,
        rig =>
        {
            Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Player, TimedSelf, SpellCastTargets.ForSelf(), triggered: true));
            Assert.True(rig.Kit.System.HasAura(rig.Player, TimedSelf));
        },
        rig => !rig.Kit.System.HasAura(rig.Player, TimedSelf),
        2500,
        AlsoMasked: SpellWakeReason.HolderChanged);

    // AuraAdded again, with a damage-over-time aura. Effect: it ticks three times in 3.5 s.
    private static Scenario AuraAddedTicks => new(
        SpellWakeReason.AuraAdded,
        rig => Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Player, SelfDot, SpellCastTargets.ForSelf(), triggered: true)),
        rig => rig.Kit.System.GetAuras(rig.Player).SingleOrDefault(h => h.Spell.Id == SelfDot) is { } dot && dot.AuraSpan[0]!.TickCount == 3,
        3500,
        AlsoMasked: SpellWakeReason.HolderChanged);

    // AuraRemoved: the last holder leaves the list (removed straight from the list, so no IsRemoved write also wakes the state).
    // Effect: the now-empty state is dropped (TrackedUnitCount 0).
    private static Scenario AuraRemoved => new(
        SpellWakeReason.AuraRemoved,
        rig =>
        {
            SpellAuraHolder holder = Assert.Single(rig.State.Auras);
            Assert.True(rig.State.Auras.Remove(holder));
        },
        rig => rig.Kit.System.TrackedUnitCount == 0,
        300);

    // HolderChanged: a permanent holder's Duration is written. Effect: the bad duration is repaired back to -1 by UpdateAuras.
    private static Scenario HolderChanged => new(
        SpellWakeReason.HolderChanged,
        rig =>
        {
            SpellAuraHolder holder = Assert.Single(rig.State.Auras);
            holder.Duration = 5000;
            Assert.Equal(5000, holder.Duration);
        },
        rig => Assert.Single(rig.State.Auras).Duration == -1,
        300);

    // CastStarted: a cast-time spell with no global cooldown on a dormant unit. Effect: the cast completes (CurrentCast is cleared).
    private static Scenario CastStarted => new(
        SpellWakeReason.CastStarted,
        rig =>
        {
            Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Player, SlowSelf, SpellCastTargets.ForSelf(), triggered: false));
            Assert.NotNull(rig.State.CurrentCast);
        },
        rig => rig.State.CurrentCast is null,
        2000);

    // AutoRepeatStarted: no kit spell is a real auto-repeat (Auto Shot and wand Shoot need a ranged weapon and ammo), so the slot is set
    // directly, as PrepareAutoRepeat does. Effect: the state is visited (UpdateAutoRepeat runs, which the dormant state would skip).
    private static Scenario AutoRepeatStarted => new(
        SpellWakeReason.AutoRepeatStarted,
        rig => rig.State.AutoRepeatCast = new SpellCast(rig.Kit.Store.Get(SpellTestKit.InstantHeal)!, rig.Player, SpellCastTargets.ForSelf(), false, 0, 0, 0),
        rig => rig.Visits >= 1,
        300);

    // MeleeQueued: a queued next-swing spell (set directly, as QueueNextSwing does). Effect: the state is visited. This reason has no other
    // observable effect: nothing in UpdateState reads MeleeCast, it only blocks dormancy and IsIdle.
    private static Scenario MeleeQueued => new(
        SpellWakeReason.MeleeQueued,
        rig => rig.State.MeleeCast = new SpellCast(rig.Kit.Store.Get(SpellTestKit.InstantHeal)!, rig.Player, SpellCastTargets.ForSelf(), false, 0, 0, 0),
        rig => rig.Visits >= 1,
        300);

    // ChannelResetPending: a channel ended normally while the state was dormant (the channel fields are still set). This cannot happen
    // through the cast flow (a channel keeps CurrentCast, so the state is awake until the cast's own visit sets the reset), so the reset is
    // set directly. Effect: the channel fields are cleared 1 s later.
    private static Scenario ChannelResetPending => new(
        SpellWakeReason.ChannelResetPending,
        rig =>
        {
            rig.Player.SetUInt32(UpdateFields.UnitChannelSpell, SpellTestKit.ChannelSpell);
            rig.Player.SetUInt64(UpdateFields.UnitFieldChannelObject, rig.Player.Guid.Value);
            rig.State.PendingChannelResetMs = SpellSystem.ChannelResetDelayMs;
        },
        rig => rig.Player.GetUInt32(UpdateFields.UnitChannelSpell) == 0 && rig.Player.GetUInt64(UpdateFields.UnitFieldChannelObject) == 0,
        1500);

    // Cooldown: a spell cooldown written on a dormant unit (an equip or trade cooldown does this). Effect: it expires (gone from the map).
    private static Scenario SpellCooldown => CooldownScenario(rig => rig.State.SpellCooldowns[SpellTestKit.CooldownSpell] = rig.Kit.Now + 3000, rig => rig.State.SpellCooldowns.Count);

    private static Scenario CategoryCooldown => CooldownScenario(rig => rig.State.CategoryCooldowns[77] = rig.Kit.Now + 3000, rig => rig.State.CategoryCooldowns.Count);

    private static Scenario GlobalCooldown => CooldownScenario(rig => rig.State.GlobalCooldowns[133] = rig.Kit.Now + 3000, rig => rig.State.GlobalCooldowns.Count);

    private static Scenario CooldownScenario(Action<Rig> write, Func<Rig, int> count) => new(
        SpellWakeReason.Cooldown,
        rig =>
        {
            write(rig);
            Assert.Equal(1, count(rig));
        },
        rig => count(rig) == 0,
        3500);

    // SchoolLockout: an interrupt lockout (the real LockOut). Effect: it expires.
    private static Scenario SchoolLockout => new(
        SpellWakeReason.SchoolLockout,
        rig =>
        {
            rig.Kit.System.LockOut(rig.Player, 1u << (int)SpellSchool.Fire, 3000, rig.Kit.Store.Get(SpellTestKit.CastBolt)!);
            Assert.True(rig.Kit.System.IsSchoolLocked(rig.Player, SpellSchool.Fire));
        },
        rig => rig.State.SchoolLockouts.Count == 0 && !rig.Kit.System.IsSchoolLocked(rig.Player, SpellSchool.Fire),
        3500);

    // MapChanged: the unit leaves the world. Effect: the spell system forgets its state (TrackedUnitCount 0).
    private static Scenario MapChanged => new(
        SpellWakeReason.MapChanged,
        rig =>
        {
            rig.Kit.World.RemovePlayer(rig.Player);
            Assert.False(rig.Player.IsInWorld);
        },
        rig => rig.Kit.System.TrackedUnitCount == 0,
        300);

    public static TheoryData<WakeMode> Modes => new() { WakeMode.Enabled, WakeMode.Disabled, WakeMode.AuditHeals };

    [Theory]
    [MemberData(nameof(Modes))]
    public void StateCreated_AnEmptyNewState_IsDroppedOnItsFirstVisit(WakeMode mode) => Verify(mode, StateCreated);

    [Theory]
    [MemberData(nameof(Modes))]
    public void AuraAdded_ATimedAuraOnADormantUnit_Expires(WakeMode mode) => Verify(mode, AuraAddedExpires);

    [Theory]
    [MemberData(nameof(Modes))]
    public void AuraAdded_ADotOnADormantUnit_Ticks(WakeMode mode) => Verify(mode, AuraAddedTicks);

    [Theory]
    [MemberData(nameof(Modes))]
    public void AuraRemoved_TheLastAuraLeaving_GetsTheStateDropped(WakeMode mode) => Verify(mode, AuraRemoved);

    [Theory]
    [MemberData(nameof(Modes))]
    public void HolderChanged_ADurationWrittenOnAPermanentHolder_IsProcessed(WakeMode mode) => Verify(mode, HolderChanged);

    [Theory]
    [MemberData(nameof(Modes))]
    public void CastStarted_ACastOnADormantUnit_Completes(WakeMode mode) => Verify(mode, CastStarted);

    [Theory]
    [MemberData(nameof(Modes))]
    public void AutoRepeatStarted_AnAutoRepeatSlotOnADormantUnit_GetsVisited(WakeMode mode) => Verify(mode, AutoRepeatStarted);

    [Theory]
    [MemberData(nameof(Modes))]
    public void MeleeQueued_ANextSwingSpellOnADormantUnit_GetsVisited(WakeMode mode) => Verify(mode, MeleeQueued);

    [Theory]
    [MemberData(nameof(Modes))]
    public void ChannelResetPending_APendingReset_ClearsTheChannelFields(WakeMode mode) => Verify(mode, ChannelResetPending);

    [Theory]
    [MemberData(nameof(Modes))]
    public void Cooldown_ASpellCooldownOnADormantUnit_Expires(WakeMode mode) => Verify(mode, SpellCooldown);

    [Theory]
    [MemberData(nameof(Modes))]
    public void Cooldown_ACategoryCooldownOnADormantUnit_Expires(WakeMode mode) => Verify(mode, CategoryCooldown);

    [Theory]
    [MemberData(nameof(Modes))]
    public void Cooldown_AGlobalCooldownOnADormantUnit_Expires(WakeMode mode) => Verify(mode, GlobalCooldown);

    [Theory]
    [MemberData(nameof(Modes))]
    public void SchoolLockout_AnInterruptLockoutOnADormantUnit_Expires(WakeMode mode) => Verify(mode, SchoolLockout);

    [Theory]
    [MemberData(nameof(Modes))]
    public void MapChanged_ALeavingUnit_HasItsStateForgotten(WakeMode mode) => Verify(mode, MapChanged);

    [Fact]
    public void AnExpiredCooldown_LetsTheSpellBeCastAgain_AfterTheWake()
    {
        using SpellTestKit kit = NewKit();
        Player player = MakeDormant(kit, 1);
        kit.Spellbook.Teach(player, SpellTestKit.CooldownSpell);
        kit.System.ActiveSetAuditBudget = 0;
        kit.System.StateOf(player).SpellCooldowns[SpellTestKit.CooldownSpell] = kit.Now + 3000;
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(player, SpellTestKit.CooldownSpell, SpellCastTargets.ForSelf(), triggered: false));

        kit.Advance(3500);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellTestKit.CooldownSpell, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void RemovingAnAuraThroughTheSpellSystem_DropsTheDormantStateWithoutTheAudit()
    {
        using SpellTestKit kit = NewKit();
        Player player = MakeDormant(kit, 1);
        kit.System.ActiveSetAuditBudget = 0;

        kit.System.RemoveAuras(player, SpellTestKit.Passive);
        kit.Advance(300);

        Assert.Equal(0, kit.System.TrackedUnitCount);
        Assert.Null(kit.System.GetState(player.Guid));
    }

    // --- (b) the idle cost ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void StatesVisited_AtIdle_StaysUnderTenPercentOfTheTrackedStates_AndFeedsTheGauge()
    {
        using SpellTestKit kit = NewKit();
        kit.System.ActiveSetAuditBudget = 0;
        Player host = kit.AddPlayer(1).Player;
        var active = new List<Player>();
        for (uint i = 0; i < 205; i++)
        {
            Player unit = AddCheapUnit(host.Map!, 100 + i);
            PassiveOn(kit, unit);
            if (i >= 200)
            {
                active.Add(unit);
            }
        }

        // 3 units with a long damage-over-time aura, 2 with a long cooldown.
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(active[i], SelfDot, SpellCastTargets.ForSelf(), triggered: true));
            kit.System.GetAuras(active[i]).Single(h => h.Spell.Id == SelfDot).Duration = 3_600_000;
        }

        for (int i = 3; i < 5; i++)
        {
            kit.System.StateOf(active[i]).SpellCooldowns[SpellTestKit.CooldownSpell] = kit.Now + 3_600_000;
        }

        Settle(kit, 3);
        Assert.Equal(205, kit.System.LastStatesTracked);
        Assert.Equal(5, kit.System.LastStatesActive);

        for (int tick = 0; tick < 20; tick++)
        {
            kit.Advance(100);
            Assert.True(
                kit.System.LastStatesVisited <= 0.10 * kit.System.LastStatesTracked,
                $"tick {tick}: visited {kit.System.LastStatesVisited} of {kit.System.LastStatesTracked}");
            Assert.Equal(5, kit.System.LastStatesVisited);
        }

        // CounterRegistry is process-wide and other tests update the same gauge in parallel, so its value cannot be compared with
        // LastStatesVisited here without a race; only its registration and kind are asserted.
        Counter gauge = Assert.IsType<Counter>(CounterRegistry.Default.Find("spell.states_visited"));
        Assert.Equal(CounterKind.Gauge, gauge.Kind);
        Assert.IsType<Counter>(CounterRegistry.Default.Find("spell.states_active"));
        output.WriteLine($"visited {kit.System.LastStatesVisited}/{kit.System.LastStatesTracked}, gauge now {gauge.Value}");
    }

    // --- (c) equivalence with the visit-every-state loop -----------------------------------------------------------------------------

    private static (string Trace, int DotTicks, int TrackedAtEnd) RunMixed(bool activeSet)
    {
        using SpellTestKit kit = NewKit();
        kit.System.ActiveSetEnabled = activeSet;
        var players = new List<Player>();
        var sessions = new List<FakeSession>();
        for (uint guid = 1; guid <= 7; guid++)
        {
            (Player player, FakeSession session) = kit.AddPlayer(guid, guid * 4, 0);
            kit.Spellbook.Teach(player, SlowSelf, SpellTestKit.CooldownSpell);
            players.Add(player);
            sessions.Add(session);
        }

        // Later players' spawn packets reach the earlier sessions and carry a wall-clock movement stamp, which differs between two runs.
        sessions.ForEach(session => session.Clear());

        // 0: passives only. 1: passive and a 2 s aura. 2: a DoT. 3: a cooldown. 4: a cast. 5: passive, leaves the world at tick 15.
        // 6: an idle state. 0 also gets a 2 s aura at tick 10 (a wake of a settled unit), 3 a lockout at tick 20.
        foreach (int i in new[] { 0, 1, 3, 5 })
        {
            PassiveOn(kit, players[i]);
        }

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(players[1], TimedSelf, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(players[2], SelfDot, SpellCastTargets.ForSelf(), triggered: true));
        kit.System.StateOf(players[3]).SpellCooldowns[SpellTestKit.CooldownSpell] = kit.Now + 1550;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(players[4], SlowSelf, SpellCastTargets.ForSelf(), triggered: false));
        kit.System.StateOf(players[6]);

        var trace = new StringBuilder();
        int dotTicks = 0;
        for (int tick = 1; tick <= 60; tick++)
        {
            if (tick == 10)
            {
                Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(players[0], TimedSelf, SpellCastTargets.ForSelf(), triggered: true));
            }

            if (tick == 15)
            {
                kit.World.RemovePlayer(players[5]);
            }

            if (tick == 20)
            {
                kit.System.LockOut(players[3], 1u << (int)SpellSchool.Fire, 1000, kit.Store.Get(SpellTestKit.CastBolt)!);
            }

            kit.Advance(100);
            trace.Append('T').Append(tick).Append(" tracked=").Append(kit.System.TrackedUnitCount).Append('\n');
            for (int i = 0; i < players.Count; i++)
            {
                UnitSpellState? state = kit.System.GetState(players[i].Guid);
                trace.Append(" u").Append(i).Append(':');
                if (state is null)
                {
                    trace.Append("none");
                }
                else
                {
                    foreach (SpellAuraHolder holder in state.Auras)
                    {
                        int ticks = holder.AuraSpan[0]?.TickCount ?? 0;
                        if (holder.Spell.Id == SelfDot)
                        {
                            dotTicks = Math.Max(dotTicks, ticks);
                        }

                        trace.Append(" a").Append(holder.Spell.Id).Append('/').Append(holder.Duration).Append('/').Append(ticks);
                    }

                    trace.Append(" cast=").Append(state.CurrentCast?.Spell.Id ?? 0)
                        .Append(" cd=").Append(state.SpellCooldowns.Count).Append('/').Append(state.CategoryCooldowns.Count).Append('/').Append(state.GlobalCooldowns.Count)
                        .Append(" lock=").Append(state.SchoolLockouts.Count);
                }

                foreach ((WorldOpcode opcode, byte[] payload) in sessions[i].Sent)
                {
                    trace.Append(" pkt=").Append((ushort)opcode).Append(':').Append(Convert.ToHexString(payload));
                }

                sessions[i].Clear();
                trace.Append('\n');
            }
        }

        return (trace.ToString(), dotTicks, kit.System.TrackedUnitCount);
    }

    [Fact]
    public void AMixedMultiUnitScenario_GivesTheSameTrace_WithAndWithoutTheActiveSet()
    {
        (string loop, int loopDotTicks, int loopTracked) = RunMixed(activeSet: false);
        (string set, int setDotTicks, int setTracked) = RunMixed(activeSet: true);

        // The scenario must actually exercise something, or equal traces would prove nothing.
        Assert.True(loopDotTicks >= 3, $"DoT ticks {loopDotTicks}");
        Assert.Equal(loopDotTicks, setDotTicks);
        Assert.Contains("pkt=", loop);
        Assert.Contains("none", loop);
        Assert.Equal(loopTracked, setTracked);
        Assert.True(loop == set, FirstDifference(loop, set));
    }

    private static string FirstDifference(string expected, string actual)
    {
        string[] a = expected.Split('\n');
        string[] b = actual.Split('\n');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return $"traces differ at line {i}:\n loop: {a[i]}\n set:  {b[i]}";
            }
        }

        return $"traces differ in length: {a.Length} vs {b.Length} lines";
    }

    // --- (d) a mass wake -------------------------------------------------------------------------------------------------------------

    private sealed class OrderSession(int accountId, List<(int Account, int Tick)> log, Func<int> tick) : IPlayerSession
    {
        public int AccountId { get; } = accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) => log.Add((AccountId, tick()));

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    [Fact]
    public void AMassWake_OfTwoThousandDormantUnits_ExpiresEveryAura_InCreationOrder()
    {
        const int units = 2000;
        using SpellTestKit kit = NewKit();
        kit.System.ActiveSetAuditBudget = 0;
        int currentTick = 0;
        var log = new List<(int Account, int Tick)>();
        Player host = kit.AddPlayer(1).Player;
        var players = new List<Player>(units);
        for (uint i = 0; i < units; i++)
        {
            Player unit = AddCheapUnit(host.Map!, 1000 + i, new OrderSession((int)(1000 + i), log, () => currentTick));
            PassiveOn(kit, unit);
            players.Add(unit);
        }

        SettleUntilIdle(kit);
        kit.Advance(100);
        Assert.Equal(units, kit.System.LastStatesTracked);
        Assert.Equal(0, kit.System.LastStatesVisited);
        Assert.Equal(0, kit.System.LastStatesActive);

        var watch = Stopwatch.StartNew();
        foreach (Player unit in players)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(unit, ShortDot, SpellCastTargets.ForSelf(), triggered: true));
        }

        watch.Stop();
        long wakeMs = watch.ElapsedMilliseconds;
        log.Clear();

        // The first update after the mass wake visits every unit exactly once.
        watch.Restart();
        currentTick = 1;
        kit.Advance(100);
        watch.Stop();
        output.WriteLine($"waking {units} units: {wakeMs} ms; first update after the wake: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(units, kit.System.LastStatesVisited);
        Assert.Equal(units, kit.System.LastStatesActive);

        for (int tick = 2; tick <= 28; tick++)
        {
            currentTick = tick;
            kit.Advance(100);
            if (tick == 28)
            {
                Assert.All(players, unit => Assert.True(kit.System.HasAura(unit, ShortDot), "an aura expired early"));
            }
        }

        // The DoT lasts 3 s: ticks at 1 s, 2 s and 3 s, gone right after.
        for (int tick = 29; tick <= 32; tick++)
        {
            currentTick = tick;
            kit.Advance(100);
        }

        Assert.All(players, unit => Assert.False(kit.System.HasAura(unit, ShortDot), "an aura did not expire on time"));
        Assert.All(players, unit => Assert.True(kit.System.HasAura(unit, SpellTestKit.Passive)));

        // Visit order: within every update the units that sent a packet appear in creation order, and every unit sent the same number.
        foreach (IGrouping<int, (int Account, int Tick)> perTick in log.GroupBy(entry => entry.Tick))
        {
            int[] accounts = [.. perTick.Select(entry => entry.Account)];
            Assert.Equal(accounts.Order().ToArray(), accounts);
        }

        Assert.True(log.Count > 0, "no unit sent a packet, so the order was not observed");
        int[] perUnit = [.. log.GroupBy(entry => entry.Account).Select(group => group.Count()).Distinct()];
        Assert.Single(perUnit);
        Assert.Equal(units, log.Select(entry => entry.Account).Distinct().Count());

        SettleUntilIdle(kit);
        Assert.Equal(0, kit.System.LastStatesVisited);
        Assert.Equal(units, kit.System.LastStatesTracked);
    }
}
