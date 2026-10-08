namespace ArcaneCore.Data.ClientData;

/// <summary>
/// The expected layout of one build-5875 DBC: its field count (every 1.12.1 field is four bytes, so the record size is four times
/// that) and where the layout comes from. <see cref="Format"/> is the vmangos format string when vmangos loads the file
/// (one character per field: n index, i int, f float, s string, d/x ignored), otherwise null and <see cref="Source"/> names the
/// ArcaneCore reader whose field count is enforced.
/// </summary>
public sealed record ClientDbcLayout(string File, int Fields, string? Format, string Source, int? PackedRecordSize = null)
{
    /// <summary>
    /// The record size in bytes: four bytes a field, except for a file with packed byte fields (CharStartOutfit declares 41 fields
    /// in 152-byte records), whose size is <see cref="PackedRecordSize"/>.
    /// </summary>
    public int RecordSize => PackedRecordSize ?? Fields * 4;
}

/// <summary>
/// The reference layouts of the build-5875 DBCs the core reads or cross-references. The vmangos rows are transcribed from
/// vmangos src/game/Database/DBCfmt.h (the build &gt; 1.10.2 variants of ChrClasses and ItemSet); vmangos reads Faction,
/// FactionTemplate, CharStartOutfit and Spell from its world database instead, so those four are the field counts the ArcaneCore
/// readers require. A file of the full client set that is not listed here has no reference layout: only its WDBC header is checked.
/// </summary>
public static class ClientDbcLayouts
{
    private static ClientDbcLayout V(string file, string format, string name) => new(file, format.Length, format, "vmangos DBCfmt.h " + name);

    private static ClientDbcLayout A(string file, int fields, string reader, int? packedRecordSize = null)
        => new(file, fields, null, "ArcaneCore " + reader, packedRecordSize);

    /// <summary>Every reference layout, by file name (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, ClientDbcLayout> All { get; } = new ClientDbcLayout[]
    {
        V("AreaTable.dbc", "niiiixxxxxissssssssxixxxi", "AreaTableEntryfmt"),
        V("AreaTrigger.dbc", "niffffffff", "AreaTriggerEntryfmt"),
        V("AuctionHouse.dbc", "niiixxxxxxxxx", "AuctionHouseEntryfmt"),
        V("BankBagSlotPrices.dbc", "ni", "BankBagSlotPricesEntryfmt"),
        V("CharSections.dbc", "diiiiixxxi", "CharSectionsEntryfmt"),
        V("CharacterFacialHairStyles.dbc", "iiixxxxxx", "CharacterFacialHairStylesfmt"),
        V("ChrClasses.dbc", "nxxixssssssssxxix", "ChrClassesEntryfmt"),
        V("ChrRaces.dbc", "niixiixxiiiiixixissssssssxxxx", "ChrRacesEntryfmt"),
        V("ChatChannels.dbc", "nixssssssssxxxxxxxxxx", "ChatChannelsEntryfmt"),
        V("CinematicSequences.dbc", "nxxxxxxxxx", "CinematicSequencesEntryfmt"),
        V("CreatureDisplayInfo.dbc", "nixifxxxxxxx", "CreatureDisplayInfofmt"),
        V("CreatureDisplayInfoExtra.dbc", "nixxxxxxxxxxxxxxxxx", "CreatureDisplayInfoExtrafmt"),
        V("CreatureModelData.dbc", "nisxfxxxxxxxxxxf", "CreatureModelDatafmt"),
        V("CreatureFamily.dbc", "nfifiiiissssssssxx", "CreatureFamilyfmt"),
        V("CreatureSpellData.dbc", "niiiixxxx", "CreatureSpellDatafmt"),
        V("CreatureType.dbc", "nxxxxxxxxxx", "CreatureTypefmt"),
        V("DurabilityCosts.dbc", "niiiiiiiiiiiiiiiiiiiiiiiiiiiii", "DurabilityCostsfmt"),
        V("DurabilityQuality.dbc", "nf", "DurabilityQualityfmt"),
        V("Emotes.dbc", "nsxiiix", "EmotesEntryfmt"),
        V("EmotesText.dbc", "nxixxxxxxxxxxxxxxxx", "EmotesTextEntryfmt"),
        V("GameObjectDisplayInfo.dbc", "nsxxxxxxxxxx", "GameObjectDisplayInfofmt"),
        V("ItemBagFamily.dbc", "nxxxxxxxxx", "ItemBagFamilyfmt"),
        V("ItemDisplayInfo.dbc", "nxxxxxxxxxxixxxxxxxxxxx", "ItemDisplayTemplateEntryfmt (commented out there)"),
        V("ItemRandomProperties.dbc", "nsiiixxssssssssx", "ItemRandomPropertiesfmt"),
        V("ItemSet.dbc", "dssssssssxxxxxxxxxxxxxxxxxxiiiiiiiiiiiiiiiiii", "ItemSetEntryfmt"),
        V("LiquidType.dbc", "niii", "LiquidTypefmt"),
        V("Lock.dbc", "niiiiiiiiiiiiiiiiiiiiiiiixxxxxxxx", "LockEntryfmt"),
        V("MailTemplate.dbc", "nxxxxxxxxx", "MailTemplateEntryfmt"),
        V("Map.dbc", "nxixssssssssxxxxxxxixxxxxxxxxxxxxxxxxxixxx", "MapEntryfmt"),
        V("NamesProfanity.dbc", "ds", "NamesProfanityEntryfmt"),
        V("NamesReserved.dbc", "ds", "NamesReservedEntryfmt"),
        V("QuestSort.dbc", "nxxxxxxxxx", "QuestSortEntryfmt"),
        V("SkillLine.dbc", "nixssssssssxxxxxxxxxxi", "SkillLinefmt"),
        V("SkillLineAbility.dbc", "niiiixxiiiiixxi", "SkillLineAbilityfmt"),
        V("SkillRaceClassInfo.dbc", "diiiiiix", "SkillRaceClassInfofmt"),
        V("SkillTiers.dbc", "niiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii", "SkillTiersfmt"),
        V("SpellCategory.dbc", "ni", "SpellCategoryfmt"),
        V("SpellCastTimes.dbc", "niii", "SpellCastTimefmt"),
        V("SpellDuration.dbc", "niii", "SpellDurationfmt"),
        V("SpellFocusObject.dbc", "nxxxxxxxxx", "SpellFocusObjectfmt"),
        V("SpellItemEnchantment.dbc", "niiiiiixxxiiissssssssxii", "SpellItemEnchantmentfmt"),
        V("SpellRadius.dbc", "nfxx", "SpellRadiusfmt"),
        V("SpellRange.dbc", "nffxxxxxxxxxxxxxxxxxxx", "SpellRangefmt"),
        V("SpellShapeshiftForm.dbc", "nxssssssssxiix", "SpellShapeshiftfmt"),
        V("SpellVisual.dbc", "niiiiiiiiiiiiiii", "SpellVisualfmt"),
        V("StableSlotPrices.dbc", "ni", "StableSlotPricesfmt"),
        V("Talent.dbc", "niiiiiiiixxxxixxixxxi", "TalentEntryfmt"),
        V("TalentTab.dbc", "nxxxxxxxxxxxiix", "TalentTabEntryfmt"),
        V("TaxiNodes.dbc", "nifffssssssssxii", "TaxiNodesEntryfmt"),
        V("TaxiPath.dbc", "niii", "TaxiPathEntryfmt"),
        V("TaxiPathNode.dbc", "diiifffii", "TaxiPathNodeEntryfmt"),
        V("TransportAnimation.dbc", "diifffx", "TransportAnimationfmt"),
        V("WMOAreaTable.dbc", "niiixxxxxiixxxxxxxxx", "WMOAreaTableEntryfmt"),
        V("WorldMapArea.dbc", "xinxffff", "WorldMapAreaEntryfmt"),
        V("WorldSafeLocs.dbc", "nifffxxxxxxxxx", "WorldSafeLocsEntryfmt"),
        A("Faction.dbc", 37, "FactionDbcReader"),
        A("FactionTemplate.dbc", 14, "FactionTemplateDbcReader"),
        A("CharStartOutfit.dbc", 41, "CharStartOutfitDbcReader", packedRecordSize: 152),
        A("Spell.dbc", 173, "SpellDbcImporter"),
    }.ToDictionary(l => l.File, StringComparer.OrdinalIgnoreCase);

    /// <summary>The reference layout of <paramref name="file"/>, or null when it has none.</summary>
    public static ClientDbcLayout? Find(string file) => All.GetValueOrDefault(file);
}
