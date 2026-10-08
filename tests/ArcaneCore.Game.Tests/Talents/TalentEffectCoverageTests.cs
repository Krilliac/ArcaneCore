using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Scripts;
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

    // Wave-4 spell-modifier-engine: aura 107/108 gained built-in handlers, so the missing-aura fixtures use aura ids no lane registers.
    private const AuraType UnhandledAuraA = (AuraType)250;
    private const AuraType UnhandledAuraB = (AuraType)251;

    private static SpellTestKit Kit() => new(
        Spell(A, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)),
        Spell(B, Effect(SpellEffectName.ApplyAura, 3, aura: UnhandledAuraA, misc: 1)),
        Spell(C, Effect(SpellEffectName.Dummy, 0)),
        Spell(D, Effect(UnhandledEffect, 0), Effect(SpellEffectName.ApplyAura, 0, aura: UnhandledAuraB)));

    [Fact]
    public void Report_ListsTheRankSpellsWhoseHandlersAreMissing_InTalentThenRankOrder()
    {
        using SpellTestKit kit = Kit();

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, kit.System);

        Assert.Equal(5, report.RankSpellCount);
        Assert.Equal(2, report.SupportedCount);       // A (aura Dummy) and C (effect Dummy)
        Assert.Equal(
            [
                new TalentCoverageGap(10, 0, B, TalentGapKind.MissingAura, (int)UnhandledAuraA),
                new TalentCoverageGap(20, 0, D, TalentGapKind.MissingEffect, (int)UnhandledEffect),
                new TalentCoverageGap(20, 0, D, TalentGapKind.MissingAura, (int)UnhandledAuraB),
                new TalentCoverageGap(20, 1, Missing, TalentGapKind.SpellMissing, 0),
            ],
            report.Gaps);
    }

    [Fact]
    public void Report_GroupsGapsByTheMissingHandler()
    {
        using SpellTestKit kit = Kit();

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, kit.System);

        Assert.Equal(1, report.CountOf(TalentGapKind.MissingAura, (int)UnhandledAuraA));
        Assert.Equal(1, report.CountOf(TalentGapKind.MissingAura, (int)UnhandledAuraB));
        Assert.Equal(1, report.CountOf(TalentGapKind.MissingEffect, (int)UnhandledEffect));
        Assert.Equal(1, report.CountOf(TalentGapKind.SpellMissing, 0));
        Assert.Equal(0, report.CountOf(TalentGapKind.MissingAura, (int)AuraType.Dummy));
    }

    [Fact]
    public void RegisteringAHandler_RemovesItsGap()
    {
        using SpellTestKit kit = Kit();
        Assert.Contains(TalentEffectCoverage.Build(Catalog, kit.System).Gaps, g => g.Spell == B);

        kit.System.RegisterAura(UnhandledAuraA, new AuraHandler(null, null));

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
        Assert.Contains($"aura {(int)UnhandledAuraA}", first);
    }

    [Fact]
    public void AnEmptyCatalog_ReportsNothing()
    {
        using SpellTestKit kit = Kit();
        TalentCoverageReport report = TalentEffectCoverage.Build(new TalentCatalog([], []), kit.System);
        Assert.Equal(0, report.RankSpellCount);
        Assert.Empty(report.Gaps);
    }

    // --- consumers of DUMMY (4) and OVERRIDE_CLASS_SCRIPTS (112) talent auras ------------------------------------------------------------

    private const uint Unconsumed = 6101, MasterOfElements = 29074, IconLookalike = 6102, ShatterRank1 = 11170, UnknownScript = 6103;
    private const uint PeriodicConsumer = 6104, ScriptConsumer = 6105;

    private static readonly TalentCatalog ConsumerCatalog = new(
        [new TalentTabRecord(1, 1, 0)],
        [
            new TalentRecord(40, 1, 0, 0, [Unconsumed, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(41, 1, 0, 1, [MasterOfElements, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(42, 1, 0, 2, [IconLookalike, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(43, 1, 1, 0, [ShatterRank1, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(44, 1, 1, 1, [UnknownScript, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(45, 1, 1, 2, [PeriodicConsumer, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(46, 1, 2, 0, [ScriptConsumer, 0, 0, 0, 0], 0, 0, 0),
        ]);

    private static SpellInfo DummyTalent(uint id, uint family = 0, uint icon = 0)
        => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { SpellFamilyName = family, SpellIconId = icon };

    private static SpellInfo ClassScriptTalent(uint id, int misc, uint family)
        => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.OverrideClassScripts, misc: misc)) with { SpellFamilyName = family };

    private static SpellTestKit ConsumerKit() => new(
        DummyTalent(Unconsumed, family: 3, icon: 9001),
        DummyTalent(MasterOfElements, family: 3, icon: 1920),
        DummyTalent(IconLookalike, family: 5, icon: 1920),
        ClassScriptTalent(ShatterRank1, 849, family: 3),
        ClassScriptTalent(UnknownScript, 4242, family: 3),
        DummyTalent(PeriodicConsumer),
        DummyTalent(ScriptConsumer));

    [Fact]
    public void ADummyTalentNothingConsumes_IsAGap_UntilAProcScriptIsRegisteredForIt()
    {
        using SpellTestKit kit = ConsumerKit();
        Assert.Contains(TalentEffectCoverage.Build(ConsumerCatalog, kit.System).Gaps, g => g.Spell == Unconsumed && g.Code == (int)AuraType.Dummy);

        // Any lane's proc script registered through SpellSystem.RegisterProcScript is a consumer, with no change to the report.
        kit.System.RegisterProcScript(Unconsumed, new FakeProcScript());

        Assert.DoesNotContain(TalentEffectCoverage.Build(ConsumerCatalog, kit.System).Gaps, g => g.Spell == Unconsumed);
    }

    [Fact]
    public void MasterOfElements_IsConsumedByItsIconKeyedScript_TheSameIconInAnotherFamilyIsNot()
    {
        using SpellTestKit kit = ConsumerKit();

        TalentCoverageReport report = TalentEffectCoverage.Build(ConsumerCatalog, kit.System);

        Assert.DoesNotContain(report.Gaps, g => g.Spell == MasterOfElements);
        Assert.Contains(report.Gaps, g => g.Spell == IconLookalike && g.Code == (int)AuraType.Dummy);
    }

    [Fact]
    public void AnOverrideClassScriptTalent_IsHandledOnlyWhenSomethingReadsItsScriptNumber()
    {
        using SpellTestKit kit = ConsumerKit();

        TalentCoverageReport report = TalentEffectCoverage.Build(ConsumerCatalog, kit.System);

        Assert.DoesNotContain(report.Gaps, g => g.Spell == ShatterRank1);                                         // the spell crit roll reads 849
        Assert.Contains(report.Gaps, g => g.Spell == UnknownScript && g.Code == (int)AuraType.OverrideClassScripts); // nothing reads 4242
    }

    [Fact]
    public void APeriodicScriptOrAnInstalledSpellScript_IsAConsumer()
    {
        using SpellTestKit kit = ConsumerKit();
        TalentCoverageReport before = TalentEffectCoverage.Build(ConsumerCatalog, kit.System);
        Assert.Contains(before.Gaps, g => g.Spell == PeriodicConsumer);
        Assert.Contains(before.Gaps, g => g.Spell == ScriptConsumer);

        kit.System.RegisterPeriodicDamageScript(PeriodicConsumer, new FakePeriodicScript());
        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new FakeSpellScript()]));

        TalentCoverageReport after = TalentEffectCoverage.Build(ConsumerCatalog, kit.System);
        Assert.DoesNotContain(after.Gaps, g => g.Spell == PeriodicConsumer);
        Assert.DoesNotContain(after.Gaps, g => g.Spell == ScriptConsumer);
    }

    private sealed class FakeProcScript : IProcScript
    {
    }

    private sealed class FakePeriodicScript : IPeriodicDamageScript
    {
    }

    [SpellScript(ScriptConsumer)]
    private sealed class FakeSpellScript : ISpellScript
    {
    }
}
