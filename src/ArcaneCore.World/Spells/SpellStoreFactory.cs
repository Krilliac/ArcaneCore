using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Resolves the world-database spell rows into a <see cref="SpellStore"/>: the DBC index columns
/// (CastingTimeIndex, DurationIndex, RangeIndex, EffectRadiusIndexN) are replaced by their
/// rows, as cmangos-classic does with sSpellCastTimesStore and friends. Rows that reference a
/// missing spell are skipped and logged (cmangos ObjectMgr::LoadPlayerInfo / SpellMgr::LoadSpellTargetPositions
/// do the same with an error-log line).
/// </summary>
public static class SpellStoreFactory
{
    public static SpellStore Build(SpellContent content, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        logger ??= NullLogger.Instance;

        Dictionary<uint, SpellCastTimeRow> castTimes = content.CastTimes.ToDictionary(r => r.Id);
        Dictionary<uint, SpellDurationRow> durations = content.Durations.ToDictionary(r => r.Id);
        Dictionary<uint, SpellRangeRow> ranges = content.Ranges.ToDictionary(r => r.Id);
        Dictionary<uint, SpellRadiusRow> radii = content.Radii.ToDictionary(r => r.Id);

        var spells = new List<SpellInfo>(content.Spells.Count);
        foreach (SpellTemplateRow row in content.Spells)
        {
            spells.Add(ToSpellInfo(row, castTimes, durations, ranges, radii));
        }

        var known = spells.Select(s => s.Id).ToHashSet();
        var createSpells = new List<(byte, byte, uint)>();
        foreach (PlayerCreateSpellRow row in content.CreateSpells)
        {
            if (!known.Contains(row.Spell))
            {
                logger.LogWarning("playercreateinfo_spell: race {Race} class {Class} references unknown spell {Spell}, skipped", row.Race, row.Class, row.Spell);
                continue;
            }

            createSpells.Add((row.Race, row.Class, row.Spell));
        }

        var positions = new List<(uint, SpellTargetPosition)>();
        foreach (SpellTargetPositionRow row in content.TargetPositions)
        {
            if (!known.Contains(row.Id))
            {
                logger.LogWarning("spell_target_position: unknown spell {Spell}, skipped", row.Id);
                continue;
            }

            positions.Add((row.Id, new SpellTargetPosition(row.TargetMap, row.TargetPositionX, row.TargetPositionY, row.TargetPositionZ, row.TargetOrientation)));
        }

        return new SpellStore(spells, createSpells, positions,
            content.ScriptTargets.Where(r => known.Contains(r.SpellId))
                .Select(r => new SpellStore.ScriptTarget(r.SpellId, r.Type, r.TargetEntry, r.InverseEffectMask)));
    }

    /// <summary>One <c>spell_template</c> row as a <see cref="SpellInfo"/> (indices resolved; an unknown index reads as 0, like a DBC lookup miss).</summary>
    public static SpellInfo ToSpellInfo(
        SpellTemplateRow row,
        IReadOnlyDictionary<uint, SpellCastTimeRow> castTimes,
        IReadOnlyDictionary<uint, SpellDurationRow> durations,
        IReadOnlyDictionary<uint, SpellRangeRow> ranges,
        IReadOnlyDictionary<uint, SpellRadiusRow> radii)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(castTimes);
        ArgumentNullException.ThrowIfNull(durations);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(radii);

        SpellCastTime castTime = castTimes.TryGetValue(row.CastingTimeIndex, out SpellCastTimeRow? ct)
            ? new SpellCastTime(ct.CastTime, ct.CastTimePerLevel, ct.MinCastTime)
            : default;

        // vmangos SpellEntry::GetDuration: no SpellDuration row means 0 (instant, no aura time).
        SpellDuration duration = durations.TryGetValue(row.DurationIndex, out SpellDurationRow? d)
            ? new SpellDuration(d.Duration, d.DurationPerLevel, d.MaxDuration)
            : default;

        SpellRange range = ranges.TryGetValue(row.RangeIndex, out SpellRangeRow? r)
            ? new SpellRange(r.MinRange, r.MaxRange)
            : default;

        float Radius(uint index) => radii.TryGetValue(index, out SpellRadiusRow? radius) ? radius.Radius : 0f;

        return new SpellInfo
        {
            Id = row.Id,
            School = (SpellSchool)row.School,
            Category = row.Category,
            Dispel = row.Dispel,
            Mechanic = row.Mechanic,
            Attributes = (SpellAttributes)row.Attributes,
            AttributesEx = (SpellAttributesEx)row.AttributesEx,
            AttributesEx2 = (SpellAttributesEx2)row.AttributesEx2,
            AttributesEx3 = row.AttributesEx3,
            AttributesEx4 = row.AttributesEx4,
            Targets = row.Targets,
            TargetCreatureType = row.TargetCreatureType,
            RequiresSpellFocus = row.RequiresSpellFocus,
            CastTime = castTime,
            RecoveryTime = row.RecoveryTime,
            CategoryRecoveryTime = row.CategoryRecoveryTime,
            InterruptFlags = (SpellInterruptFlags)row.InterruptFlags,
            AuraInterruptFlags = (SpellAuraInterruptFlags)row.AuraInterruptFlags,
            ChannelInterruptFlags = (SpellAuraInterruptFlags)row.ChannelInterruptFlags,
            MaxLevel = row.MaxLevel,
            BaseLevel = row.BaseLevel,
            SpellLevel = row.SpellLevel,
            Duration = duration,
            PowerType = unchecked((int)row.PowerType),
            ManaCost = row.ManaCost,
            ManaCostPerLevel = row.ManaCostPerlevel,
            ManaPerSecond = row.ManaPerSecond,
            ManaPerSecondPerLevel = row.ManaPerSecondPerLevel,
            ManaCostPercentage = row.ManaCostPercentage,
            Reagents =
            [
                new(row.Reagent1, row.ReagentCount1), new(row.Reagent2, row.ReagentCount2),
                new(row.Reagent3, row.ReagentCount3), new(row.Reagent4, row.ReagentCount4),
                new(row.Reagent5, row.ReagentCount5), new(row.Reagent6, row.ReagentCount6),
                new(row.Reagent7, row.ReagentCount7), new(row.Reagent8, row.ReagentCount8),
            ],
            RangeIndex = row.RangeIndex,
            Range = range,
            Speed = row.Speed,
            StackAmount = row.StackAmount,
            ProcCharges = row.ProcCharges,
            Stances = row.Stances,
            StancesNot = row.StancesNot,
            CasterAuraState = (AuraState)row.CasterAuraState,
            TargetAuraState = (AuraState)row.TargetAuraState,
            ProcFlags = (ProcFlags)row.ProcFlags,
            ProcChance = row.ProcChance,
            EquippedItemClass = row.EquippedItemClass,
            EquippedItemSubClassMask = row.EquippedItemSubClassMask,
            EquippedItemInventoryTypeMask = row.EquippedItemInventoryTypeMask,
            Totems = Totems(row),
            StartRecoveryCategory = row.StartRecoveryCategory,
            StartRecoveryTime = row.StartRecoveryTime,
            DamageClass = (SpellDamageClass)row.DmgClass,
            PreventionType = row.PreventionType,
            SpellFamilyName = row.SpellFamilyName,
            SpellFamilyFlags = row.SpellFamilyFlags,
            MaxTargetLevel = row.MaxTargetLevel,
            MaxAffectedTargets = row.MaxAffectedTargets,
            SpellVisual = row.SpellVisual,
            SpellIconId = row.SpellIconID,
            ActiveIconId = row.ActiveIconID,
            Name = row.SpellName ?? string.Empty,
            Rank = row.Rank ?? string.Empty,
            Effects =
            [
                Effect(row.Effect1, row.EffectDieSides1, row.EffectBaseDice1, row.EffectDicePerLevel1, row.EffectRealPointsPerLevel1,
                    row.EffectBasePoints1, row.EffectMechanic1, row.EffectImplicitTargetA1, row.EffectImplicitTargetB1, Radius(row.EffectRadiusIndex1),
                    row.EffectApplyAuraName1, row.EffectAmplitude1, row.EffectMultipleValue1, row.EffectChainTarget1, row.EffectItemType1,
                    row.EffectMiscValue1, row.EffectTriggerSpell1, row.EffectPointsPerComboPoint1) with { DamageMultiplier = row.DmgMultiplier1 },
                Effect(row.Effect2, row.EffectDieSides2, row.EffectBaseDice2, row.EffectDicePerLevel2, row.EffectRealPointsPerLevel2,
                    row.EffectBasePoints2, row.EffectMechanic2, row.EffectImplicitTargetA2, row.EffectImplicitTargetB2, Radius(row.EffectRadiusIndex2),
                    row.EffectApplyAuraName2, row.EffectAmplitude2, row.EffectMultipleValue2, row.EffectChainTarget2, row.EffectItemType2,
                    row.EffectMiscValue2, row.EffectTriggerSpell2, row.EffectPointsPerComboPoint2) with { DamageMultiplier = row.DmgMultiplier2 },
                Effect(row.Effect3, row.EffectDieSides3, row.EffectBaseDice3, row.EffectDicePerLevel3, row.EffectRealPointsPerLevel3,
                    row.EffectBasePoints3, row.EffectMechanic3, row.EffectImplicitTargetA3, row.EffectImplicitTargetB3, Radius(row.EffectRadiusIndex3),
                    row.EffectApplyAuraName3, row.EffectAmplitude3, row.EffectMultipleValue3, row.EffectChainTarget3, row.EffectItemType3,
                    row.EffectMiscValue3, row.EffectTriggerSpell3, row.EffectPointsPerComboPoint3) with { DamageMultiplier = row.DmgMultiplier3 },
            ],
        };
    }

    // Crafting lane: Totem[2] (vmangos SpellEntry.h:635); a zero Totem slot is absent (Spell.cpp:7292). The eight reagent slots are kept as they are (SpellInfo.Reagents).
    private static IReadOnlyList<uint> Totems(SpellTemplateRow row)
    {
        var list = new List<uint>(2);
        if (row.Totem1 != 0)
        {
            list.Add(row.Totem1);
        }

        if (row.Totem2 != 0)
        {
            list.Add(row.Totem2);
        }

        return list;
    }

    private static SpellEffectInfo Effect(
        uint effect, int dieSides, uint baseDice, float dicePerLevel, float realPointsPerLevel, int basePoints, uint mechanic,
        uint targetA, uint targetB, float radius, uint aura, uint amplitude, float multipleValue, uint chainTarget, uint itemType,
        int miscValue, uint triggerSpell, float pointsPerComboPoint) => new()
    {
        Effect = (SpellEffectName)effect,
        DieSides = dieSides,
        BaseDice = unchecked((int)baseDice),
        DicePerLevel = dicePerLevel,
        RealPointsPerLevel = realPointsPerLevel,
        BasePoints = basePoints,
        Mechanic = mechanic,
        TargetA = (SpellImplicitTarget)targetA,
        TargetB = (SpellImplicitTarget)targetB,
        Radius = radius,
        AuraType = (AuraType)aura,
        Amplitude = amplitude,
        MultipleValue = multipleValue,
        ChainTarget = chainTarget,
        ItemType = itemType,
        MiscValue = miscValue,
        TriggerSpell = triggerSpell,
        PointsPerComboPoint = pointsPerComboPoint,
    };
}
