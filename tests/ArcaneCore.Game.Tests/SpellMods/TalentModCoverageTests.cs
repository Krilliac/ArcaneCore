using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestKit;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>The talent modifier coverage report, and the old handler coverage report now seeing aura 107 and 108 as handled.</summary>
public sealed class TalentModCoverageTests
{
    private const uint Good = 948001, PctMod = 948002, NoMask = 948003, BadOp = 948004, Other = 948005, Missing = 948999;

    private static readonly TalentCatalog Catalog = new(
        [new TalentTabRecord(1, 1, 0)],
        [
            new TalentRecord(1, 1, 0, 0, [Good, PctMod, 0, 0, 0], 0, 0, 0),
            new TalentRecord(2, 1, 0, 1, [NoMask, BadOp, 0, 0, 0], 0, 0, 0),
            new TalentRecord(3, 1, 0, 2, [Other, Missing, 0, 0, 0], 0, 0, 0),
        ]);

    private static SpellTestKit Kit() => new(
        Flat(Good, SpellModOp.Cost, -3),
        Pct(PctMod, SpellModOp.CastingTime, -10),
        Flat(NoMask, SpellModOp.Damage, 2, mask: 0),
        ModPassive(BadOp, AuraType.AddFlatModifier, (SpellModOp)40, 4),
        Spell(Other, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)));

    [Fact]
    public void Report_CountsModifierEffects_ByOperation_AndNamesTheOnesThatCannotWork()
    {
        using SpellTestKit kit = Kit();

        TalentModCoverageReport report = TalentModCoverage.Build(Catalog, kit.System);

        Assert.Equal(4, report.ModEffects);
        Assert.Equal(1, report.ByOperation[(int)SpellModOp.Cost]);
        Assert.Equal(1, report.ByOperation[(int)SpellModOp.CastingTime]);
        Assert.Equal(1, report.ByOperation[40]);
        Assert.Equal(
            [
                new TalentModGap(2, 0, NoMask, 0, TalentModGapKind.NoClassMask, 0),
                new TalentModGap(2, 1, BadOp, 0, TalentModGapKind.UnknownOperation, 40),
            ],
            report.Gaps);
    }

    [Fact]
    public void AnOverlayMask_ClosesANoMaskGap()
    {
        using SpellTestKit kit = Kit();
        kit.System.Mods.MaskSource = new Overlay();

        TalentModCoverageReport report = TalentModCoverage.Build(Catalog, kit.System);

        Assert.Equal(0, report.CountOf(TalentModGapKind.NoClassMask));
        Assert.Equal(1, report.CountOf(TalentModGapKind.UnknownOperation));
    }

    [Fact]
    public void Describe_IsDeterministic_AndNamesTheTotals()
    {
        using SpellTestKit kit = Kit();

        string first = TalentModCoverage.Build(Catalog, kit.System).Describe();
        string second = TalentModCoverage.Build(Catalog, kit.System).Describe();

        Assert.Equal(first, second);
        Assert.Contains("4 modifier effect(s)", first, StringComparison.Ordinal);
        Assert.Contains("1 with an empty class mask", first, StringComparison.Ordinal);
        Assert.Contains("op 14 (Cost): 1", first, StringComparison.Ordinal);
        Assert.Contains("op 40 (unknown): 1", first, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHandlerCoverageReport_HasNoMissingAuraGapForTheModifierAuras()
    {
        using SpellTestKit kit = Kit();

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, kit.System);

        Assert.Equal(0, report.CountOf(TalentGapKind.MissingAura, (int)AuraType.AddFlatModifier));
        Assert.Equal(0, report.CountOf(TalentGapKind.MissingAura, (int)AuraType.AddPctModifier));
    }

    private const uint SpeedMod = 948101, PowerMod = 948102, ChargesMod = 948103, CostMod = 948104, UnreadMod = 948105;
    private const SpellModOp UnreadOp = (SpellModOp)25;

    private static readonly TalentCatalog UnreadCatalog = new(
        [new TalentTabRecord(1, 1, 0)],
        [
            new TalentRecord(1, 1, 0, 0, [SpeedMod, PowerMod, 0, 0, 0], 0, 0, 0),
            new TalentRecord(2, 1, 0, 1, [ChargesMod, CostMod, UnreadMod, 0, 0], 0, 0, 0),
        ]);

    [Fact]
    public void Describe_NamesTheOperationsNothingReads()
    {
        using SpellTestKit kit = new(
            Pct(SpeedMod, SpellModOp.Speed, 10),
            Flat(PowerMod, SpellModOp.AttackPower, 5),
            Flat(ChargesMod, SpellModOp.Charges, 1),
            Flat(CostMod, SpellModOp.Cost, -3),
            Flat(UnreadMod, UnreadOp, 2));

        string text = TalentModCoverage.Build(UnreadCatalog, kit.System).Describe();

        // SPEED, ATTACK_POWER, CHARGES and COST are wired; an in-range operation with no reader in Readers is reported as unread.
        Assert.Contains("1 with an operation nothing reads", text, StringComparison.Ordinal);
        Assert.Contains("op 25 (25): 1, nothing reads it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("op 3 (AttackPower): 1, nothing reads it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("op 4 (Charges): 1, nothing reads it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("op 12 (Speed): 1, nothing reads it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("op 14 (Cost): 1, nothing reads it", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReport_ListsTheUnreadOperations_WithTheirEffectCounts()
    {
        using SpellTestKit kit = new(
            Pct(SpeedMod, SpellModOp.Speed, 10),
            Flat(PowerMod, SpellModOp.AttackPower, 5),
            Flat(ChargesMod, SpellModOp.Charges, 1),
            Flat(CostMod, SpellModOp.Cost, -3),
            Flat(UnreadMod, UnreadOp, 2));

        TalentModCoverageReport report = TalentModCoverage.Build(UnreadCatalog, kit.System);

        Assert.Equal(
            new Dictionary<SpellModOp, int> { [UnreadOp] = 1 },
            report.UnreadOperations.OrderBy(p => p.Key).ToDictionary());
        Assert.Equal(1, report.UnreadEffects);
        Assert.Empty(report.Gaps); // an unread operation is not a broken effect: the mod is stored and sent, it changes nothing on the server
    }

    /// <summary>
    /// The reader list is the set of operations the engine's source reads (an Apply, ModInt, ModFloat or ModsOf call naming the operation), so a
    /// lane that wires SPEED, ATTACK_POWER or CHARGES, or stops reading one, must update the list in the same change.
    /// </summary>
    [Fact]
    public void TheReaderList_IsTheSetOfOperationsTheEngineSourceReads()
    {
        // Files that only define, store, combine or produce modifiers: they name operations without reading one for a game rule.
        string[] notReaders = ["SpellEnums.Combat.cs", "SpellModEngine.cs", "SpellModMath.cs", "PassiveReapply.cs", "HardcodedMods.cs", "TalentModCoverage.cs"];
        string root = Path.Combine(RepoRoot(), "src", "ArcaneCore.Game");
        var read = new SortedSet<SpellModOp>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (notReaders.Contains(Path.GetFileName(file)) || relative.StartsWith("obj", StringComparison.Ordinal) || relative.StartsWith("bin", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string line in File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            {
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(line, @"SpellModOp\.(\w+)"))
                {
                    if (Enum.TryParse(match.Groups[1].Value, out SpellModOp op) && op != SpellModOp.Max)
                    {
                        read.Add(op);
                    }
                }
            }
        }

        Assert.True(read.Count >= 10, $"only {read.Count} operations found under {root}: the scan proved nothing");
        Assert.Equal(read, new SortedSet<SpellModOp>(TalentModCoverage.Readers.Keys));
        Assert.Contains(SpellModOp.Speed, read);
        Assert.Contains(SpellModOp.AttackPower, read);
        Assert.Contains(SpellModOp.Charges, read);
    }

    [Fact]
    public void AttackPowerMod_ScalesMeleeAndRangedAttackPowerAuras_AndRestoresOnRemoval()
    {
        const uint apBuff = 948201, rangedApBuff = 948202;
        using SpellTestKit kit = new(
            Pct(PowerMod, SpellModOp.AttackPower, 25),
            InFamily(Spell(apBuff, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModAttackPower)) with { Duration = new SpellDuration(60_000, 0, 60_000) }),
            InFamily(Spell(rangedApBuff, Effect(SpellEffectName.ApplyAura, 80, aura: AuraType.ModRangedAttackPower)) with { Duration = new SpellDuration(60_000, 0, 60_000) }));
        (Player caster, _) = kit.AddPlayer(1);
        kit.System.CastSpell(caster, PowerMod, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CastSpell(caster, apBuff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, rangedApBuff, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(125, (short)(caster.GetUInt32(UpdateFields.UnitFieldAttackPowerMods) & 0xFFFF));
        Assert.Equal(100, (short)(caster.GetUInt32(UpdateFields.UnitFieldRangedAttackPowerMods) & 0xFFFF));

        kit.System.RemoveAuras(caster, apBuff);
        kit.System.RemoveAuras(caster, rangedApBuff);

        Assert.Equal(0u, caster.GetUInt32(UpdateFields.UnitFieldAttackPowerMods));
        Assert.Equal(0u, caster.GetUInt32(UpdateFields.UnitFieldRangedAttackPowerMods));
    }

    [Fact]
    public void ChargesMod_ModifiesHolderProcCharges_WhenSpellHasBaseCharges()
    {
        const uint chargedAura = 948211, unchargedAura = 948212;
        using SpellTestKit kit = new(
            Flat(ChargesMod, SpellModOp.Charges, 2),
            InFamily(Spell(chargedAura, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModDamageDone, misc: 1)) with
            {
                Duration = new SpellDuration(60_000, 0, 60_000),
                ProcCharges = 3,
            }),
            InFamily(Spell(unchargedAura, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModDamageDone, misc: 1)) with
            {
                Duration = new SpellDuration(60_000, 0, 60_000),
                ProcCharges = 0,
            }));
        (Player caster, _) = kit.AddPlayer(1);
        kit.System.CastSpell(caster, ChargesMod, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CastSpell(caster, chargedAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, unchargedAura, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(5, kit.System.GetAuras(caster).Single(h => h.Spell.Id == chargedAura).Charges);
        Assert.Equal(0, kit.System.GetAuras(caster).Single(h => h.Spell.Id == unchargedAura).Charges);
    }

    [Fact]
    public void SpeedMod_ScalesIncreaseAndDecreaseSpeedAuras_AndRestoresOnRemoval()
    {
        const uint sprintBuff = 948221, snareDebuff = 948222, pctSpeedMod = 948223;
        using SpellTestKit kit = new(
            Flat(SpeedMod, SpellModOp.Speed, 20),
            Pct(pctSpeedMod, SpellModOp.Speed, 50, mask: 2),
            InFamily(Spell(sprintBuff, Effect(SpellEffectName.ApplyAura, 30, aura: AuraType.ModIncreaseSpeed)) with { Duration = new SpellDuration(60_000, 0, 60_000) }),
            InFamily(Spell(snareDebuff, Effect(SpellEffectName.ApplyAura, -40, SpellImplicitTarget.UnitEnemy, aura: AuraType.ModDecreaseSpeed)) with { Duration = new SpellDuration(60_000, 0, 60_000) }, flags: 2));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, x: 5);
        var options = new LocomotionOptions();
        kit.System.CastSpell(caster, SpeedMod, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, pctSpeedMod, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CastSpell(caster, sprintBuff, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(50, kit.System.GetAuras(caster).Single(h => h.Spell.Id == sprintBuff).Auras[0]!.Amount);
        Assert.Equal(1.5f, UnitSpeed.ComputeRate(caster, MoveType.Run, options, inBattleground: false));

        kit.System.RemoveAuras(caster, sprintBuff);
        Assert.Equal(1.0f, UnitSpeed.ComputeRate(caster, MoveType.Run, options, inBattleground: false));

        kit.System.CastSpell(caster, snareDebuff, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(-60, kit.System.GetAuras(target).Single(h => h.Spell.Id == snareDebuff).Auras[0]!.Amount);
        Assert.Equal(0.4f, UnitSpeed.ComputeRate(target, MoveType.Run, options, inBattleground: false));
    }

    private static string RepoRoot()
        => RepositorySource.RequireRoot();

    private sealed class Overlay : IClassMaskSource
    {
        public ulong? TryGetMask(uint spellId, int effectIndex) => spellId == NoMask ? 0x10UL : null;
    }
}
