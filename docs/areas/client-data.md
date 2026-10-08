# Client data: the DBC directory and its validation

Status: lane `claude/client-data` (2026-10-08). WoW 1.12.1 (5875). Reference: vmangos `src/game/Database/DBCfmt.h` (the format
strings) and `DBCStores.cpp` (`LoadDBCStores`, `LoadDBC`: a file whose field count differs from its format string is reported as
"exist, but have N fields instead M ... Wrong client version DBC file?"). Re-implemented; no code was copied. No client data ships
with the server: the developer extracts the DBFilesClient set from their own client (the full 154-file set the 1.12.1 client
resolves, patch-2.MPQ over patch.MPQ over dbc.MPQ, is described in the `README.txt` beside the extraction).

## Delivered

| Behaviour | ArcaneCore owner | Reference |
| --- | --- | --- |
| One key, `ClientData:DbcDirectory`: every DBC consumer key that is unset or empty reads `<DbcDirectory>/<File>.dbc`; a key that is set anywhere (appsettings, environment, command line) still wins | `ClientDataStartup.Apply` (adds the filled keys as the last configuration source, before anything binds), `ClientDataReport` | vmangos reads every DBC from one `DataDir`/dbc directory |
| Start-up check of every DBC the daemon reads: the WDBC header (magic, length = 20 + records x record size + string block) and the field count and record size against the vmangos format string, or the ArcaneCore reader's layout for the four files vmangos takes from its database (Faction, FactionTemplate, CharStartOutfit with its packed 152-byte records, Spell) | `ClientDbcInspector`, `ClientDbcLayouts` | `DBCFileLoader::Load`, `LoadDBC` (DBCStores.cpp:150-187) |
| One log line per DBC file (category `ArcaneCore.ClientData`): `loaded N records (F fields, source)`, `missing`, `malformed` or `format mismatch: ...`, the path, where it came from (explicit key or DbcDirectory) and the keys it feeds; then a summary of the rest of the directory | `ClientDataReport.Lines`, `ClientDataStartup.Log` | |
| A missing or mismatched file under the directory is not handed to its consumer, which keeps its built-in table or stays off, with a warning; keys a feature needs together (talents, names, emotes, appearance, creature display, repair, skills) are filled all or none | `ClientDataReport.Build` | vmangos refuses to start on a bad DBC (`ASSERT` on `bad_dbc_files`); ArcaneCore warns unless `ClientData:Strict` |
| `ClientData:Strict`: every client data problem (a bad or missing file under the directory, a missing directory, an explicit key whose file is missing or another build's) is a configuration error: `check-config` and the start fail with exit code 78 | `ClientDataConfigChecks` (in `OpsCli.Validate`) | |
| Built-in tables are fallbacks only: with the directory set, the client file wins. Tests compare each built-in table with the client file when `ARCANECORE_TEST_DBC_DIR` is set: the 32 SpellShapeshiftForm rows (`ShapeshiftFormCatalog.Retail`), the six ChatChannels rows (`ChatChannelCatalog.Builtin`), the auction houses (`EconomyOptions.AuctionHouses`) and the team factions (`FactionTeams`, the neutral auctioneer factions) | `ClientDataWorldTests`, `AuctionHouseDbcAgreementTests` | |
| Cross-reference of the world database against the DBCs: spell ids (trainers, item spells, starting spells and action bars, totems, quest spells, `spell_template` rows that are not server-side, proc events, target positions), map ids, area ids, faction template and faction ids, creature, game object and item display ids, area triggers, graveyards, taxi nodes and paths, locks, item sets, skill lines and broadcast emotes; per column the distinct ids, the dangling ids, the rows holding them and up to five examples | `DbcCrossReferences` | |
| `arcane-db dbc [--dbc-dir <dir>] [--json]`: the whole directory against the layouts, then the cross-reference; read-only; exit 5 when a file is bad or an id dangles (docs/ops/database-upgrade.md) | `DbUpgradeCli.Run.DbcAsync` | |
| `.arcane dbc` (GameMaster): the start-up report in chat. `.arcane dbc validate` (Administrator): the cross-reference, run off the world thread; the summary and at most 40 lines in chat, everything in the server log | `ClientDataCommands` | |

## The consumers

Every per-file key of the world daemon (`ClientDbcConsumers.All`; a test fails when an options class gains a `*DbcPath` key
that is not listed) and what the feature does without its file:

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

The content importer (`arcane-content-importer`, `tools/content/refresh-world-content.ps1`) reads Map, AreaTable, AreaTrigger,
WorldSafeLocs, TaxiNodes, TaxiPath and Spell into the world database with its own `--dbc`/`-DbcDirectory` argument; the world
daemon then reads those tables, not the files.

## Against the real client set (2026-10-08)

`D:\ArcaneCore-data\client-dbc-5875` (154 files): all 59 files with a reference layout match it (every vmangos format string
of build 5875, ItemDisplayInfo's commented-out one, and the four ArcaneCore layouts); the other 95 have a well-formed header.
With only `ClientData:DbcDirectory` set, all 31 consumer keys are filled and nothing is reported. The cross-reference of the live
world (snapshot `live-w5-r1/after-stop-world.db`, `arcane-db dbc`, exit 5) checked 84 columns: 76 clean, 8 with 15 dangling ids in 67
rows. Three columns are the client's own data: `area_template.MapId` 17 and 150 and `areatrigger_template.MapId` 24 and 28 (imported from the
client's AreaTable and AreaTrigger, which name maps its Map.dbc does not have) and `taxi_nodes.map_id` 131074 (TaxiNodes.dbc row 81,
"Filming"). Five are content to look at: `gameobject_template.Faction` 1660, 1732, 1733, 1735, 1751 (28 rows),
`gameobject_template.DisplayId` 11686, `item_template.display_id` 37829, `item_template.required_skill` 242, and `npc_trainer.reqskill`
10 and 17862 (30 rows; 17862 is a spell id in a skill column).

## Limits

- Only the header is validated: a file of the right layout whose rows are wrong (a duplicate id, an unknown map type) still stops
  its feature when it reads it, as before.
- vmangos loads locale overrides of the string columns from `dbc/<locale>/`; ArcaneCore reads only the one directory.
- `item_template.random_property` is not checked: it names an `item_enchantment_template` group (the dump table the random property
  feature reads), not an ItemRandomProperties.dbc row.
- `spell_template.EffectTriggerSpell*` is not checked: triggered spells may be server-side.
