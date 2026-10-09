# Client data: the DBC directory and its validation

Status: lane `claude/client-data` (2026-10-08), extended by `codex/w3-client-data-dbd`. WoW 1.12.1 (5875). Reference: vmangos `src/game/Database/DBCfmt.h` (the format
strings) and `DBCStores.cpp` (`LoadDBCStores`, `LoadDBC`: a file whose field count differs from its format string is reported as
"exist, but have N fields instead M ... Wrong client version DBC file?"). Re-implemented; no code was copied. No client data ships
with the server: the developer extracts the DBFilesClient set from their own client (the full 154-file set the 1.12.1 client
resolves, patch-2.MPQ over patch.MPQ over dbc.MPQ, is described in the `README.txt` beside the extraction).

`tools/codegen/gen_dbc_layouts.py` selects build 1.12.1.5875 blocks from the local WoWDBDefs checkout at commit `e3df370`
(`definitions/*.dbd`, CC BY-SA 4.0) and generates `ClientDbcLayouts.Dbd.g.cs`. The original vmangos `DBCfmt.h`
strings stay authoritative for their files: generation fails if field counts or record sizes disagree. The generated table
adds field names, types, lengths, offsets and COLUMNS foreign keys for all 154 extracted client files. No `.dbd` files ship
in this repository; see `THIRD_PARTY_NOTICES.md`.

The generated integer metadata also preserves `<32>` versus `<u32>` (and packed widths).
`arcane-db dbc dump Map.dbc --dbc-dir <directory>` prints named records without opening the
world database; signed fields such as `ParentMapID` print `-1` for all-one bits, while
unsigned fields retain the unsigned value. The ordinary `arcane-db dbc` validation still
uses the world database for cross references.

## Delivered

| Behaviour | ArcaneCore owner | Reference |
| --- | --- | --- |
| One key, `ClientData:DbcDirectory`: every DBC consumer key that is unset or empty reads `<DbcDirectory>/<File>.dbc`; a key that is set anywhere (appsettings, environment, command line) still wins | `ClientDataStartup.Apply` (adds the filled keys as the last configuration source, before anything binds), `ClientDataReport` | vmangos reads every DBC from one `DataDir`/dbc directory |
| Start-up check of all 154 build-5875 DBCs: WDBC header, file length, field count and record size. vmangos formats and four ArcaneCore reader layouts take precedence; WoWDBDefs covers the other 95. A file nothing reads that is malformed or of another layout is a problem; one that is missing is counted (and warned about) in the directory summary, but is not a problem, so `ClientData:Strict` does not refuse a partial extraction for it. | `ClientDbcInspector`, `ClientDbcLayouts`, `ClientDbcDbdLayouts` | vmangos `DBCFileLoader::Load`, `DBCStores.cpp::LoadDBC` (150-187); WoWDBDefs `definitions/*.dbd` BUILD 1.12.1.5875 |
| One log line per DBC file (category `ArcaneCore.ClientData`): `loaded N records (F fields, source)`, `missing`, `malformed` or `format mismatch: ...`, the path, where it came from (explicit key or DbcDirectory) and the keys it feeds; then a summary of the rest of the directory | `ClientDataReport.Lines`, `ClientDataStartup.Log` | |
| A missing or mismatched file under the directory is not handed to its consumer, which keeps its built-in table or stays off, with a warning; keys a feature needs together (talents, names, emotes, appearance, creature display, repair, skills) are filled all or none | `ClientDataReport.Build` | vmangos refuses to start on a bad DBC (`ASSERT` on `bad_dbc_files`); ArcaneCore warns unless `ClientData:Strict` |
| `ClientData:Strict`: every client data problem (a bad or missing file under the directory, a missing directory, an explicit key whose file is missing or another build's) is a configuration error: `check-config` and the start fail with exit code 78 | `ClientDataConfigChecks` (in `OpsCli.Validate`) | |
| Built-in tables are fallbacks only: with the directory set, the client file wins. Tests compare each built-in table with the client file when `ARCANECORE_TEST_DBC_DIR` is set: the 32 SpellShapeshiftForm rows (`ShapeshiftFormCatalog.Retail`), the six ChatChannels rows (`ChatChannelCatalog.Builtin`), the auction houses (`EconomyOptions.AuctionHouses`) and the team factions (`FactionTeams`, the neutral auctioneer factions) | `ClientDataWorldTests`, `AuctionHouseDbcAgreementTests` | |
| Cross-reference of the world database against the DBCs: spell ids (trainers, item spells, starting spells and action bars, totems, quest spells, `spell_template` rows that are not server-side, proc events, target positions), map ids, area ids, faction template and faction ids, creature, game object and item display ids, area triggers, graveyards, taxi nodes and paths, locks, item sets, skill lines and broadcast emotes; per column the distinct ids, the dangling ids, the rows holding them and up to five examples | `DbcCrossReferences` | |
| Cross-reference of DBC integer foreign keys to extracted DBC `ID` columns (for example AreaTable.ContinentID to Map, Spell.SpellVisualID to SpellVisual), ignoring zero and all-ones unset markers; both validation commands print it after the world cross-reference under its own heading (`clientReferences` in `arcane-db dbc --json`). It is a diagnostic of the client's own data and never makes `arcane-db dbc` exit 5 | `DbcCrossReferences.RunDbc` | WoWDBDefs `definitions/*.dbd` COLUMNS declarations |
| `arcane-db dbc [--dbc-dir <dir>] [--json]`: the whole directory against the layouts, then the cross-references; read-only; exit 5 when a file is malformed or of another layout or a world id dangles (docs/ops/database-upgrade.md) | `DbUpgradeCli.Run.DbcAsync` | |
| `.arcane dbc` (GameMaster): the start-up report in chat. `.arcane dbc validate` (Administrator): the cross-reference, run off the world thread; the summary and at most 40 lines in chat, everything in the server log | `ClientDataCommands` | |

## The consumers

Every per-file key of the world daemon (`ClientDbcConsumers.All`; a test fails when an options class gains a `*DbcPath` key
that is not listed; `ClientDbcConsumers.Directories` holds the `*DbcDirectory` keys, below) and what the feature does without its file:

| Key | File | Without it |
| --- | --- | --- |
| `CharacterCreation:CharSectionsDbcPath`, `CharacterCreation:CharacterFacialHairStylesDbcPath` | CharSections.dbc, CharacterFacialHairStyles.dbc | appearance is not checked |
| `Combat:ShapeshiftFormDbcPath` | SpellShapeshiftForm.dbc | the built-in build-5875 table |
| `Creatures:CreatureDisplayInfoDbcPath`, `Creatures:CreatureModelDataDbcPath` | CreatureDisplayInfo.dbc, CreatureModelData.dbc | default creature geometry |
| `Creatures:FactionTemplateDbcPath`, `Quests:FactionTemplateDbcPath` | FactionTemplate.dbc | nobody aggroes on sight; unknown NPC factions |
| `Enchanting:SpellItemEnchantmentDbcPath` | SpellItemEnchantment.dbc | enchanting off |
| `GameObjects:TransportAnimationDbcPath` | TransportAnimation.dbc | elevator progress stays 0 |
| `ItemRandomProperties:DbcPath` | ItemRandomProperties.dbc | no random properties (also needs `EnchantmentTemplateDumpPath`, a SQL dump, not a DBC) |
| `ItemSets:DbcPath` | ItemSet.dbc | no set bonuses |
| `Items:CharStartOutfitDbcPath` | CharStartOutfit.dbc | SQL starting items only |
| `Names:NamesProfanityDbcPath`, `Names:NamesReservedDbcPath` | NamesProfanity.dbc, NamesReserved.dbc | SQL reserved names only |
| `NpcServices:BankBagSlotPricesDbcPath`, `DurabilityCostsDbcPath`, `DurabilityQualityDbcPath`, `SkillLineAbilityDbcPath`, `TaxiNodesDbcPath`, `TaxiPathDbcPath`, `TaxiPathNodeDbcPath` | the files of the same names | no bank slot prices, repairs or trainer rank checks; the imported taxi tables; no flight waypoints and no ships |
| `Reputation:FactionDbcPath` | Faction.dbc | no reputation factions |
| `Skills:SkillLineDbcPath`, `SkillRaceClassInfoDbcPath`, `SkillTiersDbcPath`, `SkillLineAbilityDbcPath` | the files of the same names | the legacy skill stand-ins |
| `Talents:TalentDbcPath`, `Talents:TalentTabDbcPath` | Talent.dbc, TalentTab.dbc | the talent system is inert |
| `World:Chat:ChatChannelsDbcPath` | ChatChannels.dbc | the six transcribed channels, English names only |
| `World:Chat:EmotesDbcPath`, `World:Chat:EmotesTextDbcPath` | Emotes.dbc, EmotesText.dbc | text emotes are only announced |
| `World:GmCommands:DebugDraw:GameObjectDisplayInfoDbcPath` | GameObjectDisplayInfo.dbc | `.debug vis` marker models are not checked against the client |

One key takes a directory, not a file (`ClientDbcConsumers.Directories`; a test fails when an options class gains a `*DbcDirectory` key that is not listed). When it is unset, `ClientData:DbcDirectory` fills it with itself:

| Key | Reads | Without it |
| --- | --- | --- |
| `World:GmCommands:LiveFxDbcDirectory` | SoundEntries, ZoneMusic, CinematicSequences, SpellVisualKit, SpellVisualEffectName, WorldStateUI (each optional) | `.fx` sends any id unchecked and `.fx lookup` has no client data |

The content importer (`arcane-content-importer`, `tools/content/refresh-world-content.ps1`) reads Map, AreaTable, AreaTrigger,
WorldSafeLocs, TaxiNodes, TaxiPath and Spell into the world database with its own `--dbc`/`-DbcDirectory` argument; the world
daemon then reads those tables, not the files.

## Against the real client set (2026-10-08)

`D:\ArcaneCore-data\client-dbc-5875` (154 files): all 154 have reference layouts and match their field count and record size.
The 59 primary layouts still match (every vmangos format string of build 5875, ItemDisplayInfo's commented-out one, and four ArcaneCore layouts).
The reviewed DBC-to-DBC scan of this extraction checks 173 columns: 151 clean, 22 known client-data-gap groups with 147 distinct absent ids in 291 rows, no unexpected dangling ids and none skipped. The [build-5875 review](../integration/dbc-content-review-20261008.md) records the verdict for every former dangling group and the world-DB rows below.
Examples include AreaTable.ContinentID -> Map (17, 150), AreaTrigger.ContinentID -> Map (24, 28), and
Spell.SpellVisualID -> SpellVisual (clean). These counts are diagnostic: a WoWDBDefs foreign-key annotation can describe
a bitmask or optional link, and some client tables contain ids for content absent from this particular extraction. Three
annotations do not hold for this build and are overridden in the generator (`FOREIGN_OVERRIDES` in
`tools/codegen/gen_dbc_layouts.py`): FactionTemplate.FactionGroup is a mask (5 and 8; vmangos `FactionTemplateEntry`
names the masks `ourMask`, `friendlyMask`, `hostileMask`), and Map.ParentMapID (field 19) holds AreaTable ids (all 23 values,
717 The Stockade, 718, 719, 721, 1337 ..., are AreaTable rows and none is a Map row; the column is now checked against AreaTable and is clean). FootstepTerrainLookup.CreatureFootstepID is a footstep-group key shared with CreatureSoundData.SoundFootstepID, not a SpellVisualEffectName ID.
Before these overrides the scan reported 175 columns, 25 dangling.
With only `ClientData:DbcDirectory` set, all 32 per-file keys and the one directory key are filled and nothing is reported. The cross-reference of the live
world (snapshot `live-w5-r1/after-stop-world.db`, `arcane-db dbc`, exit 5) checked 84 columns: 76 clean, 8 with 15 dangling ids in 67
rows. Three columns are the client's own data: `area_template.MapId` 17 and 150 and `areatrigger_template.MapId` 24 and 28 (imported from the
client's AreaTable and AreaTrigger, which name maps its Map.dbc does not have) and `taxi_nodes.map_id` 131074 (TaxiNodes.dbc row 81,
"Filming"). Five are content to look at: `gameobject_template.Faction` 1660, 1732, 1733, 1735, 1751 (28 rows),
`gameobject_template.DisplayId` 11686, `item_template.display_id` 37829, `item_template.required_skill` 242, and `npc_trainer.reqskill`
10 and 17862 (30 rows; 17862 is a spell id in a skill column).

## Limits

- The layout check validates headers and size; the DBC foreign-key check validates declared integer references to extracted
  DBC `ID` columns. Duplicate ids and other semantic row errors are not checked.
- vmangos loads locale overrides of the string columns from `dbc/<locale>/`; ArcaneCore reads only the one directory.
- `item_template.random_property` is not checked: it names an `item_enchantment_template` group (the dump table the random property
  feature reads), not an ItemRandomProperties.dbc row.
- `spell_template.EffectTriggerSpell*` is not checked: triggered spells may be server-side.
