using System.Text;
using ArcaneCore.Game.Spells;
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

    /// <summary>Rank spells whose every effect has a handler.</summary>
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
        _ => "spell",
    };
}

/// <summary>
/// Read-only report over the talent catalog, the spell table and the registered handlers: which talent rank spells carry an
/// effect or aura nothing implements yet. Learning such a talent is still allowed (as in retail) and spends the point, so this
/// is the visible, measurable list of the work the spell-modifier, proc and stat-aura areas still owe the talents.
/// </summary>
public static class TalentEffectCoverage
{
    public static TalentCoverageReport Build(TalentCatalog catalog, SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(spells);
        int total = 0;
        int supported = 0;
        List<TalentCoverageGap> gaps = [];
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

                    if (effect.Effect == SpellEffectName.ApplyAura && !spells.HasAuraHandler(effect.AuraType))
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
}
