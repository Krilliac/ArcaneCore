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

        if (Environment.GetEnvironmentVariable(SpellSweepBaseline.WriteVariable) == "1")
        {
            Assert.True(full, $"{SpellSweepBaseline.WriteVariable}=1 needs {SpellSweepCatalog.FullVariable}=1: a partial sweep would drop the other spells' lines");
            baseline.Write(result.Violations);
            output.WriteLine($"baseline rewritten: {baseline.Path}");
            (added, fixedKeys) = ([], []);
        }

        Assert.True(result.Outcomes.Count == selection.Count && selection.Count > 0, $"swept {result.Outcomes.Count} of {selection.Count} selected spells");
        SweepViolation[] gating = [.. result.Violations.Where(v => SweepCategory.IsGating(v.Category))];
        Assert.True(gating.Length == 0, "exceptions or invariant breaks:\n" + string.Join('\n', gating
            .GroupBy(v => v.Category + " " + v.Detail).OrderByDescending(g => g.Count()).Take(25)
            .Select(g => $"{g.Count(),5} {g.Key} (e.g. {string.Join(", ", g.Select(v => v.SpellId).Distinct().Take(5))})")));
        Assert.True(added.Length == 0, $"{added.Length} contract violations not in the baseline (regressions):\n" + string.Join('\n', added.Take(40)));
        Assert.True(fixedKeys.Length == 0, $"{fixedKeys.Length} baseline lines no longer happen: delete them from {SpellSweepBaseline.FileName} (the ratchet):\n"
            + string.Join('\n', fixedKeys.Take(40)));
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

    private const uint MoveBreaks = 990_950, ShortBuff = 990_951, StunNoClear = 990_952, StandBreaks = 990_953;

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

    private static SweepResult RunFaults(SpellInfo spell, string contracts, ISweepActions? actions = null, ISweepProbe? probe = null, Action<SpellSweepRig>? arrange = null)
    {
        using var rig = new SpellSweepRig(SweepStore([spell]));
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
    public void Harness_AnExceptionInAHandler_IsCaughtAsGating()
    {
        AuraType unhandled = AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Unsupported).Type;
        SweepResult result = RunFaults(Aura(StunNoClear, unhandled, 2_000), "aj", arrange: rig =>
            rig.System.RegisterAura(unhandled, new AuraHandler((_, _, _, apply) =>
            {
                if (apply)
                {
                    throw new InvalidOperationException("injected");
                }
            }, null)));
        Assert.Contains(result.Violations, v => v.Category is SweepCategory.Exception or SweepCategory.LoggedFault);
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

    /// <summary>A never-expiring aura: once seen, a holder keeps being reported as applied.</summary>
    private sealed class StickyAuraProbe : ISweepProbe
    {
        private readonly ProductionSweepProbe _real = new();
        private readonly Dictionary<(ObjectGuid, uint), IReadOnlyList<SpellAuraHolder>> _seen = [];

        public IReadOnlyList<SpellAuraHolder> Holders(SpellSystem system, Unit unit, uint spellId)
        {
            IReadOnlyList<SpellAuraHolder> live = _real.Holders(system, unit, spellId);
            if (live.Count > 0)
            {
                _seen[(unit.Guid, spellId)] = live;
                return live;
            }

            return _seen.GetValueOrDefault((unit.Guid, spellId)) ?? [];
        }

        public UnitFlags Flags(Unit unit) => _real.Flags(unit);
    }
}
