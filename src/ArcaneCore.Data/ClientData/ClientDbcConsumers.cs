namespace ArcaneCore.Data.ClientData;

/// <summary>
/// One configuration key through which a world daemon feature reads a client DBC.
/// <paramref name="Group"/> names keys that must be set together (the feature refuses to start with only some of them), so
/// <c>ClientData:DbcDirectory</c> fills either all of a group's unset keys or none. <paramref name="Fallback"/> is what the
/// feature does without the file.
/// </summary>
public sealed record ClientDbcConsumer(string Key, string File, string Feature, string Fallback, string? Group = null);

/// <summary>
/// One configuration key whose value is a directory of client DBCs (a feature that reads several files and treats each as optional,
/// <c>World:GmCommands:LiveFxDbcDirectory</c>). <c>ClientData:DbcDirectory</c> fills it with itself when the key is unset.
/// </summary>
public sealed record ClientDbcDirectoryConsumer(string Key, string Feature, string Fallback);

/// <summary>
/// Every per-file DBC path key of the world daemon (the inventory of docs/areas/client-data.md). A test walks the options
/// classes and fails when a property named <c>*DbcPath</c> or <c>*DbcDirectory</c> is not listed here (in <see cref="All"/> or
/// <see cref="Directories"/>), so a new consumer cannot miss the <c>ClientData:DbcDirectory</c> fallback.
/// </summary>
public static class ClientDbcConsumers
{
    private const string Off = "the feature stays off";

    public static IReadOnlyList<ClientDbcConsumer> All { get; } =
    [
        new("CharacterCreation:CharSectionsDbcPath", "CharSections.dbc", "character creation appearance check", "appearance is not checked", "CharacterCreation"),
        new("CharacterCreation:CharacterFacialHairStylesDbcPath", "CharacterFacialHairStyles.dbc", "character creation appearance check", "appearance is not checked", "CharacterCreation"),
        new("Combat:ShapeshiftFormDbcPath", "SpellShapeshiftForm.dbc", "stances and shapeshift forms", "built-in build-5875 table (ShapeshiftFormCatalog.Retail)"),
        new("Creatures:CreatureDisplayInfoDbcPath", "CreatureDisplayInfo.dbc", "creature display scale and model", "default creature geometry", "CreatureDisplay"),
        new("Creatures:CreatureModelDataDbcPath", "CreatureModelData.dbc", "creature display scale and model", "default creature geometry", "CreatureDisplay"),
        new("Creatures:FactionTemplateDbcPath", "FactionTemplate.dbc", "creature hostility", "nobody aggroes on sight unless a catalog is registered"),
        new("Enchanting:SpellItemEnchantmentDbcPath", "SpellItemEnchantment.dbc", "enchanting", Off),
        new("GameObjects:TransportAnimationDbcPath", "TransportAnimation.dbc", "elevator and tram animation", "their progress stays 0"),
        new("ItemRandomProperties:DbcPath", "ItemRandomProperties.dbc", "random item properties (also needs EnchantmentTemplateDumpPath)", "no item gets a random property"),
        new("ItemSets:DbcPath", "ItemSet.dbc", "item set bonuses", "no item set bonuses"),
        new("Items:CharStartOutfitDbcPath", "CharStartOutfit.dbc", "starting outfits", "the SQL starting items only"),
        new("Names:NamesProfanityDbcPath", "NamesProfanity.dbc", "character and pet name rules", "only the SQL reserved names", "Names"),
        new("Names:NamesReservedDbcPath", "NamesReserved.dbc", "character and pet name rules", "only the SQL reserved names", "Names"),
        new("NpcServices:BankBagSlotPricesDbcPath", "BankBagSlotPrices.dbc", "bank slot prices", Off),
        new("NpcServices:DurabilityCostsDbcPath", "DurabilityCosts.dbc", "repair prices", Off, "Repair"),
        new("NpcServices:DurabilityQualityDbcPath", "DurabilityQuality.dbc", "repair prices", Off, "Repair"),
        new("NpcServices:SkillLineAbilityDbcPath", "SkillLineAbility.dbc", "trainer rank prerequisites", Off),
        new("NpcServices:TaxiNodesDbcPath", "TaxiNodes.dbc", "flight masters", "the imported taxi_nodes table"),
        new("NpcServices:TaxiPathDbcPath", "TaxiPath.dbc", "flight masters", "the imported taxi_path table"),
        new("NpcServices:TaxiPathNodeDbcPath", "TaxiPathNode.dbc", "flight paths and transports", "no flight waypoints, no ships"),
        new("Quests:FactionTemplateDbcPath", "FactionTemplate.dbc", "quest giver factions", "unknown NPC factions"),
        new("Reputation:FactionDbcPath", "Faction.dbc", "reputation", "no reputation factions"),
        new("Skills:SkillLineDbcPath", "SkillLine.dbc", "the retail skill system", "the legacy skill stand-ins", "Skills"),
        new("Skills:SkillRaceClassInfoDbcPath", "SkillRaceClassInfo.dbc", "the retail skill system", "the legacy skill stand-ins", "Skills"),
        new("Skills:SkillTiersDbcPath", "SkillTiers.dbc", "the retail skill system", "the legacy skill stand-ins", "Skills"),
        new("Skills:SkillLineAbilityDbcPath", "SkillLineAbility.dbc", "the retail skill system", "the legacy skill stand-ins", "Skills"),
        new("Talents:TalentDbcPath", "Talent.dbc", "talents", "the talent system is inert", "Talents"),
        new("Talents:TalentTabDbcPath", "TalentTab.dbc", "talents", "the talent system is inert", "Talents"),
        new("World:GmCommands:DebugDraw:GameObjectDisplayInfoDbcPath", "GameObjectDisplayInfo.dbc", ".debug vis marker model check", "marker models are not checked against the client"),
        new("World:Chat:ChatChannelsDbcPath", "ChatChannels.dbc", "built-in chat channels", "the six transcribed 1.12.1 channels (English names only)"),
        new("World:Chat:EmotesDbcPath", "Emotes.dbc", "text emote animations", "text emotes are only announced", "Emotes"),
        new("World:Chat:EmotesTextDbcPath", "EmotesText.dbc", "text emote animations", "text emotes are only announced", "Emotes"),
    ];

    /// <summary>The keys that take a directory of DBCs instead of one file; <c>ClientData:DbcDirectory</c> fills an unset one with itself.</summary>
    public static IReadOnlyList<ClientDbcDirectoryConsumer> Directories { get; } =
    [
        new("World:GmCommands:LiveFxDbcDirectory", ".fx id checks and .fx lookup (SoundEntries, ZoneMusic, CinematicSequences, SpellVisualKit, SpellVisualEffectName, WorldStateUI)",
            "ids are sent unchecked and .fx lookup has no client data"),
    ];

    /// <summary>The distinct DBC files the world daemon reads, sorted.</summary>
    public static IReadOnlyList<string> Files { get; } = [.. All.Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
}
