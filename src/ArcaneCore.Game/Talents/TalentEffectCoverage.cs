using System.Text;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Scripts;
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

        // Tactical Mastery (ShapeshiftService.GetTacticalMasteryRage): the script table and the read on a stance change.
        new(ExternalReaderKind.ClassScript, 831, 0, "Spells/Stances/ShapeshiftService.cs", @"\(831, 50\)"),
        new(ExternalReaderKind.ClassScript, 832, 0, "Spells/Stances/ShapeshiftService.cs", @"\(832, 100\)"),
        new(ExternalReaderKind.ClassScript, 833, 0, "Spells/Stances/ShapeshiftService.cs", @"\(833, 150\)"),
        new(ExternalReaderKind.ClassScript, 834, 0, "Spells/Stances/ShapeshiftService.cs", @"\(834, 200\)"),
        new(ExternalReaderKind.ClassScript, 835, 0, "Spells/Stances/ShapeshiftService.cs", @"\(835, 250\)"),
        new(ExternalReaderKind.ClassScript, 831, 0, "Spells/Stances/ShapeshiftService.cs", @"GetTacticalMasteryRage\(target\)"),

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
            ? DummySpellReaders.Contains(spell.Id) || DummyIconReaders.Contains(spell.SpellIconId)
                || DummyFamilyIconReaders.Contains((spell.SpellFamilyName, spell.SpellIconId))
            : ClassScriptReaders.Contains(effect.MiscValue) || SpellCritRules.ShatterBonuses.ContainsKey(effect.MiscValue)
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
