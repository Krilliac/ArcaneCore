using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Talents;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>One part of a spell the spell system does not implement (<see cref="QaSpellGaps"/> codes).</summary>
/// <param name="EffectIndex">The effect (0..2).</param>
/// <param name="Code">The gap code (<see cref="QaSpellGaps.Effect"/>, <see cref="QaSpellGaps.Aura"/>, ...).</param>
/// <param name="Detail">What is missing, by name (the effect, the aura type).</param>
/// <param name="EffectName">The effect's name.</param>
/// <param name="EffectValue">The effect's base value (BasePoints + 1).</param>
/// <param name="Aura">The aura type number of an aura effect (0 when none).</param>
internal readonly record struct QaSpellGap(int EffectIndex, string Code, string Detail, string EffectName, int EffectValue, uint Aura);

/// <summary>
/// Which parts of a spell nothing implements, against the live registries of one <see cref="SpellSystem"/> (the TalentEffectCoverage approach, for
/// any spell): an effect with no handler; an aura type with no handler that <see cref="AuraSupport"/> marks Unsupported (a Referenced type is read by a
/// rule and works); a DUMMY or OVERRIDE_CLASS_SCRIPTS aura nothing reads (<see cref="TalentEffectCoverage.HasConsumer"/>, a dummy-aura case or a periodic
/// dummy tick); a DUMMY effect no spell script claims, and a SCRIPT_EFFECT neither the script-effect table nor a spell script claims, each only when it is
/// the spell's sole working effect (a DUMMY next to a damage effect is usually cosmetic; the mage teleports' empty SCRIPT_EFFECT is vmangos-exact).
/// An implicit target the system cannot select is only known when a cast meets it (<see cref="ISpellCastObserver.OnUnsupported"/>).
/// <para>
/// Read the registries after start-up (the modules and the spell script dispatcher register while the world starts); the script id sets are read
/// once, on the first call. Not thread-safe: one instance per thread (the world thread's monitor, the QA task's coverage each own one).
/// </para>
/// </summary>
internal sealed class PlayerbotQaSpellGapAnalyzer(SpellSystem system)
{
    private static readonly AuraSupportLevel[] s_auraLevels = BuildAuraLevels();
    private HashSet<uint>? _spellScripts;
    private HashSet<uint>? _scriptEffects;

    internal SpellSystem System => system;

    /// <summary>The gaps of <paramref name="spell"/>, in effect order; empty when it is fully handled.</summary>
    internal QaSpellGap[] Of(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        _spellScripts ??= system.Observers.OfType<SpellScriptDispatcher>().FirstOrDefault() is { } dispatcher ? [.. dispatcher.Registry.SpellIds] : [];
        _scriptEffects ??= [.. ScriptEffectRegistry.For(system).SpellIds];
        List<QaSpellGap>? gaps = null;
        IReadOnlyList<SpellEffectInfo> effects = spell.Effects;
        for (int i = 0; i < effects.Count; i++)
        {
            SpellEffectInfo effect = effects[i];
            if (effect.IsEmpty) continue;
            string name = effect.Effect.ToString();
            int value = effect.BasePoints + 1;
            if (!system.HasEffectHandler(effect.Effect))
            {
                (gaps ??= []).Add(new QaSpellGap(i, QaSpellGaps.Effect, name, name, value, 0));
                continue;
            }

            if (IsAuraEffect(effect.Effect) && effect.AuraType != AuraType.None)
            {
                if (AuraGap(spell, effect) is { } code) (gaps ??= []).Add(new QaSpellGap(i, code, effect.AuraType.ToString(), name, value, (uint)effect.AuraType));
                continue;
            }

            bool scripted = _spellScripts.Contains(spell.Id);
            if (effect.Effect == SpellEffectName.Dummy && !scripted && !OtherEffectWorks(spell, i))
            {
                (gaps ??= []).Add(new QaSpellGap(i, QaSpellGaps.Dummy, name, name, value, 0));
            }
            else if (effect.Effect == SpellEffectName.ScriptEffect && !scripted && !_scriptEffects.Contains(spell.Id) && !OtherEffectWorks(spell, i))
            {
                (gaps ??= []).Add(new QaSpellGap(i, QaSpellGaps.Script, name, name, value, 0));
            }
        }

        return gaps is null ? [] : [.. gaps];
    }

    /// <summary>The effects that carry an aura type (vmangos EffectApplyAura, EffectPersistentAA, EffectApplyAreaAura).</summary>
    internal static bool IsAuraEffect(SpellEffectName effect)
        => effect is SpellEffectName.ApplyAura or SpellEffectName.PersistentAreaAura or SpellEffectName.ApplyAreaAuraParty;

    /// <summary>The level <see cref="AuraSupport"/> gives <paramref name="type"/> (Unsupported for a value outside the table).</summary>
    internal static AuraSupportLevel LevelOf(AuraType type) => (uint)type < (uint)s_auraLevels.Length ? s_auraLevels[(int)type] : AuraSupportLevel.Unsupported;

    private string? AuraGap(SpellInfo spell, SpellEffectInfo effect)
    {
        AuraType type = effect.AuraType;
        if (type is AuraType.Dummy or AuraType.OverrideClassScripts)
        {
            bool read = system.HasDummyAuraHandler(spell.Id) || system.HasPeriodicDummyAura(spell.Id) || TalentEffectCoverage.HasConsumer(system, spell, effect);
            return read ? null : QaSpellGaps.NoConsumer;
        }

        return !system.HasAuraHandler(type) && LevelOf(type) == AuraSupportLevel.Unsupported ? QaSpellGaps.Aura : null;
    }

    /// <summary>Whether an effect other than <paramref name="index"/> does something (a handled effect that is not DUMMY or SCRIPT_EFFECT).</summary>
    private bool OtherEffectWorks(SpellInfo spell, int index)
    {
        IReadOnlyList<SpellEffectInfo> effects = spell.Effects;
        for (int j = 0; j < effects.Count; j++)
        {
            SpellEffectInfo other = effects[j];
            if (j == index || other.IsEmpty || other.Effect is SpellEffectName.Dummy or SpellEffectName.ScriptEffect) continue;
            if (!system.HasEffectHandler(other.Effect)) continue;
            if (IsAuraEffect(other.Effect) && other.AuraType != AuraType.None && AuraGap(spell, other) is not null) continue;
            return true;
        }

        return false;
    }

    private static AuraSupportLevel[] BuildAuraLevels()
    {
        int max = AuraSupport.Entries.Count == 0 ? 0 : AuraSupport.Entries.Max(e => (int)e.Type);
        var levels = new AuraSupportLevel[max + 1];
        Array.Fill(levels, AuraSupportLevel.Unsupported);
        foreach (AuraSupportEntry entry in AuraSupport.Entries) levels[(int)entry.Type] = entry.Level;
        return levels;
    }
}
