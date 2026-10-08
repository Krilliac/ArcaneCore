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
    /// DUMMY auras read outside the script registries, by SpellIconID: Furor (238, ShapeshiftService.RollFuror) and Predatory Strikes (1563,
    /// FormStatListener).
    /// </summary>
    private static readonly HashSet<uint> DummyIconReaders = [ShapeshiftService.FurorIconId, FormStatListener.PredatoryStrikesIconId];

    /// <summary>DUMMY auras read outside the script registries, by family and SpellIconID: Improved Life Tap (warlock 208, LifeTapScript).</summary>
    private static readonly HashSet<(uint Family, uint Icon)> DummyFamilyIconReaders = [(SoulShardRules.WarlockFamily, LifeTapScript.ImprovedLifeTapIcon)];

    /// <summary>
    /// DUMMY auras read outside the script registries, by spell id: Frost Warding (11189, 28332) and Improved Fire Ward (11094, 13043)
    /// (HardcodedMods.WardMask), Improved Drain Mana (17864, 18393, DrainAuras).
    /// </summary>
    private static readonly HashSet<uint> DummySpellReaders = [11189, 28332, 11094, 13043, 17864, 18393];

    /// <summary>
    /// The OVERRIDE_CLASS_SCRIPTS misc values a rule reads: Tactical Mastery (831-835, ShapeshiftService), Demonic Sacrifice (2228,
    /// SummonService and ResurrectEffects); Shatter (<see cref="SpellCritRules.ShatterBonuses"/>) and the built-in proc cases are added below.
    /// </summary>
    private static readonly HashSet<int> ClassScriptReaders = [831, 832, 833, 834, 835, DemonicSacrificeScript.ClassScriptMisc];

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
