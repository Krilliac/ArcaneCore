namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// Converts the 1.12.1 client's Spell.dbc, SpellCastTimes.dbc, SpellDuration.dbc,
/// SpellRange.dbc and SpellRadius.dbc into the world-database spell tables. The Spell.dbc field
/// indices are cmangos-classic DBCStructure.h <c>SpellEntry</c> (173 fields, build 5875); the
/// columns are its <c>spell_template</c> names. Generated mapping — keep it in field order.
/// </summary>
public static class SpellDbcImporter
{
    /// <summary>Spell.dbc of build 5875 has 173 four-byte fields.</summary>
    public const int SpellFieldCount = 173;

    /// <summary>Read the five DBCs from <paramref name="directory"/> (file names as in the client's DBFilesClient).</summary>
    public static SpellDbcContent ReadDirectory(string directory) => Read(
        DbcFile.Load(Path.Combine(directory, "Spell.dbc")),
        DbcFile.Load(Path.Combine(directory, "SpellCastTimes.dbc")),
        DbcFile.Load(Path.Combine(directory, "SpellDuration.dbc")),
        DbcFile.Load(Path.Combine(directory, "SpellRange.dbc")),
        DbcFile.Load(Path.Combine(directory, "SpellRadius.dbc")));

    public static SpellDbcContent Read(DbcFile spells, DbcFile castTimes, DbcFile durations, DbcFile ranges, DbcFile radii)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(castTimes);
        ArgumentNullException.ThrowIfNull(durations);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(radii);
        Require(spells, "Spell.dbc", SpellFieldCount);
        Require(castTimes, "SpellCastTimes.dbc", 4);
        Require(durations, "SpellDuration.dbc", 4);
        RequireAtLeast(ranges, "SpellRange.dbc", 4);
        Require(radii, "SpellRadius.dbc", 4);

        var spellRows = new List<SpellTemplateRow>(spells.RecordCount);
        for (int r = 0; r < spells.RecordCount; r++)
        {
            spellRows.Add(ReadSpell(spells, r));
        }

        var castTimeRows = new List<SpellCastTimeRow>(castTimes.RecordCount);
        for (int r = 0; r < castTimes.RecordCount; r++)
        {
            castTimeRows.Add(new SpellCastTimeRow
            {
                Id = castTimes.GetUInt32(r, 0),
                CastTime = castTimes.GetInt32(r, 1),
                CastTimePerLevel = castTimes.GetInt32(r, 2),
                MinCastTime = castTimes.GetInt32(r, 3),
            });
        }

        var durationRows = new List<SpellDurationRow>(durations.RecordCount);
        for (int r = 0; r < durations.RecordCount; r++)
        {
            durationRows.Add(new SpellDurationRow
            {
                Id = durations.GetUInt32(r, 0),
                Duration = durations.GetInt32(r, 1),
                DurationPerLevel = durations.GetInt32(r, 2),
                MaxDuration = durations.GetInt32(r, 3),
            });
        }

        var rangeRows = new List<SpellRangeRow>(ranges.RecordCount);
        for (int r = 0; r < ranges.RecordCount; r++)
        {
            rangeRows.Add(new SpellRangeRow
            {
                Id = ranges.GetUInt32(r, 0),
                MinRange = ranges.GetFloat(r, 1),
                MaxRange = ranges.GetFloat(r, 2),
                Flags = ranges.GetUInt32(r, 3),
            });
        }

        var radiusRows = new List<SpellRadiusRow>(radii.RecordCount);
        for (int r = 0; r < radii.RecordCount; r++)
        {
            radiusRows.Add(new SpellRadiusRow
            {
                Id = radii.GetUInt32(r, 0),
                Radius = radii.GetFloat(r, 1),
                RadiusPerLevel = radii.GetFloat(r, 2),
                RadiusMax = radii.GetFloat(r, 3),
            });
        }

        return new SpellDbcContent(spellRows, castTimeRows, durationRows, rangeRows, radiusRows);
    }

    /// <summary>One Spell.dbc record (field indices: cmangos-classic DBCStructure.h SpellEntry).</summary>
    public static SpellTemplateRow ReadSpell(DbcFile dbc, int r)
    {
        ArgumentNullException.ThrowIfNull(dbc);
        return new SpellTemplateRow
        {
            Id = dbc.GetUInt32(r, 0),
            School = dbc.GetUInt32(r, 1),
            Category = dbc.GetUInt32(r, 2),
            Dispel = dbc.GetUInt32(r, 4),
            Mechanic = dbc.GetUInt32(r, 5),
            Attributes = dbc.GetUInt32(r, 6),
            AttributesEx = dbc.GetUInt32(r, 7),
            AttributesEx2 = dbc.GetUInt32(r, 8),
            AttributesEx3 = dbc.GetUInt32(r, 9),
            AttributesEx4 = dbc.GetUInt32(r, 10),
            Stances = dbc.GetUInt32(r, 11),
            StancesNot = dbc.GetUInt32(r, 12),
            Targets = dbc.GetUInt32(r, 13),
            TargetCreatureType = dbc.GetUInt32(r, 14),
            RequiresSpellFocus = dbc.GetUInt32(r, 15),
            CasterAuraState = dbc.GetUInt32(r, 16),
            TargetAuraState = dbc.GetUInt32(r, 17),
            CastingTimeIndex = dbc.GetUInt32(r, 18),
            RecoveryTime = dbc.GetUInt32(r, 19),
            CategoryRecoveryTime = dbc.GetUInt32(r, 20),
            InterruptFlags = dbc.GetUInt32(r, 21),
            AuraInterruptFlags = dbc.GetUInt32(r, 22),
            ChannelInterruptFlags = dbc.GetUInt32(r, 23),
            ProcFlags = dbc.GetUInt32(r, 24),
            ProcChance = dbc.GetUInt32(r, 25),
            ProcCharges = dbc.GetUInt32(r, 26),
            MaxLevel = dbc.GetUInt32(r, 27),
            BaseLevel = dbc.GetUInt32(r, 28),
            SpellLevel = dbc.GetUInt32(r, 29),
            DurationIndex = dbc.GetUInt32(r, 30),
            PowerType = dbc.GetUInt32(r, 31),
            ManaCost = dbc.GetUInt32(r, 32),
            ManaCostPerlevel = dbc.GetUInt32(r, 33),
            ManaPerSecond = dbc.GetUInt32(r, 34),
            ManaPerSecondPerLevel = dbc.GetUInt32(r, 35),
            RangeIndex = dbc.GetUInt32(r, 36),
            Speed = dbc.GetFloat(r, 37),
            StackAmount = dbc.GetUInt32(r, 39),
            Totem1 = dbc.GetUInt32(r, 40),
            Totem2 = dbc.GetUInt32(r, 41),
            Reagent1 = dbc.GetInt32(r, 42),
            Reagent2 = dbc.GetInt32(r, 43),
            Reagent3 = dbc.GetInt32(r, 44),
            Reagent4 = dbc.GetInt32(r, 45),
            Reagent5 = dbc.GetInt32(r, 46),
            Reagent6 = dbc.GetInt32(r, 47),
            Reagent7 = dbc.GetInt32(r, 48),
            Reagent8 = dbc.GetInt32(r, 49),
            ReagentCount1 = dbc.GetUInt32(r, 50),
            ReagentCount2 = dbc.GetUInt32(r, 51),
            ReagentCount3 = dbc.GetUInt32(r, 52),
            ReagentCount4 = dbc.GetUInt32(r, 53),
            ReagentCount5 = dbc.GetUInt32(r, 54),
            ReagentCount6 = dbc.GetUInt32(r, 55),
            ReagentCount7 = dbc.GetUInt32(r, 56),
            ReagentCount8 = dbc.GetUInt32(r, 57),
            EquippedItemClass = dbc.GetInt32(r, 58),
            EquippedItemSubClassMask = dbc.GetInt32(r, 59),
            EquippedItemInventoryTypeMask = dbc.GetInt32(r, 60),
            Effect1 = dbc.GetUInt32(r, 61),
            Effect2 = dbc.GetUInt32(r, 62),
            Effect3 = dbc.GetUInt32(r, 63),
            EffectDieSides1 = dbc.GetInt32(r, 64),
            EffectDieSides2 = dbc.GetInt32(r, 65),
            EffectDieSides3 = dbc.GetInt32(r, 66),
            EffectBaseDice1 = dbc.GetUInt32(r, 67),
            EffectBaseDice2 = dbc.GetUInt32(r, 68),
            EffectBaseDice3 = dbc.GetUInt32(r, 69),
            EffectDicePerLevel1 = dbc.GetFloat(r, 70),
            EffectDicePerLevel2 = dbc.GetFloat(r, 71),
            EffectDicePerLevel3 = dbc.GetFloat(r, 72),
            EffectRealPointsPerLevel1 = dbc.GetFloat(r, 73),
            EffectRealPointsPerLevel2 = dbc.GetFloat(r, 74),
            EffectRealPointsPerLevel3 = dbc.GetFloat(r, 75),
            EffectBasePoints1 = dbc.GetInt32(r, 76),
            EffectBasePoints2 = dbc.GetInt32(r, 77),
            EffectBasePoints3 = dbc.GetInt32(r, 78),
            EffectMechanic1 = dbc.GetUInt32(r, 79),
            EffectMechanic2 = dbc.GetUInt32(r, 80),
            EffectMechanic3 = dbc.GetUInt32(r, 81),
            EffectImplicitTargetA1 = dbc.GetUInt32(r, 82),
            EffectImplicitTargetA2 = dbc.GetUInt32(r, 83),
            EffectImplicitTargetA3 = dbc.GetUInt32(r, 84),
            EffectImplicitTargetB1 = dbc.GetUInt32(r, 85),
            EffectImplicitTargetB2 = dbc.GetUInt32(r, 86),
            EffectImplicitTargetB3 = dbc.GetUInt32(r, 87),
            EffectRadiusIndex1 = dbc.GetUInt32(r, 88),
            EffectRadiusIndex2 = dbc.GetUInt32(r, 89),
            EffectRadiusIndex3 = dbc.GetUInt32(r, 90),
            EffectApplyAuraName1 = dbc.GetUInt32(r, 91),
            EffectApplyAuraName2 = dbc.GetUInt32(r, 92),
            EffectApplyAuraName3 = dbc.GetUInt32(r, 93),
            EffectAmplitude1 = dbc.GetUInt32(r, 94),
            EffectAmplitude2 = dbc.GetUInt32(r, 95),
            EffectAmplitude3 = dbc.GetUInt32(r, 96),
            EffectMultipleValue1 = dbc.GetFloat(r, 97),
            EffectMultipleValue2 = dbc.GetFloat(r, 98),
            EffectMultipleValue3 = dbc.GetFloat(r, 99),
            EffectChainTarget1 = dbc.GetUInt32(r, 100),
            EffectChainTarget2 = dbc.GetUInt32(r, 101),
            EffectChainTarget3 = dbc.GetUInt32(r, 102),
            EffectItemType1 = dbc.GetUInt32(r, 103),
            EffectItemType2 = dbc.GetUInt32(r, 104),
            EffectItemType3 = dbc.GetUInt32(r, 105),
            EffectMiscValue1 = dbc.GetInt32(r, 106),
            EffectMiscValue2 = dbc.GetInt32(r, 107),
            EffectMiscValue3 = dbc.GetInt32(r, 108),
            EffectTriggerSpell1 = dbc.GetUInt32(r, 109),
            EffectTriggerSpell2 = dbc.GetUInt32(r, 110),
            EffectTriggerSpell3 = dbc.GetUInt32(r, 111),
            EffectPointsPerComboPoint1 = dbc.GetFloat(r, 112),
            EffectPointsPerComboPoint2 = dbc.GetFloat(r, 113),
            EffectPointsPerComboPoint3 = dbc.GetFloat(r, 114),
            SpellVisual = dbc.GetUInt32(r, 115),
            SpellIconID = dbc.GetUInt32(r, 117),
            ActiveIconID = dbc.GetUInt32(r, 118),
            SpellPriority = dbc.GetUInt32(r, 119),
            SpellName = dbc.GetString(r, 120),
            SpellName2 = dbc.GetString(r, 121),
            SpellName3 = dbc.GetString(r, 122),
            SpellName4 = dbc.GetString(r, 123),
            SpellName5 = dbc.GetString(r, 124),
            SpellName6 = dbc.GetString(r, 125),
            SpellName7 = dbc.GetString(r, 126),
            SpellName8 = dbc.GetString(r, 127),
            Rank = dbc.GetString(r, 129),
            Rank2 = dbc.GetString(r, 130),
            Rank3 = dbc.GetString(r, 131),
            Rank4 = dbc.GetString(r, 132),
            Rank5 = dbc.GetString(r, 133),
            Rank6 = dbc.GetString(r, 134),
            Rank7 = dbc.GetString(r, 135),
            Rank8 = dbc.GetString(r, 136),
            ManaCostPercentage = dbc.GetUInt32(r, 156),
            StartRecoveryCategory = dbc.GetUInt32(r, 157),
            StartRecoveryTime = dbc.GetUInt32(r, 158),
            MaxTargetLevel = dbc.GetUInt32(r, 159),
            SpellFamilyName = dbc.GetUInt32(r, 160),
            SpellFamilyFlags = dbc.GetUInt32(r, 161) | ((ulong)dbc.GetUInt32(r, 162) << 32),
            MaxAffectedTargets = dbc.GetUInt32(r, 163),
            DmgClass = dbc.GetUInt32(r, 164),
            PreventionType = dbc.GetUInt32(r, 165),
            DmgMultiplier1 = dbc.GetFloat(r, 167),
            DmgMultiplier2 = dbc.GetFloat(r, 168),
            DmgMultiplier3 = dbc.GetFloat(r, 169),
            IsServerSide = 0,
        };
    }

    private static void Require(DbcFile file, string name, int fields)
    {
        if (file.FieldCount != fields)
        {
            throw new InvalidDataException($"{name} has {file.FieldCount} fields; build 5875 has {fields}");
        }
    }

    private static void RequireAtLeast(DbcFile file, string name, int fields)
    {
        if (file.FieldCount < fields)
        {
            throw new InvalidDataException($"{name} has {file.FieldCount} fields; at least {fields} expected");
        }
    }
}
