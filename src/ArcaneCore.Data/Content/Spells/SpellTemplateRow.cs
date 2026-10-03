namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// One <c>spell_template</c> row: the Spell.dbc-equivalent table of the world database. Every
/// column carries the cmangos-classic name and type (classic-db <c>spell_template</c>, schema
/// z2691) so the M8 content importer and <c>tools/spell-import</c> map rows by column name.
/// Generated from the cmangos DDL; do not rename properties.
/// </summary>
public sealed class SpellTemplateRow
{
    public uint Id { get; set; }

    public uint School { get; set; }

    public uint Category { get; set; }

    public uint Dispel { get; set; }

    public uint Mechanic { get; set; }

    public uint Attributes { get; set; }

    public uint AttributesEx { get; set; }

    public uint AttributesEx2 { get; set; }

    public uint AttributesEx3 { get; set; }

    public uint AttributesEx4 { get; set; }

    public uint Stances { get; set; }

    public uint StancesNot { get; set; }

    public uint Targets { get; set; }

    public uint TargetCreatureType { get; set; }

    public uint RequiresSpellFocus { get; set; }

    public uint CasterAuraState { get; set; }

    public uint TargetAuraState { get; set; }

    public uint CastingTimeIndex { get; set; }

    public uint RecoveryTime { get; set; }

    public uint CategoryRecoveryTime { get; set; }

    public uint InterruptFlags { get; set; }

    public uint AuraInterruptFlags { get; set; }

    public uint ChannelInterruptFlags { get; set; }

    public uint ProcFlags { get; set; }

    public uint ProcChance { get; set; }

    public uint ProcCharges { get; set; }

    public uint MaxLevel { get; set; }

    public uint BaseLevel { get; set; }

    public uint SpellLevel { get; set; }

    public uint DurationIndex { get; set; }

    public uint PowerType { get; set; }

    public uint ManaCost { get; set; }

    public uint ManaCostPerlevel { get; set; }

    public uint ManaPerSecond { get; set; }

    public uint ManaPerSecondPerLevel { get; set; }

    public uint RangeIndex { get; set; }

    public float Speed { get; set; }

    public uint StackAmount { get; set; }

    public uint Totem1 { get; set; }

    public uint Totem2 { get; set; }

    public int Reagent1 { get; set; }

    public int Reagent2 { get; set; }

    public int Reagent3 { get; set; }

    public int Reagent4 { get; set; }

    public int Reagent5 { get; set; }

    public int Reagent6 { get; set; }

    public int Reagent7 { get; set; }

    public int Reagent8 { get; set; }

    public uint ReagentCount1 { get; set; }

    public uint ReagentCount2 { get; set; }

    public uint ReagentCount3 { get; set; }

    public uint ReagentCount4 { get; set; }

    public uint ReagentCount5 { get; set; }

    public uint ReagentCount6 { get; set; }

    public uint ReagentCount7 { get; set; }

    public uint ReagentCount8 { get; set; }

    public int EquippedItemClass { get; set; }

    public int EquippedItemSubClassMask { get; set; }

    public int EquippedItemInventoryTypeMask { get; set; }

    public uint Effect1 { get; set; }

    public uint Effect2 { get; set; }

    public uint Effect3 { get; set; }

    public int EffectDieSides1 { get; set; }

    public int EffectDieSides2 { get; set; }

    public int EffectDieSides3 { get; set; }

    public uint EffectBaseDice1 { get; set; }

    public uint EffectBaseDice2 { get; set; }

    public uint EffectBaseDice3 { get; set; }

    public float EffectDicePerLevel1 { get; set; }

    public float EffectDicePerLevel2 { get; set; }

    public float EffectDicePerLevel3 { get; set; }

    public float EffectRealPointsPerLevel1 { get; set; }

    public float EffectRealPointsPerLevel2 { get; set; }

    public float EffectRealPointsPerLevel3 { get; set; }

    public int EffectBasePoints1 { get; set; }

    public int EffectBasePoints2 { get; set; }

    public int EffectBasePoints3 { get; set; }

    public uint EffectMechanic1 { get; set; }

    public uint EffectMechanic2 { get; set; }

    public uint EffectMechanic3 { get; set; }

    public uint EffectImplicitTargetA1 { get; set; }

    public uint EffectImplicitTargetA2 { get; set; }

    public uint EffectImplicitTargetA3 { get; set; }

    public uint EffectImplicitTargetB1 { get; set; }

    public uint EffectImplicitTargetB2 { get; set; }

    public uint EffectImplicitTargetB3 { get; set; }

    public uint EffectRadiusIndex1 { get; set; }

    public uint EffectRadiusIndex2 { get; set; }

    public uint EffectRadiusIndex3 { get; set; }

    public uint EffectApplyAuraName1 { get; set; }

    public uint EffectApplyAuraName2 { get; set; }

    public uint EffectApplyAuraName3 { get; set; }

    public uint EffectAmplitude1 { get; set; }

    public uint EffectAmplitude2 { get; set; }

    public uint EffectAmplitude3 { get; set; }

    public float EffectMultipleValue1 { get; set; }

    public float EffectMultipleValue2 { get; set; }

    public float EffectMultipleValue3 { get; set; }

    public uint EffectChainTarget1 { get; set; }

    public uint EffectChainTarget2 { get; set; }

    public uint EffectChainTarget3 { get; set; }

    public uint EffectItemType1 { get; set; }

    public uint EffectItemType2 { get; set; }

    public uint EffectItemType3 { get; set; }

    public int EffectMiscValue1 { get; set; }

    public int EffectMiscValue2 { get; set; }

    public int EffectMiscValue3 { get; set; }

    public uint EffectTriggerSpell1 { get; set; }

    public uint EffectTriggerSpell2 { get; set; }

    public uint EffectTriggerSpell3 { get; set; }

    public float EffectPointsPerComboPoint1 { get; set; }

    public float EffectPointsPerComboPoint2 { get; set; }

    public float EffectPointsPerComboPoint3 { get; set; }

    public uint SpellVisual { get; set; }

    public uint SpellIconID { get; set; }

    public uint ActiveIconID { get; set; }

    public uint SpellPriority { get; set; }

    public string? SpellName { get; set; }

    public string? SpellName2 { get; set; }

    public string? SpellName3 { get; set; }

    public string? SpellName4 { get; set; }

    public string? SpellName5 { get; set; }

    public string? SpellName6 { get; set; }

    public string? SpellName7 { get; set; }

    public string? SpellName8 { get; set; }

    public string? Rank { get; set; }

    public string? Rank2 { get; set; }

    public string? Rank3 { get; set; }

    public string? Rank4 { get; set; }

    public string? Rank5 { get; set; }

    public string? Rank6 { get; set; }

    public string? Rank7 { get; set; }

    public string? Rank8 { get; set; }

    public uint ManaCostPercentage { get; set; }

    public uint StartRecoveryCategory { get; set; }

    public uint StartRecoveryTime { get; set; }

    public uint MaxTargetLevel { get; set; }

    public uint SpellFamilyName { get; set; }

    public ulong SpellFamilyFlags { get; set; }

    public uint MaxAffectedTargets { get; set; }

    public uint DmgClass { get; set; }

    public uint PreventionType { get; set; }

    public float DmgMultiplier1 { get; set; }

    public float DmgMultiplier2 { get; set; }

    public float DmgMultiplier3 { get; set; }

    public uint IsServerSide { get; set; }
}
