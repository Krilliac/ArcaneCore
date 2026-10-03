using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Talents;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

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

    private sealed class Overlay : IClassMaskSource
    {
        public ulong? TryGetMask(uint spellId, int effectIndex) => spellId == NoMask ? 0x10UL : null;
    }
}
