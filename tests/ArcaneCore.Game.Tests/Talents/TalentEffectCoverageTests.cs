using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Talents;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Talents;

public sealed class TalentEffectCoverageTests
{
    private const uint A = 6001, B = 6002, C = 6003, D = 6004, Missing = 6999;

    private static readonly TalentCatalog Catalog = new(
        [new TalentTabRecord(1, 1, 0)],
        [
            new TalentRecord(30, 1, 0, 0, [A, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(10, 1, 0, 1, [B, C, 0, 0, 0], 0, 0, 0),
            new TalentRecord(20, 1, 0, 2, [D, Missing, 0, 0, 0], 0, 0, 0),
        ]);

    // Wave-2 integration: Instakill gained a built-in handler (spell-breadth), so the gap fixture uses an effect id no lane registers.
    private const SpellEffectName UnhandledEffect = (SpellEffectName)250;

    private static SpellTestKit Kit() => new(
        Spell(A, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)),
        Spell(B, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.AddFlatModifier, misc: 1)),
        Spell(C, Effect(SpellEffectName.Dummy, 0)),
        Spell(D, Effect(UnhandledEffect, 0), Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.AddPctModifier)));

    [Fact]
    public void Report_ListsTheRankSpellsWhoseHandlersAreMissing_InTalentThenRankOrder()
    {
        using SpellTestKit kit = Kit();

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, kit.System);

        Assert.Equal(5, report.RankSpellCount);
        Assert.Equal(2, report.SupportedCount);       // A (aura Dummy) and C (effect Dummy)
        Assert.Equal(
            [
                new TalentCoverageGap(10, 0, B, TalentGapKind.MissingAura, (int)AuraType.AddFlatModifier),
                new TalentCoverageGap(20, 0, D, TalentGapKind.MissingEffect, (int)UnhandledEffect),
                new TalentCoverageGap(20, 0, D, TalentGapKind.MissingAura, (int)AuraType.AddPctModifier),
                new TalentCoverageGap(20, 1, Missing, TalentGapKind.SpellMissing, 0),
            ],
            report.Gaps);
    }

    [Fact]
    public void Report_GroupsGapsByTheMissingHandler()
    {
        using SpellTestKit kit = Kit();

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, kit.System);

        Assert.Equal(1, report.CountOf(TalentGapKind.MissingAura, (int)AuraType.AddFlatModifier));
        Assert.Equal(1, report.CountOf(TalentGapKind.MissingAura, (int)AuraType.AddPctModifier));
        Assert.Equal(1, report.CountOf(TalentGapKind.MissingEffect, (int)UnhandledEffect));
        Assert.Equal(1, report.CountOf(TalentGapKind.SpellMissing, 0));
        Assert.Equal(0, report.CountOf(TalentGapKind.MissingAura, (int)AuraType.Dummy));
    }

    [Fact]
    public void RegisteringAHandler_RemovesItsGap()
    {
        using SpellTestKit kit = Kit();
        Assert.Contains(TalentEffectCoverage.Build(Catalog, kit.System).Gaps, g => g.Spell == B);

        kit.System.RegisterAura(AuraType.AddFlatModifier, new AuraHandler(null, null));

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, kit.System);
        Assert.DoesNotContain(report.Gaps, g => g.Spell == B);
        Assert.Equal(3, report.SupportedCount);
    }

    [Fact]
    public void Describe_IsDeterministic_AndNamesTheTotals()
    {
        using SpellTestKit kit = Kit();

        string first = TalentEffectCoverage.Build(Catalog, kit.System).Describe();
        string second = TalentEffectCoverage.Build(Catalog, kit.System).Describe();

        Assert.Equal(first, second);
        Assert.Contains("2 of 5", first);
        Assert.Contains($"aura {(int)AuraType.AddFlatModifier}", first);
    }

    [Fact]
    public void AnEmptyCatalog_ReportsNothing()
    {
        using SpellTestKit kit = Kit();
        TalentCoverageReport report = TalentEffectCoverage.Build(new TalentCatalog([], []), kit.System);
        Assert.Equal(0, report.RankSpellCount);
        Assert.Empty(report.Gaps);
    }
}
