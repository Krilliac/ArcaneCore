using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The DBC-driven spell contract sweep: every selected Spell.dbc 5875 row is cast in a composed world and checked against the
/// contracts its own row declares (handlers, aura duration, aura and cast interrupt flags, cooldowns, costs, range, stacking,
/// dispel), with the invariants checked after every cast. Fails on exceptions and invariant breaks; the other contracts are
/// ratcheted against <c>spell-contract-baseline.txt</c>. Needs the client DBCs (ARCANECORE_TEST_DBC_DIR) and skips without them.
/// Default selection: every player-castable spell plus a seeded sample of the rest; ARCANECORE_SPELL_SWEEP_FULL=1 sweeps all rows.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class SpellContractSweepTests(ITestOutputHelper output)
{
    private static SpellStore SweepStore(IEnumerable<SpellInfo> spells) => new([.. spells, SweepHelperSpells.SelfDummySpell()], [], []);

    [RealClientDbcFact]
    public void Sweep_NoExceptionsOrInvariantBreaks_AndContractsMatchTheBaseline()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        bool full = SpellSweepCatalog.FullRequested;
        IReadOnlyList<SpellInfo> selection = catalog.Select(full);
        using var rig = new SpellSweepRig(SweepStore(catalog.Spells), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults(); // start-up noise of the composed features is not a spell's
        SweepResult result = new SpellSweepRunner(rig, catalog).Run(selection, catalog.Spells.Count, full);

        SpellSweepBaseline baseline = SpellSweepBaseline.Load(SpellSweepBaseline.SourcePath);
        HashSet<uint> swept = [.. result.Outcomes.Select(o => o.SpellId)];
        (string[] added, string[] fixedKeys) = baseline.Compare(result.Violations, swept);
        (string json, string markdown) = SpellSweepReport.Write(result, full ? "full" : "default", added, fixedKeys);
        output.WriteLine($"swept {result.Outcomes.Count} of {result.RowsTotal} rows in {result.Elapsed.TotalSeconds:F0} s; report {json} and {markdown}");
        output.WriteLine(SpellSweepReport.Summary(result, added, fixedKeys));

        SpellSweepCoverage coverage = SpellSweepCoverage.Load(SpellSweepCoverage.SourcePath);
        string[] coverageChanged = coverage.Compare(result.Outcomes);
        output.WriteLine($"coverage: {coverageChanged.Length} swept spells changed which checks ran");
        if (Environment.GetEnvironmentVariable(SpellSweepBaseline.WriteVariable) == "1")
        {
            Assert.True(full, $"{SpellSweepBaseline.WriteVariable}=1 needs {SpellSweepCatalog.FullVariable}=1: a partial sweep would drop the other spells' lines");
            baseline.Write(result.Violations);
            coverage.Write(result.Outcomes);
            output.WriteLine($"baseline rewritten: {baseline.Path}; coverage rewritten: {coverage.Path}");
            (added, fixedKeys, coverageChanged) = ([], [], []);
        }

        Assert.True(result.Outcomes.Count == selection.Count && selection.Count > 0, $"swept {result.Outcomes.Count} of {selection.Count} selected spells");
        SweepViolation[] gating = [.. result.Violations.Where(v => SweepCategory.IsGating(v.Category))];
        Assert.True(gating.Length == 0, "exceptions or invariant breaks:\n" + string.Join('\n', gating
            .GroupBy(v => v.Category + " " + v.Detail).OrderByDescending(g => g.Count()).Take(25)
            .Select(g => $"{g.Count(),5} {g.Key} (e.g. {string.Join(", ", g.Select(v => v.SpellId).Distinct().Take(5))})")));
        Assert.True(added.Length == 0, $"{added.Length} contract violations not in the baseline (regressions):\n" + string.Join('\n', added.Take(40)));
        Assert.True(fixedKeys.Length == 0, $"{fixedKeys.Length} baseline lines no longer happen: delete them from {SpellSweepBaseline.FileName} (the ratchet):\n"
            + string.Join('\n', fixedKeys.Take(40)));
        Assert.True(coverageChanged.Length == 0, $"{coverageChanged.Length} spells changed which checks ran (a check that stops running is not a pass;"
            + $" refresh {SpellSweepCoverage.FileName} only for an expected change):\n" + string.Join('\n', coverageChanged.Take(40)));
    }

    /// <summary>
    /// The live report: Feign Death does not end when the hunter moves (AuraInterruptFlags carries MOVING). A known failure another lane is
    /// fixing; when the fix lands this test fails and says to delete the baseline line, so the regression check takes over.
    /// </summary>
    [RealClientDbcFact]
    public void FeignDeath_5384_DoesNotBreakOnMove_KnownFailure()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo feign = catalog.Store.Get(5384) ?? throw new InvalidOperationException("Spell.dbc has no 5384");
        Assert.True(feign.AuraInterruptFlags.HasFlag(SpellAuraInterruptFlags.Moving), $"5384 AuraInterruptFlags=0x{(uint)feign.AuraInterruptFlags:X}");
        using var rig = new SpellSweepRig(SweepStore(catalog.Spells), RealClientDbcFactAttribute.DbcDirectory);
        SweepResult result = new SpellSweepRunner(rig, catalog) { Contracts = "cj" }.Run([feign], 1, full: false);
        output.WriteLine(string.Join('\n', result.Violations.Select(v => v.Key)));
        SweepViolation? violation = result.Violations.FirstOrDefault(v => v.Category == SweepCategory.InterruptNotRemoved && v.Detail.StartsWith("move ", StringComparison.Ordinal));
        Assert.True(violation is not null,
            "Feign Death now breaks on movement: the known failure is fixed. Delete its c.interrupt-not-removed move line from the baseline and this test.");
        Assert.Contains(violation!.Key, SpellSweepBaseline.Load(SpellSweepBaseline.SourcePath).Keys);
    }

    // ---- the harness can fail ------------------------------------------------------------------------------------------

    private const uint MoveBreaks = 990_950, ShortBuff = 990_951, StunNoClear = 990_952, StandBreaks = 990_953, CastBreaks = 990_954,
        Cooled = 990_955, Costly = 990_956, Ranged = 990_957, Stacker = 990_958, MagicBuff = 990_959, Dispeller = 990_960, TickThrows = 990_961,
        ShortAura = 990_962;

    /// <summary>An instant (or <paramref name="castMs"/>) self Dummy with no aura.</summary>
    private static SpellInfo SelfSpell(uint id, int castMs = 0) => new()
    {
        Id = id,
        Name = "Sweep fault " + id,
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Range = new SpellRange(0, 0),
        CastTime = new SpellCastTime(castMs, 0, castMs),
        SpellVisual = 1,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Dummy, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    /// <summary>A priest-family Magic dispel of any unit within 30 yards (the dispeller the sweep indexes for (i)).</summary>
    private static SpellInfo DispelSpell() => new()
    {
        Id = Dispeller,
        Name = "Sweep dispel",
        SpellFamilyName = 6,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        SpellVisual = 1,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Dispel, BaseDice = 1, DieSides = 1, MiscValue = 1, TargetA = SpellImplicitTarget.Unit }, new(), new()],
    };

    private static SpellInfo Aura(uint id, AuraType type, int durationMs, SpellAuraInterruptFlags flags = SpellAuraInterruptFlags.None) => new()
    {
        Id = id,
        Name = "Sweep fault " + id,
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Range = new SpellRange(0, 0),
        Duration = new SpellDuration(durationMs, 0, durationMs),
        AuraInterruptFlags = flags,
        SpellVisual = 1,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster, AuraType = type }, new(), new()],
    };

    private static SweepResult RunFaults(SpellInfo spell, string contracts, ISweepActions? actions = null, ISweepProbe? probe = null,
        Action<SpellSweepRig>? arrange = null, SpellInfo[]? extra = null)
    {
        using var rig = new SpellSweepRig(SweepStore([spell, .. extra ?? []]));
        arrange?.Invoke(rig);
        rig.TakeFaults();
        SweepResult result = new SpellSweepRunner(rig, catalog: null, actions, probe) { Contracts = contracts }.Run([spell], 1, full: false);
        foreach (SweepViolation violation in result.Violations)
        {
            Console.WriteLine("sweep-fault-test " + violation.Key);
        }

        return result;
    }

    [Fact]
    public void Harness_ACorrectStandBreakingAura_PassesTheInterruptCheck()
    {
        // STANDING_CANCELS through the production stand-state path works (food and drink use it), so a correct aura is clean.
        SweepResult result = RunFaults(Aura(StandBreaks, AuraType.Dummy, 30_000, (SpellAuraInterruptFlags)AuraInterruptMask.Standing), "cj");
        Assert.DoesNotContain(result.Violations, v => v.Category.StartsWith("c.", StringComparison.Ordinal) || SweepCategory.IsGating(v.Category));
        Assert.Contains("stand-up=ran", result.Outcomes[0].Checks["c"], StringComparison.Ordinal);
        Assert.Contains("melee-hit-taken=ran", result.Outcomes[0].Checks["c"], StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_ABrokenInterruptMapping_IsCaught()
    {
        // The double maps "stand-up" to the turning driver: the STANDING aura survives its own action.
        SweepResult result = RunFaults(Aura(StandBreaks, AuraType.Dummy, 30_000, (SpellAuraInterruptFlags)AuraInterruptMask.Standing), "cj",
            new StandIsTurnActions());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.InterruptNotRemoved && v.Detail.StartsWith("stand-up ", StringComparison.Ordinal));
    }

    /// <summary>
    /// What the sweep found on the production movement path: a player's MOVING_CANCELS aura survives a client move (no caller of
    /// RemoveAurasWithInterruptFlags(Moving) on it), the generic form of the Feign Death report. Known failure; flips when it is fixed.
    /// </summary>
    [Fact]
    public void Production_AMovingCancelsAura_SurvivesAClientMove_KnownFailure()
    {
        SweepResult result = RunFaults(Aura(MoveBreaks, AuraType.Dummy, 30_000, SpellAuraInterruptFlags.Moving), "cj");
        Assert.True(result.Violations.Any(v => v.Category == SweepCategory.InterruptNotRemoved && v.Detail.StartsWith("move ", StringComparison.Ordinal)),
            "a MOVING aura now breaks on a client move: the known failure is fixed; turn this into a passing control and refresh the baseline");
    }

    /// <summary>
    /// (c) judges an aura by how it ended, the same with batching off and on (both CI legs: ARCANECORE_TEST_SPELL_BATCH_MS unset and 400).
    /// An aura the action removed is judged, even when the action's cast makes the sweep wait for the batch boundary; an aura that ran out on
    /// its own meanwhile is "not run", never a pass, whether or not it carries the action's bit; and an aura shorter than
    /// <see cref="SpellSweepRunner.ShortestInterruptJudgedMs"/> (the 300 ms floor a Spell.dbc duration of 0 gets) is not judged at all, in
    /// either mode, because under batching it can run out before the action's effect lands.
    /// </summary>
    [Fact]
    public void Harness_AShortAura_IsJudgedByHowItEnded()
    {
        // (a) A broken mapping: "move" removes TURNING auras (RemoveMode Default), then casts (held to the boundary with batching on). The
        // TURNING aura was removed by an action that does not fire TURNING: removed-without-flag.
        var stripThenCast = new OneAction(new SweepAction("move", AuraInterruptMask.Moving, PlayerOnly: true, (scene, unit) =>
        {
            scene.Rig.System.RemoveAurasWithInterruptFlags(unit, SpellAuraInterruptFlags.Turning);
            return ProductionSweepActions.CastOther(scene, (Player)unit);
        }));
        SweepResult removed = RunFaults(Aura(ShortAura, AuraType.Dummy, 1_500, SpellAuraInterruptFlags.Turning), "cj", stripThenCast);
        Assert.Equal("move=ran", removed.Outcomes[0].Checks["c"]);
        Assert.Contains(removed.Violations, v => v.Category == SweepCategory.InterruptRemovedWithoutFlag && v.SpellId == ShortAura
            && v.Detail.StartsWith("move ", StringComparison.Ordinal));

        // (b) An action that takes two seconds: the aura expires during it. Flagless, its end is not a removal by the action; carrying the
        // action's bit (MOVING) with an action that removes nothing, its end is not the action honouring the bit. Neither is a pass.
        var waits = new OneAction(new SweepAction("move", AuraInterruptMask.Moving, PlayerOnly: true, (scene, _) =>
        {
            scene.Rig.Advance(2_000);
            return true;
        }));
        foreach (SpellAuraInterruptFlags flags in new[] { SpellAuraInterruptFlags.None, SpellAuraInterruptFlags.Moving })
        {
            SweepResult expired = RunFaults(Aura(ShortAura, AuraType.Dummy, 1_500, flags), "cj", waits);
            Assert.Equal("move=not-run:expired", expired.Outcomes[0].Checks["c"]);
            Assert.DoesNotContain(expired.Violations, v => v.Category.StartsWith("c.", StringComparison.Ordinal) || SweepCategory.IsGating(v.Category));
        }

        // (c) A 300 ms aura and the production cast action (whose go waits for the boundary with batching on): not judged, in either mode.
        var cast = new OneAction(new ProductionSweepActions().Actions.Single(a => a.Name == "cast"));
        SweepResult shortAura = RunFaults(Aura(ShortAura, AuraType.Dummy, 300), "cj", cast);
        Assert.Equal("cast=not-run:short-aura", shortAura.Outcomes[0].Checks["c"]);
        Assert.DoesNotContain(shortAura.Violations, v => v.Category.StartsWith("c.", StringComparison.Ordinal) || SweepCategory.IsGating(v.Category));
    }

    [Fact]
    public void Harness_ANeverExpiringAura_IsCaught()
    {
        SweepResult honest = RunFaults(Aura(ShortBuff, AuraType.Dummy, 3_000), "bj");
        Assert.DoesNotContain(honest.Violations, v => v.Category == SweepCategory.NeverExpires);
        Assert.Equal("ran", honest.Outcomes[0].Checks["b"]);

        SweepResult result = RunFaults(Aura(ShortBuff, AuraType.Dummy, 3_000), "bj", probe: new StickyAuraProbe());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.NeverExpires && v.SpellId == ShortBuff);
    }

    [Fact]
    public void Harness_ALeftoverFlag_IsCaughtAndFailsAsAnInvariant()
    {
        // An aura type nothing handles gets a handler that sets UNIT_FLAG_STUNNED on apply and forgets it on remove.
        AuraType unhandled = AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Unsupported).Type;
        SweepResult result = RunFaults(Aura(StunNoClear, unhandled, 2_000), "bj", arrange: rig =>
        {
            Assert.False(rig.System.HasAuraHandler(unhandled));
            rig.System.RegisterAura(unhandled, new AuraHandler((_, holder, _, apply) =>
            {
                if (apply)
                {
                    holder.Target.UnitFlags |= UnitFlags.Stunned;
                }
            }, null));
        });
        SweepViolation leftover = Assert.Single(result.Violations, v => v.Category == SweepCategory.LeftoverFlag);
        Assert.True(SweepCategory.IsGating(leftover.Category));
        Assert.Contains("Stunned", leftover.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_AnExceptionInAnApplyHandler_IsCaughtAsGating()
    {
        AuraType unhandled = AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Unsupported).Type;
        uint window = 0;
        SweepResult result = RunFaults(Aura(StunNoClear, unhandled, 2_000), "aj", arrange: rig =>
        {
            window = rig.System.SpellBatchWindowMs;
            rig.System.RegisterAura(unhandled, new AuraHandler((_, _, _, apply) =>
            {
                if (apply)
                {
                    throw new InvalidOperationException("injected");
                }
            }, null));
        });
        // The cast pipeline counts it in the ledger (spell.script) and lets it out of Cast. Without spell batching that is out of CastSpell,
        // into the sweep: both reach the report, both gate. With batching on (ARCANECORE_TEST_SPELL_BATCH_MS) the go is held to the batch
        // boundary and runs inside the world tick, where the batch flush catches it so the rest of the batch still goes off (SpellSystem.GoHeld)
        // and counts it again (spell.batch): the second signal is that ledger record instead, and it gates too.
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.LoggedFault && v.Detail.StartsWith("spell.script:System.InvalidOperationException", StringComparison.Ordinal));
        if (window == 0)
        {
            Assert.Contains(result.Violations, v => v.Category == SweepCategory.Exception && v.Detail.StartsWith("InvalidOperationException at", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(result.Violations, v => v.Category == SweepCategory.LoggedFault && v.Detail.StartsWith("spell.batch:System.InvalidOperationException", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A periodic handler that throws on every tick: the engine catches it into the fault ledger without logging (aura.tick), and the
    /// sweep still reports it as a gating logged fault.
    /// </summary>
    [Fact]
    public void Harness_AnExceptionCaughtIntoTheLedgerOnly_IsCaughtAsGating()
    {
        AuraType unhandled = AuraSupport.Entries.Where(e => e.Level == AuraSupportLevel.Unsupported).Skip(1).First().Type;
        SpellInfo spell = Aura(TickThrows, unhandled, 3_000) with
        {
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster, AuraType = unhandled, Amplitude = 1_000 }, new(), new()],
        };
        SweepResult result = RunFaults(spell, "bj", arrange: rig =>
            rig.System.RegisterAura(unhandled, new AuraHandler(null, (_, _, _) => throw new InvalidOperationException("injected tick"))));
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.LoggedFault && v.Detail.StartsWith("aura.tick:System.InvalidOperationException", StringComparison.Ordinal));
    }

    /// <summary>An exception out of a driver (here an interrupt action) is a gating exception of the spell, and the sweep goes on.</summary>
    [Fact]
    public void Harness_AnExceptionOutOfTheSweep_IsCaughtAsGating()
    {
        SweepResult result = RunFaults(Aura(StandBreaks, AuraType.Dummy, 30_000, (SpellAuraInterruptFlags)AuraInterruptMask.Standing), "cj",
            new ThrowingActions());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.Exception && v.Detail.StartsWith("NotSupportedException", StringComparison.Ordinal));
    }

    [Fact]
    public void Harness_ACastThatIgnoresMovement_IsCaught()
    {
        SpellInfo spell = SelfSpell(CastBreaks, castMs: 2_000) with { InterruptFlags = SpellInterruptFlags.Movement };
        SweepResult honest = RunFaults(spell, "d");
        Assert.Equal("ran", honest.Outcomes[0].Checks["d"]);
        Assert.DoesNotContain(honest.Violations, v => v.Category.StartsWith("d.", StringComparison.Ordinal));

        SweepResult result = RunFaults(spell, "d", probe: new AlwaysCastingProbe());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.CastNotInterrupted && v.SpellId == CastBreaks);
    }

    [Fact]
    public void Harness_AMissingCooldown_IsCaught()
    {
        SpellInfo spell = SelfSpell(Cooled) with { RecoveryTime = 5_000, Category = 990, CategoryRecoveryTime = 5_000 };
        SweepResult honest = RunFaults(spell, "ej");
        Assert.Equal("ran", honest.Outcomes[0].Checks["e"]);
        Assert.DoesNotContain(honest.Violations, v => v.Category.StartsWith("e.", StringComparison.Ordinal));

        SweepResult result = RunFaults(spell, "ej", probe: new NoCooldownProbe());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.CooldownMissing && v.SpellId == Cooled);
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.CategoryCooldownMissing && v.SpellId == Cooled);
    }

    [Fact]
    public void Harness_ACostNotTaken_IsCaught()
    {
        SpellInfo spell = SelfSpell(Costly) with { ManaCost = 50, PowerType = (int)PowerType.Mana };
        SweepResult honest = RunFaults(spell, "fj");
        Assert.Equal("ran", honest.Outcomes[0].Checks["f"]);
        Assert.DoesNotContain(honest.Violations, v => v.Category.StartsWith("f.", StringComparison.Ordinal));

        SweepResult result = RunFaults(spell, "fj", probe: new FlatPowerProbe());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.CostNotDeducted && v.SpellId == Costly);
    }

    [Fact]
    public void Harness_AnOutOfRangeCastAccepted_IsCaught()
    {
        SpellInfo spell = new()
        {
            Id = Ranged,
            Name = "Sweep fault ranged",
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitEnemy }, new(), new()],
        };
        SweepResult honest = RunFaults(spell, "g");
        Assert.Equal("ran", honest.Outcomes[0].Checks["g"]);
        Assert.DoesNotContain(honest.Violations, v => v.Category.StartsWith("g.", StringComparison.Ordinal));

        SweepResult result = RunFaults(spell, "g", probe: new RangeBlindProbe());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.OutOfRangeAccepted && v.SpellId == Ranged);
    }

    [Fact]
    public void Harness_StacksSpreadOverHolders_AreCaught()
    {
        SpellInfo spell = Aura(Stacker, AuraType.Dummy, 30_000) with { StackAmount = 3 };
        SweepResult honest = RunFaults(spell, "hj");
        Assert.Equal("ran", honest.Outcomes[0].Checks["h"]);
        Assert.DoesNotContain(honest.Violations, v => v.Category.StartsWith("h.", StringComparison.Ordinal));

        SweepResult result = RunFaults(spell, "hj", probe: new SplitStackProbe());
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.StackOverCap && v.Detail.Contains("holders=", StringComparison.Ordinal));
    }

    [Fact]
    public void Harness_AnAuraThatSurvivesItsDispel_IsCaught()
    {
        SpellInfo spell = Aura(MagicBuff, AuraType.Dummy, 30_000) with { Dispel = 1 };
        SweepResult honest = RunFaults(spell, "ij", extra: [DispelSpell()]);
        Assert.Equal("ran:" + Dispeller, honest.Outcomes[0].Checks["i"]);
        Assert.DoesNotContain(honest.Violations, v => v.Category.StartsWith("i.", StringComparison.Ordinal));

        SweepResult result = RunFaults(spell, "ij", probe: new StickyAuraProbe(), extra: [DispelSpell()]);
        Assert.Contains(result.Violations, v => v.Category == SweepCategory.NotDispelled && v.SpellId == MagicBuff);
    }

    [Fact]
    public void Baseline_Ratchet_ReportsNewAndFixed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sweep-baseline-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path, ["# header", "c.interrupt-not-removed\t5\tmove flags=0x8", "b.never-expires\t9\tx", "e.cooldown-missing\t77\tunswept"]);
            SpellSweepBaseline baseline = SpellSweepBaseline.Load(path);
            (string[] added, string[] fixedKeys) = baseline.Compare(
                [new(SweepCategory.InterruptNotRemoved, 5, "move flags=0x8"), new(SweepCategory.StackOverCap, 6, "cap=3"), new(SweepCategory.LeftoverFlag, 6, "gating")],
                new HashSet<uint> { 5, 6, 9 });
            Assert.Equal(["h.stack-over-cap\t6\tcap=3"], added);
            Assert.Equal(["b.never-expires\t9\tx"], fixedKeys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A broken interrupt mapping: the "stand-up" action performs a turn.</summary>
    private sealed class StandIsTurnActions : ISweepActions
    {
        private readonly ProductionSweepActions _real = new();

        public IReadOnlyList<SweepAction> Actions =>
        [
            .. _real.Actions.Select(a => a.Name == "stand-up" ? a with { Perform = (scene, unit) => ProductionSweepActions.Turn(scene, (Player)unit) } : a),
        ];

        public bool Move(SweepScene scene, Player player) => ProductionSweepActions.Move(scene, player);
    }

    /// <summary>One action, and the production move for (d).</summary>
    private sealed class OneAction(SweepAction action) : ISweepActions
    {
        public IReadOnlyList<SweepAction> Actions { get; } = [action];

        public bool Move(SweepScene scene, Player player) => ProductionSweepActions.Move(scene, player);
    }

    /// <summary>An action driver that throws.</summary>
    private sealed class ThrowingActions : ISweepActions
    {
        public IReadOnlyList<SweepAction> Actions { get; } =
            [new("throws", AuraInterruptMask.Standing, PlayerOnly: false, (_, _) => throw new NotSupportedException("injected driver fault"))];

        public bool Move(SweepScene scene, Player player) => ProductionSweepActions.Move(scene, player);
    }

    /// <summary>A never-expiring aura: once seen, a holder keeps being reported as applied.</summary>
    private sealed class StickyAuraProbe : ProductionSweepProbe
    {
        private readonly Dictionary<(ObjectGuid, uint), IReadOnlyList<SpellAuraHolder>> _seen = [];

        public override IReadOnlyList<SpellAuraHolder> Holders(SpellSystem system, Unit unit, uint spellId)
        {
            IReadOnlyList<SpellAuraHolder> live = base.Holders(system, unit, spellId);
            if (live.Count > 0)
            {
                _seen[(unit.Guid, spellId)] = live;
                return live;
            }

            return _seen.GetValueOrDefault((unit.Guid, spellId)) ?? [];
        }
    }

    /// <summary>A cast that a move does not interrupt: the caster keeps casting.</summary>
    private sealed class AlwaysCastingProbe : ProductionSweepProbe
    {
        public override bool IsCasting(SpellSystem system, Unit unit) => true;
    }

    /// <summary>A spell that never goes on cooldown, its own or its category's.</summary>
    private sealed class NoCooldownProbe : ProductionSweepProbe
    {
        public override bool IsReady(SpellSystem system, Unit unit, SpellInfo spell) => true;

        public override uint? CategoryCooldownUntil(SpellSystem system, Unit unit, uint category) => null;
    }

    /// <summary>A cost that is never taken: the power never moves.</summary>
    private sealed class FlatPowerProbe : ProductionSweepProbe
    {
        public override uint Power(Unit unit, PowerType power) => 1_000;
    }

    /// <summary>A range check that lets everything through.</summary>
    private sealed class RangeBlindProbe : ProductionSweepProbe
    {
        public override SpellCastResult Cast(SpellSystem system, Unit caster, uint spellId, SpellCastTargets targets, bool triggered)
        {
            SpellCastResult result = base.Cast(system, caster, spellId, targets, triggered);
            return result == SpellCastResult.OutOfRange ? SpellCastResult.CastOk : result;
        }
    }

    /// <summary>Every application lands in a holder of its own (reported as two holders of the same caster).</summary>
    private sealed class SplitStackProbe : ProductionSweepProbe
    {
        public override IReadOnlyList<SpellAuraHolder> Holders(SpellSystem system, Unit unit, uint spellId)
        {
            IReadOnlyList<SpellAuraHolder> live = base.Holders(system, unit, spellId);
            return live.Count > 0 ? [.. live, .. live] : live;
        }
    }
}

/// <summary>
/// The sweep reads the process-wide fault ledger (<see cref="ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared"/>) and attributes every new fault
/// to the spell being swept, so no other test may record into it meanwhile: these tests run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpellContractSweepCollection
{
    public const string Name = "Spell contract sweep (fault ledger)";
}
