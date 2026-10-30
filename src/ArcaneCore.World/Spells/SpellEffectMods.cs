using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// The vmangos <c>spell_effect_mod</c> corrections (world 66, <see cref="SpellEffectModRow"/>) over the built spells: vmangos
/// SpellModMgr::LoadSpellMods (src/game/Spells/SpellModMgr.cpp:195-262) overwrites one effect of a Spell.dbc entry column by column, a -1
/// keeping the spell's value (ModUInt32ValueIfExplicit and friends, :48-70: the unsigned columns apply when non-negative, the signed and
/// float ones when not -1).
/// <para>
/// A correction applies only to an effect slot the world's spell data leaves empty. The spell data of record is the world database's
/// <c>spell_template</c> (the DBC import with classic-db's own corrections), so a vmangos row that rewrites an effect it defines would
/// stack one core's fix over the other's; those rows are left out and logged, as classic-db's rows win over vmangos' spell scripts. The
/// importer writes only reviewed rows (<c>SpellEffectModDumpImporter</c>); this guard holds for a table filled any other way too.
/// </para>
/// </summary>
internal static class SpellEffectMods
{
    /// <summary><paramref name="spell"/> with its corrections applied (the same instance when none applies).</summary>
    public static SpellInfo Apply(SpellInfo spell, IEnumerable<SpellEffectModRow> mods, Func<uint, float> radius, ILogger logger)
    {
        SpellEffectInfo[]? effects = null;
        foreach (SpellEffectModRow mod in mods.OrderBy(m => m.EffectIndex))
        {
            if (mod.EffectIndex >= SpellConstants.MaxEffects)
            {
                logger.LogWarning("spell_effect_mod: spell {Spell} effect {Effect} is beyond the {Max} effects of a spell, ignored", mod.Id, mod.EffectIndex,
                    SpellConstants.MaxEffects);
                continue;
            }

            if (!spell.Effects[mod.EffectIndex].IsEmpty)
            {
                logger.LogWarning("spell_effect_mod: spell {Spell} effect {Effect} is defined by spell_template, which wins; the correction is not applied",
                    mod.Id, mod.EffectIndex);
                continue;
            }

            effects ??= [.. spell.Effects];
            effects[mod.EffectIndex] = Apply(effects[mod.EffectIndex], mod, radius);
        }

        return effects is null ? spell : spell with { Effects = effects };
    }

    private static SpellEffectInfo Apply(SpellEffectInfo e, SpellEffectModRow m, Func<uint, float> radius) => e with
    {
        Effect = m.Effect >= 0 ? (SpellEffectName)m.Effect : e.Effect,
        AuraType = m.EffectApplyAuraName >= 0 ? (AuraType)m.EffectApplyAuraName : e.AuraType,
        Mechanic = m.EffectMechanic >= 0 ? (uint)m.EffectMechanic : e.Mechanic,
        TargetA = m.EffectImplicitTargetA >= 0 ? (SpellImplicitTarget)m.EffectImplicitTargetA : e.TargetA,
        TargetB = m.EffectImplicitTargetB >= 0 ? (SpellImplicitTarget)m.EffectImplicitTargetB : e.TargetB,
        Radius = m.EffectRadiusIndex >= 0 ? radius((uint)m.EffectRadiusIndex) : e.Radius,
        // SpellEffectInfo keeps the DBC's 32-bit EffectItemType (SpellStoreFactory.ToSpellInfo); vmangos reads a uint64 here.
        ItemType = m.EffectItemType >= 0 ? unchecked((uint)m.EffectItemType) : e.ItemType,
        MiscValue = m.EffectMiscValue != -1 ? m.EffectMiscValue : e.MiscValue,
        TriggerSpell = m.EffectTriggerSpell >= 0 ? (uint)m.EffectTriggerSpell : e.TriggerSpell,
        DieSides = m.EffectDieSides != -1 ? m.EffectDieSides : e.DieSides,
        BaseDice = m.EffectBaseDice >= 0 ? m.EffectBaseDice : e.BaseDice,
        BasePoints = m.EffectBasePoints != -1 ? m.EffectBasePoints : e.BasePoints,
        Amplitude = m.EffectAmplitude >= 0 ? (uint)m.EffectAmplitude : e.Amplitude,
        ChainTarget = m.EffectChainTarget >= 0 ? (uint)m.EffectChainTarget : e.ChainTarget,
        DicePerLevel = m.EffectDicePerLevel != -1f ? m.EffectDicePerLevel : e.DicePerLevel,
        RealPointsPerLevel = m.EffectRealPointsPerLevel != -1f ? m.EffectRealPointsPerLevel : e.RealPointsPerLevel,
        PointsPerComboPoint = m.EffectPointsPerComboPoint != -1f ? m.EffectPointsPerComboPoint : e.PointsPerComboPoint,
        MultipleValue = m.EffectMultipleValue != -1f ? m.EffectMultipleValue : e.MultipleValue,
    };
}
