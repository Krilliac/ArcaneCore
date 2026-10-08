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
        UnreadOperations = byOperation
            .Where(p => p.Key is >= 0 and < (int)SpellModOp.Max && !TalentModCoverage.Readers.ContainsKey((SpellModOp)p.Key))
            .ToDictionary(p => (SpellModOp)p.Key, p => p.Value);
    }

    /// <summary>Modifier effects on talent rank spells.</summary>
    public int ModEffects { get; }

    /// <summary>How many modifier effects use each operation number.</summary>
    public IReadOnlyDictionary<int, int> ByOperation { get; }

    public IReadOnlyList<TalentModGap> Gaps { get; }

    public int CountOf(TalentModGapKind kind) => Gaps.Count(g => g.Kind == kind);

    /// <summary>
    /// The operations of the catalog's modifier effects that nothing in the engine reads (not in <see cref="TalentModCoverage.Readers"/>), with
    /// how many effects use each: the modifier is stored and sent to the client, but changes nothing on the server.
    /// </summary>
    public IReadOnlyDictionary<SpellModOp, int> UnreadOperations { get; }

    /// <summary>Modifier effects whose operation nothing reads.</summary>
    public int UnreadEffects => UnreadOperations.Values.Sum();

    /// <summary>A deterministic summary for the startup log.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("Talent modifiers: ").Append(ModEffects).Append(" modifier effect(s) on talent rank spells, ")
            .Append(CountOf(TalentModGapKind.NoClassMask)).Append(" with an empty class mask, ")
            .Append(CountOf(TalentModGapKind.UnknownOperation)).Append(" with an unknown operation, ")
            .Append(UnreadEffects).Append(" with an operation nothing reads.");
        foreach ((int op, int count) in ByOperation.OrderBy(p => p.Key))
        {
            text.AppendLine().Append("  op ").Append(op).Append(" (").Append(op is >= 0 and < (int)SpellModOp.Max ? ((SpellModOp)op).ToString() : "unknown").Append("): ").Append(count);
            if (op is >= 0 and < (int)SpellModOp.Max && UnreadOperations.ContainsKey((SpellModOp)op))
            {
                text.Append(", nothing reads it");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Read-only report over the talent catalog, the spell table and the installed modifier engine: how many talent rank spells carry
/// a spell-modifier aura, and which of them cannot work (no class mask from the spell data or the overlay, or an operation vmangos
/// ignores), plus the operations nothing in the engine reads yet (<see cref="Readers"/>). Layered on <see cref="TalentEffectCoverage"/>,
/// which reports a missing handler; with the engine installed that report has no gap for aura 107 or 108.
/// </summary>
public static class TalentModCoverage
{
    /// <summary>
    /// The operations something in the engine reads, with where (docs/areas/spell-mods.md). An operation missing here is reported by
    /// <see cref="TalentModCoverageReport.UnreadOperations"/>; SPEED and ATTACK_POWER (an aura's own amount, vmangos SpellAuras.cpp:3982, 4016,
    /// 5086-5219) and CHARGES (at holder creation, :6693) have no reader yet. Wiring one adds it here, in the same change; a test compares
    /// this list with the reads in the source.
    /// </summary>
    public static IReadOnlyDictionary<SpellModOp, string> Readers { get; } = new Dictionary<SpellModOp, string>
    {
        [SpellModOp.Damage] = "SpellBonusModule, SpellSystem.Combat",
        [SpellModOp.Duration] = "SpellModValueAdapter",
        [SpellModOp.Threat] = "SpellThreatModifiers",
        [SpellModOp.Range] = "SpellSystem (range checks)",
        [SpellModOp.Radius] = "SpellSystem.Targeting, SpellSystem.AreaAuras, SpellSystem.PersistentAreaAuras",
        [SpellModOp.CriticalChance] = "SpellCombatRules.CritChance",
        [SpellModOp.AllEffects] = "SpellSystem.Effects, SpellSystem.PersistentAreaAuras, PaladinProcScripts",
        [SpellModOp.NotLoseCastingTime] = "SpellSystem.Pushback",
        [SpellModOp.CastingTime] = "SpellModValueAdapter",
        [SpellModOp.Cooldown] = "SpellSystem (cooldowns)",
        [SpellModOp.Cost] = "SpellSystem.Seams (power cost)",
        [SpellModOp.CritDamageBonus] = "SpellCombatRules (crit damage)",
        [SpellModOp.ResistMissChance] = "SpellCombatRules (hit chance), SpellSystem.Auras (aura amount)",
        [SpellModOp.JumpTargets] = "SpellSystem.Targeting (chain targets)",
        [SpellModOp.ChanceOfSuccess] = "SpellSystem.Procs, SpellSystem.ItemCombatProcs",
        [SpellModOp.ActivationTime] = "SpellSystem.Auras (periodic amplitude)",
        [SpellModOp.EffectPastFirst] = "SpellSystem.Targeting (chain factor)",
        [SpellModOp.GlobalCooldown] = "SpellSystem (global cooldown)",
        [SpellModOp.Dot] = "SpellBonusModule (periodic snapshot)",
        [SpellModOp.Haste] = "AttackSpeedAuras",
        [SpellModOp.SpellBonusDamage] = "SpellBonusModule (coefficient)",
        [SpellModOp.MultipleValue] = "SpellSystem.Combat, SpellSystem.PowerBurn, SpellSystem.Mitigation, DrainAuras",
        [SpellModOp.ResistDispelChance] = "SpellSystem.Dispel",
    };

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
