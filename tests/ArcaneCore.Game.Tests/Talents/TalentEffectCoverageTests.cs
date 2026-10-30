using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.PetAuras;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.TestSupport;
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
        Assert.Equal(1, report.SupportedCount);       // C (effect Dummy); A's DUMMY aura applies but nothing reads it
        Assert.Equal(
            [
                new TalentCoverageGap(10, 0, B, TalentGapKind.MissingAura, (int)UnhandledAuraA),
                new TalentCoverageGap(20, 0, D, TalentGapKind.MissingEffect, (int)UnhandledEffect),
                new TalentCoverageGap(20, 0, D, TalentGapKind.MissingAura, (int)UnhandledAuraB),
                new TalentCoverageGap(20, 1, Missing, TalentGapKind.SpellMissing, 0),
                new TalentCoverageGap(30, 0, A, TalentGapKind.NoConsumer, (int)AuraType.Dummy),
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
        Assert.Equal(1, report.CountOf(TalentGapKind.NoConsumer, (int)AuraType.Dummy));
    }

    [Fact]
    public void RegisteringAHandler_RemovesItsGap()
    {
        using SpellTestKit kit = Kit();
        Assert.Contains(TalentEffectCoverage.Build(Catalog, kit.System).Gaps, g => g.Spell == B);

        kit.System.RegisterAura(UnhandledAuraA, new AuraHandler(null, null));

        TalentCoverageReport report = TalentEffectCoverage.Build(Catalog, kit.System);
        Assert.DoesNotContain(report.Gaps, g => g.Spell == B);
        Assert.Equal(2, report.SupportedCount);
    }

    [Fact]
    public void Describe_IsDeterministic_AndNamesTheTotals()
    {
        using SpellTestKit kit = Kit();

        string first = TalentEffectCoverage.Build(Catalog, kit.System).Describe();
        string second = TalentEffectCoverage.Build(Catalog, kit.System).Describe();

        Assert.Equal(first, second);
        Assert.Contains("1 of 5", first);
        Assert.Contains($"aura {(int)UnhandledAuraA}", first);
        Assert.Contains("missing consumer of aura 4 (Dummy): 1", first);
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

    [Fact]
    public void DummyAuraHandlers_PeriodicDummyAuras_AndImprovedHealthstone_AreConsumers()
    {
        const uint customDummy = 6110, customPeriodicDummy = 6111;
        using SpellTestKit kit = new(
            DummyTalent(customDummy),
            DummyTalent(customPeriodicDummy),
            DummyTalent(PetAuraTable.SpiritBond1),
            DummyTalent(CreateHealthstoneScript.ImprovedHealthstoneRank1),
            DummyTalent(CreateHealthstoneScript.ImprovedHealthstoneRank2));
        var catalog = new TalentCatalog(
            [new TalentTabRecord(1, 1, 0)],
            [
                new TalentRecord(50, 1, 0, 0, [customDummy, customPeriodicDummy, PetAuraTable.SpiritBond1, CreateHealthstoneScript.ImprovedHealthstoneRank1, CreateHealthstoneScript.ImprovedHealthstoneRank2], 0, 0, 0),
            ]);

        TalentCoverageReport before = TalentEffectCoverage.Build(catalog, kit.System);
        Assert.Contains(before.Gaps, g => g.Spell == customDummy);
        Assert.Contains(before.Gaps, g => g.Spell == customPeriodicDummy);
        Assert.DoesNotContain(before.Gaps, g => g.Spell == PetAuraTable.SpiritBond1);
        Assert.DoesNotContain(before.Gaps, g => g.Spell == CreateHealthstoneScript.ImprovedHealthstoneRank1);
        Assert.DoesNotContain(before.Gaps, g => g.Spell == CreateHealthstoneScript.ImprovedHealthstoneRank2);

        kit.System.RegisterDummyAuraHandler(customDummy, static (_, _, _, _) => { });
        kit.System.RegisterPeriodicDummyAura(customPeriodicDummy, 1000, static (_, _, _) => { });

        TalentCoverageReport after = TalentEffectCoverage.Build(catalog, kit.System);
        Assert.Empty(after.Gaps);
    }

    /// <summary>
    /// With spell batching on (<c>ARCANECORE_TEST_SPELL_BATCH_MS</c>) a cast that goes off is held until the next window boundary: advance
    /// the clock to it so the held go resolves. A no-op with the window at 0.
    /// </summary>
    private static void FlushSpellBatch(SpellTestKit kit)
    {
        uint window = kit.System.SpellBatchWindowMs;
        if (window != 0)
        {
            kit.Advance(window - (kit.Now % window));
        }
    }

    [Fact]
    public void DreamwalkerClassScripts_AreConsumedAndTriggerPowerAndRegrowthBonuses()
    {
        const uint rejuvBonusAura = 28716, regrowthBonusAura = 28744, rejuvSpell = 774, regrowthSpell = 8936;
        using SpellTestKit kit = new(
            ClassScriptTalent(rejuvBonusAura, 4533, family: 7) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                ProcFlags = ProcFlags.DealHelpfulSpell,
                ProcChance = 100,
            },
            ClassScriptTalent(regrowthBonusAura, 4537, family: 7) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                ProcFlags = ProcFlags.DealHelpfulSpell,
                ProcChance = 100,
            },
            Spell(rejuvSpell, Effect(SpellEffectName.Heal, 50, SpellImplicitTarget.UnitFriend)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 40),
                SpellFamilyName = 7,
                SpellFamilyFlags = 1UL << 4,
            },
            Spell(regrowthSpell, Effect(SpellEffectName.Heal, 50, SpellImplicitTarget.UnitFriend)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 40),
                SpellFamilyName = 7,
                SpellFamilyFlags = 1UL << 6,
            },
            Spell(28722, Effect(SpellEffectName.Energize, 60, SpellImplicitTarget.UnitFriend, misc: (int)PowerType.Mana)) with { RangeIndex = 4, Range = new SpellRange(0, 40) },
            Spell(28723, Effect(SpellEffectName.Energize, 100, SpellImplicitTarget.UnitFriend, misc: (int)PowerType.Rage)) with { RangeIndex = 4, Range = new SpellRange(0, 40) },
            Spell(28724, Effect(SpellEffectName.Energize, 20, SpellImplicitTarget.UnitFriend, misc: (int)PowerType.Energy)) with { RangeIndex = 4, Range = new SpellRange(0, 40) },
            Spell(28750, Effect(SpellEffectName.ApplyAura, 100, SpellImplicitTarget.UnitFriend, aura: AuraType.ModIncreaseHealth)) with { Duration = new SpellDuration(15_000, 0, 15_000), RangeIndex = 4, Range = new SpellRange(0, 40) });

        var catalog = new TalentCatalog(
            [new TalentTabRecord(1, 1, 0)],
            [new TalentRecord(51, 1, 0, 0, [rejuvBonusAura, regrowthBonusAura, 0, 0, 0], 0, 0, 0)]);
        Assert.Empty(TalentEffectCoverage.Build(catalog, kit.System).Gaps);

        (Player druid, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, x: 2);
        SpellSystem.SetPower(ally, PowerType.Rage, 100);
        kit.System.CastSpell(druid, rejuvBonusAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(druid, regrowthBonusAura, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CastSpell(druid, rejuvSpell, SpellCastTargets.ForUnit(ally.Guid), triggered: false);
        FlushSpellBatch(kit);
        Assert.Equal(200u, SpellSystem.GetPower(ally, PowerType.Rage));

        kit.Now += 2_000;
        kit.System.CastSpell(druid, regrowthSpell, SpellCastTargets.ForUnit(ally.Guid), triggered: false);
        FlushSpellBatch(kit);
        Assert.True(kit.System.HasAura(ally, 28750));
    }

    private sealed class FakeProcScript : IProcScript
    {
    }

    private sealed class FakePeriodicScript : IPeriodicDamageScript
    {
    }

    /// <summary>
    /// Build 5875 talents whose OVERRIDE_CLASS_SCRIPTS effect is read by a rule outside the registries (Improved Scorpid Sting, Improved Drain Mana,
    /// Illumination, Pyroclasm) or by nothing at all in vmangos (Arcane Concentration: its proc trigger is the talent) are not reported as gaps.
    /// </summary>
    [Theory]
    [InlineData(2388)]
    [InlineData(2390)]
    [InlineData(2028)]
    [InlineData(2029)]
    [InlineData(2689)]
    [InlineData(2188)]
    [InlineData(1388)]
    [InlineData(1392)]
    public void ClassScriptsOfTalentsWorkingElsewhere_HaveAConsumer(int classScript)
    {
        using var kit = new SpellTestKit(Spell(990_100, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.OverrideClassScripts, misc: classScript)));
        SpellInfo spell = kit.Store.Get(990_100)!;

        Assert.True(TalentEffectCoverage.HasConsumer(kit.System, spell, spell.Effects[0]));
    }

    /// <summary>
    /// A DUMMY or OVERRIDE_CLASS_SCRIPTS talent read outside the script registries counts as handled only while its rule still reads it: every
    /// <see cref="TalentEffectCoverage.ExternalReaders"/> site must hold a code line matching its pattern, so removing the read (or the rule) fails
    /// here instead of leaving the talent reported as handled.
    /// </summary>
    [Fact]
    public void EveryExternalReader_StillHasItsReadingLine()
    {
        string root = Path.Combine(RepoRoot(), "src", "ArcaneCore.Game");
        IReadOnlyList<ExternalReader> readers = TalentEffectCoverage.ExternalReaders;

        bool Reads(ExternalReader reader)
        {
            string file = Path.Combine(root, reader.Site.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) && File.ReadLines(file).Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(line, reader.Pattern));
        }

        Assert.True(readers.Count >= 20, $"only {readers.Count} external readers: the scan proved nothing");
        Assert.False(Reads(readers[0] with { Pattern = "NoSuchReadingLine_" }), "the scan matches a line that does not exist");
        Assert.False(Reads(readers[0] with { Site = "NoSuchFile.cs" }), "the scan matches a file that does not exist");
        Assert.Empty(readers.Where(reader => !Reads(reader)).Select(reader => $"{reader.Kind} {reader.Key}: {reader.Site} /{reader.Pattern}/"));
        Assert.Equal(
            [831u, 832u, 833u, 834u, 835u, 2028u, 2029u, 2188u, 2189u, 2228u, 2388u, 2389u, 2390u, 2689u, 3654u, 3736u, 4327u, 4415u, 11094u, 11189u, 13043u, 17007u, 17864u, 18393u, 18692u, 18693u, 20375u, 20711u, 23445u, 23958u, 28332u, 29203u],
            readers.Where(r => r.Kind is ExternalReaderKind.ClassScript or ExternalReaderKind.DummySpell).Select(r => r.Key).Distinct().Order());
    }

    private static string RepoRoot()
        => RepositorySource.RequireRoot();

    [SpellScript(ScriptConsumer)]
    private sealed class FakeSpellScript : ISpellScript
    {
    }
}
