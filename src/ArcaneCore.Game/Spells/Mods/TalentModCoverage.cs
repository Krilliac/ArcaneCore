using System.Text;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Talents;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>Why a modifier effect of a talent rank spell would not work.</summary>
public enum TalentModGapKind
{
    /// <summary>The class mask is empty (neither the spell data nor the overlay has one): the modifier affects no spell.</summary>
    NoClassMask,

    /// <summary>The operation (the effect's misc value) is outside <see cref="SpellModOp"/>'s range: vmangos ignores such an aura.</summary>
    UnknownOperation,
}

/// <summary>One modifier effect that cannot work. <paramref name="Detail"/> is the offending operation number for <see cref="TalentModGapKind.UnknownOperation"/>, else 0.</summary>
public readonly record struct TalentModGap(uint Talent, int RankIndex, uint Spell, int EffectIndex, TalentModGapKind Kind, int Detail);

/// <summary>The talent modifier effects (aura 107 and 108) of the catalog and which of them cannot work.</summary>
public sealed class TalentModCoverageReport
{
    internal TalentModCoverageReport(int modEffects, IReadOnlyDictionary<int, int> byOperation, List<TalentModGap> gaps)
    {
        ModEffects = modEffects;
        ByOperation = byOperation;
        Gaps = gaps;
    }

    /// <summary>Modifier effects on talent rank spells.</summary>
    public int ModEffects { get; }

    /// <summary>How many modifier effects use each operation number.</summary>
    public IReadOnlyDictionary<int, int> ByOperation { get; }

    public IReadOnlyList<TalentModGap> Gaps { get; }

    public int CountOf(TalentModGapKind kind) => Gaps.Count(g => g.Kind == kind);

    /// <summary>A deterministic summary for the startup log.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("Talent modifiers: ").Append(ModEffects).Append(" modifier effect(s) on talent rank spells, ")
            .Append(CountOf(TalentModGapKind.NoClassMask)).Append(" with an empty class mask, ")
            .Append(CountOf(TalentModGapKind.UnknownOperation)).Append(" with an unknown operation.");
        foreach ((int op, int count) in ByOperation.OrderBy(p => p.Key))
        {
            text.AppendLine().Append("  op ").Append(op).Append(" (").Append(op is >= 0 and < (int)SpellModOp.Max ? ((SpellModOp)op).ToString() : "unknown").Append("): ").Append(count);
        }

        return text.ToString();
    }
}

/// <summary>
/// Read-only report over the talent catalog, the spell table and the installed modifier engine: how many talent rank spells carry
/// a spell-modifier aura, and which of them cannot work (no class mask from the spell data or the overlay, or an operation vmangos
/// ignores). Layered on <see cref="TalentEffectCoverage"/>, which reports a missing handler; with the engine installed that report
/// has no gap for aura 107 or 108.
/// </summary>
public static class TalentModCoverage
{
    public static TalentModCoverageReport Build(TalentCatalog catalog, SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(spells);
        ISpellModEngine engine = spells.Mods;
        int effects = 0;
        var byOp = new Dictionary<int, int>();
        List<TalentModGap> gaps = [];
        foreach (TalentRecord talent in catalog.Talents)
        {
            for (int rank = 0; rank < talent.RankSpells.Count; rank++)
            {
                if (talent.RankSpells[rank] == 0 || spells.Store.Get(talent.RankSpells[rank]) is not { } spell)
                {
                    continue;
                }

                for (int i = 0; i < spell.Effects.Count; i++)
                {
                    SpellEffectInfo effect = spell.Effects[i];
                    if (effect.Effect != SpellEffectName.ApplyAura || effect.AuraType is not (AuraType.AddFlatModifier or AuraType.AddPctModifier))
                    {
                        continue;
                    }

                    effects++;
                    byOp[effect.MiscValue] = byOp.GetValueOrDefault(effect.MiscValue) + 1;
                    if (effect.MiscValue is < 0 or >= (int)SpellModOp.Max)
                    {
                        gaps.Add(new TalentModGap(talent.Id, rank, spell.Id, i, TalentModGapKind.UnknownOperation, effect.MiscValue));
                    }
                    else if (engine.ClassMask(spell, i) == 0)
                    {
                        gaps.Add(new TalentModGap(talent.Id, rank, spell.Id, i, TalentModGapKind.NoClassMask, 0));
                    }
                }
            }
        }

        return new TalentModCoverageReport(effects, byOp, gaps);
    }
}
