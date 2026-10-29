using System.Text;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Shaman;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Talents;

namespace ArcaneCore.Game.Talents;

/// <summary>Why a talent rank spell is not fully supported.</summary>
public enum TalentGapKind
{
    /// <summary>The rank spell id is in Talent.dbc but not in the spell table.</summary>
    SpellMissing,

    /// <summary>An effect of the spell has no registered effect handler (<see cref="SpellSystem.HasEffectHandler"/>).</summary>
    MissingEffect,

    /// <summary>An ApplyAura effect names an aura type with no registered handler (<see cref="SpellSystem.HasAuraHandler"/>).</summary>
    MissingAura,

    /// <summary>
    /// A DUMMY (4) or OVERRIDE_CLASS_SCRIPTS (112) aura applies, but nothing reads it: these aura types do nothing by themselves (vmangos
    /// HandleAuraDummy / HandleNoImmediateEffect), the talent works only through a script or a rule that looks for it.
    /// </summary>
    NoConsumer,
}

/// <summary>One unsupported part of a talent rank spell. <paramref name="Code"/> is the effect or aura number (0 for a missing spell).</summary>
public readonly record struct TalentCoverageGap(uint Talent, int RankIndex, uint Spell, TalentGapKind Kind, int Code);

/// <summary>The talent rank spells the spell system can and cannot apply today.</summary>
public sealed class TalentCoverageReport
{
    private readonly Dictionary<(TalentGapKind, int), int> _counts = [];

    internal TalentCoverageReport(int rankSpellCount, int supportedCount, List<TalentCoverageGap> gaps)
    {
        RankSpellCount = rankSpellCount;
        SupportedCount = supportedCount;
        Gaps = gaps;
        foreach (TalentCoverageGap gap in gaps)
        {
            _counts[(gap.Kind, gap.Code)] = _counts.GetValueOrDefault((gap.Kind, gap.Code)) + 1;
        }
    }

    /// <summary>Talent rank spells in the catalog.</summary>
    public int RankSpellCount { get; }

    /// <summary>Rank spells whose every effect has a handler and whose every DUMMY or OVERRIDE_CLASS_SCRIPTS aura has a consumer.</summary>
    public int SupportedCount { get; }

    /// <summary>The unsupported parts, in talent id then rank order.</summary>
    public IReadOnlyList<TalentCoverageGap> Gaps { get; }

    /// <summary>How many gaps name this missing handler.</summary>
    public int CountOf(TalentGapKind kind, int code) => _counts.GetValueOrDefault((kind, code));

    /// <summary>A deterministic multi-line summary for the startup log.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("Talent effects: ").Append(SupportedCount).Append(" of ").Append(RankSpellCount)
            .Append(" rank spells are fully handled by the spell system.");
        foreach (IGrouping<(TalentGapKind Kind, int Code), TalentCoverageGap> group in Gaps
                     .GroupBy(g => (g.Kind, g.Code)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Code))
        {
            text.AppendLine().Append("  missing ").Append(Label(group.Key.Kind, group.Key.Code)).Append(": ").Append(group.Count());
        }

        return text.ToString();
    }

    private static string Label(TalentGapKind kind, int code) => kind switch
    {
        TalentGapKind.MissingAura => $"aura {code} ({(AuraType)code})",
        TalentGapKind.MissingEffect => $"effect {code} ({(SpellEffectName)code})",
        TalentGapKind.NoConsumer => $"consumer of aura {code} ({(AuraType)code})",
        _ => "spell",
    };
}

/// <summary>
/// Read-only report over the talent catalog, the spell table and the registered handlers: which talent rank spells carry an
/// effect or aura nothing implements yet. Learning such a talent is still allowed (as in retail) and spends the point, so this
/// is the visible, measurable list of the work the spell-modifier, proc and stat-aura areas still owe the talents.
/// <para>
/// A DUMMY (4) or OVERRIDE_CLASS_SCRIPTS (112) aura always has a handler (it does nothing on apply), so for those the report asks who reads
/// it (<see cref="HasConsumer"/>): a proc script (<see cref="SpellSystem.RegisterProcScript"/> or the family-and-icon
/// <see cref="SpellSystem.RegisterIconProcScript"/>), a periodic damage or trigger script, a spell script of the installed dispatcher, a
/// built-in proc case, or one of the known readers listed below. Every lane that registers through those registries is counted with no change
/// here; a rule that reads such an aura somewhere else belongs in the reader lists.
/// </para>
/// </summary>
public static class TalentEffectCoverage
{
    /// <summary>
    /// The DUMMY and OVERRIDE_CLASS_SCRIPTS auras a rule reads outside the script registries, each with the code line that reads it. A talent
    /// listed here counts as handled, so TalentEffectCoverageTests scans every <see cref="ExternalReader.Site"/> for its
    /// <see cref="ExternalReader.Pattern"/>: a rule that stops reading the aura fails that test instead of leaving the talent reported as handled.
    /// Shatter (<see cref="SpellCritRules.ShatterBonuses"/>) and the built-in proc cases are read from their own tables.
    /// </summary>
    public static IReadOnlyList<ExternalReader> ExternalReaders { get; } =
    [
        // Furor (ShapeshiftService.RollFuror) and Predatory Strikes (FormStatListener), by SpellIconID.
        new(ExternalReaderKind.DummyIcon, ShapeshiftService.FurorIconId, 0, "Spells/Stances/ShapeshiftService.cs", @"SpellIconId != FurorIconId\b"),
        new(ExternalReaderKind.DummyIcon, FormStatListener.PredatoryStrikesIconId, 0, "Stats/FormStatListener.cs", @"SpellIconId != PredatoryStrikesIconId\b"),

        // Improved Life Tap (LifeTapScript), by family and SpellIconID.
        new(ExternalReaderKind.DummyFamilyIcon, LifeTapScript.ImprovedLifeTapIcon, SoulShardRules.WarlockFamily, "Spells/Warlock/LifeTapScript.cs",
            @"SpellFamilyName != SoulShardRules\.WarlockFamily \|\| holder\.Spell\.SpellIconId != ImprovedLifeTapIcon\b"),

        // Frost Warding and Improved Fire Ward (HardcodedMods.WardMask, applied on the holder events).
        new(ExternalReaderKind.DummySpell, 11189, 0, "Spells/Mods/HardcodedMods.cs", @"\b11189 or 28332 =>"),
        new(ExternalReaderKind.DummySpell, 28332, 0, "Spells/Mods/HardcodedMods.cs", @"\b11189 or 28332 =>"),
        new(ExternalReaderKind.DummySpell, 11094, 0, "Spells/Mods/HardcodedMods.cs", @"\b11094 or 13043 =>"),
        new(ExternalReaderKind.DummySpell, 13043, 0, "Spells/Mods/HardcodedMods.cs", @"\b11094 or 13043 =>"),
        new(ExternalReaderKind.DummySpell, 11189, 0, "Spells/Mods/HardcodedMods.cs", @"WardMask\(holder\.Spell\.Id\)"),

        // Improved Drain Mana (DrainAuras): the rank constants and the read.
        new(ExternalReaderKind.DummySpell, 17864, 0, "Spells/Casters/Drain/DrainAuras.cs", @"ImprovedDrainManaRank1 = 17864;"),
        new(ExternalReaderKind.DummySpell, 17864, 0, "Spells/Casters/Drain/DrainAuras.cs", @"HasAura\(caster, ImprovedDrainManaRank1\)"),
        new(ExternalReaderKind.DummySpell, 18393, 0, "Spells/Casters/Drain/DrainAuras.cs", @"ImprovedDrainManaRank2 = 18393;"),
        new(ExternalReaderKind.DummySpell, 18393, 0, "Spells/Casters/Drain/DrainAuras.cs", @"HasAura\(caster, ImprovedDrainManaRank2\)"),

        // Improved Drain Mana's class scripts 2028/2029 (the talent spells carry OVERRIDE_CLASS_SCRIPTS, not DUMMY): DrainAuras reads the rank by spell id.
        new(ExternalReaderKind.ClassScript, 2028, 0, "Spells/Casters/Drain/DrainAuras.cs", @"HasAura\(caster, ImprovedDrainManaRank1\)"),
        new(ExternalReaderKind.ClassScript, 2029, 0, "Spells/Casters/Drain/DrainAuras.cs", @"HasAura\(caster, ImprovedDrainManaRank2\)"),

        // Improved Scorpid Sting (ImprovedScorpidStingModule.StaminaPercent): the class script table read when a Scorpid Sting lands.
        new(ExternalReaderKind.ClassScript, 2388, 0, "Spells/Hunter/ImprovedScorpidStingModule.cs", @"2388 => 10,"),
        new(ExternalReaderKind.ClassScript, 2389, 0, "Spells/Hunter/ImprovedScorpidStingModule.cs", @"2389 => 20,"),
        new(ExternalReaderKind.ClassScript, 2390, 0, "Spells/Hunter/ImprovedScorpidStingModule.cs", @"2390 => 30,"),

        // Illumination (2689) and Pyroclasm (2188/2189): the talent's work is its PROC_TRIGGER_SPELL effect, which the built-in proc case keyed on the
        // talent's family and icon rewrites (vmangos UnitAuraProcHandler.cpp:1226-1262, 1468-1498); the class script effect itself is read nowhere.
        new(ExternalReaderKind.ClassScript, 2689, 0, "Spells/Procs/BuiltInProcHandlers.cs", @"case FamilyPaladin when aura\.SpellIconId == IlluminationIcon:"),
        new(ExternalReaderKind.ClassScript, 2188, 0, "Spells/Procs/BuiltInProcHandlers.cs", @"case FamilyWarlock when aura\.SpellIconId == PyroclasmIcon:"),
        new(ExternalReaderKind.ClassScript, 2189, 0, "Spells/Procs/BuiltInProcHandlers.cs", @"case FamilyWarlock when aura\.SpellIconId == PyroclasmIcon:"),
        // Improved Healthstone (CreateHealthstoneScript.TalentRank): the rank constants and the read.
        new(ExternalReaderKind.DummySpell, CreateHealthstoneScript.ImprovedHealthstoneRank1, 0, "Spells/Warlock/CreateHealthstoneScript.cs",
            @"ImprovedHealthstoneRank1 = 18692;"),
        new(ExternalReaderKind.DummySpell, CreateHealthstoneScript.ImprovedHealthstoneRank1, 0, "Spells/Warlock/CreateHealthstoneScript.cs",
            @"holder\.Spell\.Id is not \(ImprovedHealthstoneRank1 or ImprovedHealthstoneRank2\)"),
        new(ExternalReaderKind.DummySpell, CreateHealthstoneScript.ImprovedHealthstoneRank2, 0, "Spells/Warlock/CreateHealthstoneScript.cs",
            @"ImprovedHealthstoneRank2 = 18693;"),
        new(ExternalReaderKind.DummySpell, CreateHealthstoneScript.ImprovedHealthstoneRank2, 0, "Spells/Warlock/CreateHealthstoneScript.cs",
            @"holder\.Spell\.Id is not \(ImprovedHealthstoneRank1 or ImprovedHealthstoneRank2\)"),

        // Tactical Mastery (ShapeshiftService.GetTacticalMasteryRage): the script table and the read on a stance change.
        new(ExternalReaderKind.ClassScript, 831, 0, "Spells/Stances/ShapeshiftService.cs", @"\(831, 50\)"),
        new(ExternalReaderKind.ClassScript, 832, 0, "Spells/Stances/ShapeshiftService.cs", @"\(832, 100\)"),
        new(ExternalReaderKind.ClassScript, 833, 0, "Spells/Stances/ShapeshiftService.cs", @"\(833, 150\)"),
        new(ExternalReaderKind.ClassScript, 834, 0, "Spells/Stances/ShapeshiftService.cs", @"\(834, 200\)"),
        new(ExternalReaderKind.ClassScript, 835, 0, "Spells/Stances/ShapeshiftService.cs", @"\(835, 250\)"),
        new(ExternalReaderKind.ClassScript, 831, 0, "Spells/Stances/ShapeshiftService.cs", @"GetTacticalMasteryRage\(target\)"),

        // Leader of the Pack (ShapeshiftService): knowing the talent spell casts the party aura 24932 on entering Cat, Bear or Dire Bear Form
        // (vmangos Player::_ApplyShapeshiftFormBoost / CastFormBoost); the dummy aura itself does nothing.
        new(ExternalReaderKind.DummySpell, FormBoostTable.LeaderOfThePackKnownSpell, 0, "Spells/Stances/ShapeshiftService.cs",
            @"known\.Contains\(FormBoostTable\.LeaderOfThePackKnownSpell\)"),

        // Seal of Command (the talent's seal, 20375): its third effect's dummy aura names the judgement (20425), which Judgement reads off every seal
        // (vmangos SpellEffects.cpp:4502-4529, "all seals have judgement's aura dummy spell id in 2 effect").
        new(ExternalReaderKind.DummySpell, 20375, 0, "Spells/Paladin/PaladinSpellScripts.cs",
            @"holder\.Auras\[JudgementEffectIndex\] is not \{ Type: AuraType\.Dummy \}"),

        // Healing Way (29203, the stacking DUMMY aura the talent puts on a healed target): HealingWayRules multiplies the healing taken from Healing Wave
        // (vmangos Unit::SpellHealingBonusTaken; cmangos Shaman.cpp HealingWay OnDamageCalculate).
        new(ExternalReaderKind.DummySpell, HealingWayRules.HealingWayAura, 0, "Spells/Shaman/HealingWayRules.cs", @"holder\.Spell\.Id != HealingWayAura\b"),

        // Spirit of Redemption (20711): the killing blow on a priest with the talent aura makes the spirit (vmangos Unit::Kill, Unit.cpp:1108-1140).
        new(ExternalReaderKind.DummySpell, SpellSystem.SpiritOfRedemptionTalent, 0, "Spells/SpellSystem.SpiritOfRedemption.cs",
            @"HasAura\(priest, SpiritOfRedemptionTalent\)"),

        // Demonic Sacrifice: removed when a demon is summoned (SummonService) and kept through death (ResurrectEffects).
        new(ExternalReaderKind.ClassScript, (uint)DemonicSacrificeScript.ClassScriptMisc, 0, "Pets/SummonService.Demons.cs",
            @"a\.MiscValue == DemonicSacrificeScript\.ClassScriptMisc"),
        new(ExternalReaderKind.ClassScript, (uint)DemonicSacrificeScript.ClassScriptMisc, 0, "Spells/Effects/ResurrectEffects.cs",
            @"OverrideClassScripts, MiscValue: 2228 \}"),
    ];

    private static readonly HashSet<uint> DummySpellReaders = KeysOf(ExternalReaderKind.DummySpell);

    private static readonly HashSet<uint> DummyIconReaders = KeysOf(ExternalReaderKind.DummyIcon);

    private static readonly HashSet<(uint Family, uint Icon)> DummyFamilyIconReaders
        = ExternalReaders.Where(r => r.Kind == ExternalReaderKind.DummyFamilyIcon).Select(r => (r.Family, r.Key)).ToHashSet();

    private static readonly HashSet<int> ClassScriptReaders = KeysOf(ExternalReaderKind.ClassScript).Select(key => (int)key).ToHashSet();
    /// <summary>
    /// OVERRIDE_CLASS_SCRIPTS misc values of talents whose work is entirely another effect of the same spell, and which vmangos reads nowhere either:
    /// Arcane Concentration (1388-1392; the generic PROC_TRIGGER_SPELL of Clearcasting 12536 with the spell_proc_event row is the whole talent).
    /// </summary>
    public static IReadOnlySet<int> InertClassScripts { get; } = new HashSet<int> { 1388, 1389, 1390, 1391, 1392 };

    private static HashSet<uint> KeysOf(ExternalReaderKind kind) => ExternalReaders.Where(r => r.Kind == kind).Select(r => r.Key).ToHashSet();

    public static TalentCoverageReport Build(TalentCatalog catalog, SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(spells);
        int total = 0;
        int supported = 0;
        List<TalentCoverageGap> gaps = [];
        IReadOnlySet<uint> scriptIds = InstalledSpellScripts(spells);
        foreach (TalentRecord talent in catalog.Talents)
        {
            for (int rank = 0; rank < talent.RankSpells.Count; rank++)
            {
                uint spellId = talent.RankSpells[rank];
                if (spellId == 0)
                {
                    continue;
                }

                total++;
                int before = gaps.Count;
                if (spells.Store.Get(spellId) is not { } spell)
                {
                    gaps.Add(new TalentCoverageGap(talent.Id, rank, spellId, TalentGapKind.SpellMissing, 0));
                    continue;
                }

                foreach (SpellEffectInfo effect in spell.Effects)
                {
                    if (effect.Effect == SpellEffectName.None)
                    {
                        continue;
                    }

                    if (!spells.HasEffectHandler(effect.Effect))
                    {
                        gaps.Add(new TalentCoverageGap(talent.Id, rank, spellId, TalentGapKind.MissingEffect, (int)effect.Effect));
                    }

                    if (effect.Effect != SpellEffectName.ApplyAura)
                    {
                        continue;
                    }

                    // DUMMY and OVERRIDE_CLASS_SCRIPTS do nothing on apply (vmangos HandleAuraDummy for talents, HandleNoImmediateEffect): whether one
                    // has an apply handler says nothing, only a consumer makes the talent work.
                    if (effect.AuraType is AuraType.Dummy or AuraType.OverrideClassScripts)
                    {
                        if (!HasConsumer(spells, spell, effect, scriptIds))
                        {
                            gaps.Add(new TalentCoverageGap(talent.Id, rank, spellId, TalentGapKind.NoConsumer, (int)effect.AuraType));
                        }
                    }
                    else if (!spells.HasAuraHandler(effect.AuraType))
                    {
                        gaps.Add(new TalentCoverageGap(talent.Id, rank, spellId, TalentGapKind.MissingAura, (int)effect.AuraType));
                    }
                }

                if (gaps.Count == before)
                {
                    supported++;
                }
            }
        }

        return new TalentCoverageReport(total, supported, gaps);
    }

    /// <summary>Whether something reads the DUMMY or OVERRIDE_CLASS_SCRIPTS aura <paramref name="effect"/> of <paramref name="spell"/>.</summary>
    public static bool HasConsumer(SpellSystem spells, SpellInfo spell, SpellEffectInfo effect)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(effect);
        return HasConsumer(spells, spell, effect, InstalledSpellScripts(spells));
    }

    private static bool HasConsumer(SpellSystem spells, SpellInfo spell, SpellEffectInfo effect, IReadOnlySet<uint> scriptIds)
    {
        if (spells.FindProcScript(spell) is not null || spells.FindPeriodicDamageScript(spell.Id) is not null
            || spells.HasPeriodicTriggerScript(spell.Id) || scriptIds.Contains(spell.Id))
        {
            return true;
        }

        return effect.AuraType == AuraType.Dummy
            ? spells.HasDummyAuraHandler(spell.Id) || spells.HasPeriodicDummyAura(spell.Id)
                || DummySpellReaders.Contains(spell.Id) || DummyIconReaders.Contains(spell.SpellIconId)
                || DummyFamilyIconReaders.Contains((spell.SpellFamilyName, spell.SpellIconId))
            : ClassScriptReaders.Contains(effect.MiscValue) || InertClassScripts.Contains(effect.MiscValue) || SpellCritRules.ShatterBonuses.ContainsKey(effect.MiscValue)
                || BuiltInProcHandlers.HandledClassScripts.Contains(effect.MiscValue);
    }

    /// <summary>The spell ids of the installed spell script dispatcher (none when no dispatcher runs the scripts, so none consume anything).</summary>
    private static IReadOnlySet<uint> InstalledSpellScripts(SpellSystem spells)
        => spells.Observers.OfType<SpellScriptDispatcher>().FirstOrDefault() is { } dispatcher ? dispatcher.Registry.SpellIds.ToHashSet() : [];
}

/// <summary>What an <see cref="ExternalReader"/> is keyed on.</summary>
public enum ExternalReaderKind
{
    /// <summary>A DUMMY aura of the spell id <see cref="ExternalReader.Key"/>.</summary>
    DummySpell,

    /// <summary>A DUMMY aura of a spell with SpellIconID <see cref="ExternalReader.Key"/>.</summary>
    DummyIcon,

    /// <summary>A DUMMY aura of a spell of family <see cref="ExternalReader.Family"/> with SpellIconID <see cref="ExternalReader.Key"/>.</summary>
    DummyFamilyIcon,

    /// <summary>An OVERRIDE_CLASS_SCRIPTS aura of misc value <see cref="ExternalReader.Key"/>.</summary>
    ClassScript,
}

/// <summary>
/// One reading site of a DUMMY or OVERRIDE_CLASS_SCRIPTS talent aura outside the script registries: <see cref="Site"/> (a path under
/// src/ArcaneCore.Game) holds a code line that matches the regular expression <see cref="Pattern"/>. A key may have several sites.
/// </summary>
public sealed record ExternalReader(ExternalReaderKind Kind, uint Key, uint Family, string Site, string Pattern);
